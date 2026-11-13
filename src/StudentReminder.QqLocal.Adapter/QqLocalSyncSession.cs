using System.Collections.Concurrent;
using System.Text.Json;
using StudentReminder.Ingestion;

namespace StudentReminder.QqLocal.Adapter;

/// <summary>
/// Owns the QqLocal cache lifecycle for one source: locate the account, extract keys and
/// fully decrypt every database on first run, then apply only the WAL delta on later runs
/// so newly arrived messages surface in near real time. The WAL cursor is persisted in a
/// sidecar <c>wal-cursor.json</c> next to the decrypted shadow databases.
/// </summary>
public sealed class QqLocalSyncSession
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly int _lookbackDays;
    private readonly string _cacheDir;
    private readonly string _snapshotRoot;
    private Dictionary<string, QqWalState?> _walStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshed;

    public QqLocalSyncSession(QqAccount account, int lookbackDays, DirectReadCache? cache = null)
    {
        Account = account;
        _lookbackDays = lookbackDays;
        var platformRoot = (cache ?? new DirectReadCache(DirectReadCache.DefaultDataDirectory)).QqDirectory;
        _cacheDir = Path.Combine(platformRoot, account.Qq);
        _snapshotRoot = Path.Combine(platformRoot, ".snapshots");
    }

    public string CacheDir => _cacheDir;

    private string KeysPath => Path.Combine(_cacheDir, "keys.json");
    private string WalCursorPath => Path.Combine(_cacheDir, "wal-cursor.json");

    public QqAccount Account { get; }

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
                    QqLocalReader.Refresh(account, _cacheDir, snapshotRoot: _snapshotRoot);
                    _walStates = new Dictionary<string, QqWalState?>(StringComparer.OrdinalIgnoreCase);
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
            // "未检测到 QQ 进程" — QQ is not running.
            throw new SourceFailureException("offline");
        }
        catch (QqDecryptException)
        {
            // Key extraction failed (e.g. account not logged in or not elevated) or decryption failed.
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
        _walStates = new Dictionary<string, QqWalState?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(WalCursorPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, QqWalState?>>(File.ReadAllText(WalCursorPath));
            if (loaded is not null) _walStates = new Dictionary<string, QqWalState?>(loaded, StringComparer.OrdinalIgnoreCase);
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

    private void ApplyWalDeltas(QqAccount account)
    {
        var keys = LoadKeys();
        var tempDir = Path.Combine(_snapshotRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (rel, info) in keys)
            {
                if (!info.TryGetValue("enc_key", out var keyHex)) continue;
                var src = Path.Combine(account.NtDbDir, rel);
                var shadow = Path.Combine(_cacheDir, rel);
                if (!File.Exists(src) || !File.Exists(shadow)) continue;
                var encKey = Convert.FromHexString(keyHex);
                var wal = src + "-wal";
                var state = _walStates.TryGetValue(rel, out var existing) ? existing : null;
                var result = QqDbDecryptor.ApplyWalDelta(shadow, wal, encKey, state);
                if (result.Status == QqWalDeltaStatus.NeedsFullRefresh)
                {
                    // WAL generation reset (checkpoint): re-snapshot and fully decrypt this database.
                    var snap = QqDbDecryptor.Snapshot(src, tempDir);
                    QqDbDecryptor.DecryptFile(snap, shadow, encKey);
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

    public QqMessageReader OpenReader() => QqLocalReader.Open(_cacheDir);

    public int LookbackDays => _lookbackDays;
}
