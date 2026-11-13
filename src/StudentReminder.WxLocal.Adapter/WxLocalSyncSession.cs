using System.Collections.Concurrent;
using System.Text.Json;
using StudentReminder.Ingestion;

namespace StudentReminder.WxLocal.Adapter;

/// <summary>
/// Owns the WxLocal cache lifecycle for one source: locate the account, extract keys and
/// fully decrypt every database on first run, then apply only the WAL delta on later runs
/// so newly arrived messages surface in near real time. The WAL cursor is persisted in a
/// sidecar <c>wal-cursor.json</c> next to the decrypted shadow databases.
/// </summary>
public sealed class WxLocalSyncSession
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly int _lookbackDays;
    private readonly string _cacheDir;
    private readonly string _snapshotRoot;
    private Dictionary<string, WxWalState?> _walStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshed;

    public WxLocalSyncSession(WxAccount account, int lookbackDays, DirectReadCache? cache = null)
    {
        Account = account;
        _lookbackDays = lookbackDays;
        var platformRoot = (cache ?? new DirectReadCache(DirectReadCache.DefaultDataDirectory)).WechatDirectory;
        _cacheDir = Path.Combine(platformRoot, account.Wxid);
        _snapshotRoot = Path.Combine(platformRoot, ".snapshots");
    }

    public string CacheDir => _cacheDir;

    private string KeysPath => Path.Combine(_cacheDir, "keys.json");
    private string WalCursorPath => Path.Combine(_cacheDir, "wal-cursor.json");

    public WxAccount Account { get; }

    public bool HasDecryptedCache =>
        File.Exists(KeysPath) && Directory.EnumerateFiles(_cacheDir, "*.db", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Bring the decrypted shadow databases up to date: full extraction + decryption on
    /// the first run, then incremental WAL deltas. Idempotent within one session instance.
    /// </summary>
    public void EnsureRefreshed()
    {
        if (_refreshed) return;
        try
        {
            var account = Account;
            Directory.CreateDirectory(_cacheDir);
            var gate = RefreshLocks.GetOrAdd(_cacheDir, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                if (!HasDecryptedCache)
                {
                    WxLocalReader.Refresh(account, _cacheDir, snapshotRoot: _snapshotRoot);
                    _walStates = new Dictionary<string, WxWalState?>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    LoadWalStates();
                    ApplyWalDeltas(account);
                    SaveWalStates();
                }
            }
            finally
            {
                gate.Release();
            }
            _refreshed = true;
        }
        catch (SourceFailureException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // "未检测到微信进程 Weixin.exe" — WeChat is not running.
            throw new SourceFailureException("offline");
        }
        catch (WxDecryptException)
        {
            // Key extraction failed (e.g. not elevated) or decryption failed.
            throw new SourceFailureException("database_not_ready");
        }
        catch (IOException)
        {
            throw new SourceFailureException("backoff");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SourceFailureException("database_not_ready");
        }
    }

    private void LoadWalStates()
    {
        _walStates = new Dictionary<string, WxWalState?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(WalCursorPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, WxWalState?>>(File.ReadAllText(WalCursorPath));
            if (loaded is not null) _walStates = new Dictionary<string, WxWalState?>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Corrupt cursor file: start fresh; the next pass re-applies from the WAL header.
        }
    }

    private void SaveWalStates()
    {
        File.WriteAllText(WalCursorPath, JsonSerializer.Serialize(_walStates));
    }

    private void ApplyWalDeltas(WxAccount account)
    {
        var keys = LoadKeys();
        var tempDir = Path.Combine(_snapshotRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (rel, info) in keys)
            {
                if (!info.TryGetValue("enc_key", out var keyHex)) continue;
                var src = Path.Combine(account.DbStorage, rel);
                var shadow = Path.Combine(_cacheDir, rel);
                if (!File.Exists(src) || !File.Exists(shadow)) continue;
                var encKey = Convert.FromHexString(keyHex);
                var wal = src + "-wal";
                var state = _walStates.TryGetValue(rel, out var existing) ? existing : null;
                var result = WxDbDecryptor.ApplyWalDelta(shadow, wal, encKey, state);
                if (result.Status == WxWalDeltaStatus.NeedsFullRefresh)
                {
                    // WAL generation reset (checkpoint): re-snapshot and fully decrypt this database.
                    var snap = WxDbDecryptor.Snapshot(src, tempDir);
                    WxDbDecryptor.DecryptFile(snap, shadow, encKey);
                    _walStates[rel] = null;
                }
                else if (result.State is not null)
                {
                    _walStates[rel] = result.State;
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, Dictionary<string, string>> LoadKeys()
    {
        var json = File.ReadAllText(KeysPath);
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
            ?? throw new SourceFailureException("database_not_ready");
    }

    public WxMessageReader OpenReader() => new(_cacheDir, Account.Wxid);

    public int LookbackDays => _lookbackDays;
}
