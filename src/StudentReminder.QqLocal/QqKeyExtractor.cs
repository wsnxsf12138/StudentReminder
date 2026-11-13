using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace StudentReminder.QqLocal;

public sealed record QqKeyResult(IReadOnlyDictionary<string, string> Keys, string Report);

/// <summary>
/// Extracts QQ NT SQLCipher keys from a running QQ.exe process. The per-account key and
/// salt are laid out in memory as x'&lt;64hex key&gt;&lt;32hex salt&gt;'; because the salt is
/// already known from each database's page 1, the scanner searches for that salt hex string
/// and reads the 64 hex chars immediately before it as the key, then validates against the
/// database page-1 HMAC.
/// </summary>
public sealed class QqKeyExtractor
{
    private sealed record DbItem(string Rel, string Path, string SaltHex, byte[] Page1);

    private readonly string _ntDbDir;
    private readonly List<DbItem> _dbFiles = [];
    private readonly Dictionary<string, List<DbItem>> _saltIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal); // salt hex -> key hex
    private readonly List<string> _log = [];

    public QqKeyExtractor(string ntDbDir)
    {
        if (!Directory.Exists(ntDbDir))
            throw new ArgumentException($"nt_db 目录不存在：{ntDbDir}");
        _ntDbDir = ntDbDir;
    }

    public IReadOnlyDictionary<string, string> Keys => _keys;
    public string Log => string.Join(Environment.NewLine, _log);

    /// <summary>Collect every encrypted database with its salt and first SQLCipher page (after the 1024-byte header).</summary>
    public int Collect()
    {
        foreach (var path in Directory.EnumerateFiles(_ntDbDir, "*.db", SearchOption.AllDirectories))
        {
            try
            {
                if (new FileInfo(path).Length < QqConstants.HeaderSz + QqConstants.PageSz) continue;
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var page1 = new byte[QqConstants.PageSz];
                file.Position = QqConstants.HeaderSz;
                if (file.Read(page1) < QqConstants.PageSz) continue;
                var saltHex = Convert.ToHexString(page1, 0, QqConstants.SaltSz).ToLowerInvariant();
                var item = new DbItem(Path.GetRelativePath(_ntDbDir, path), path, saltHex, page1);
                _dbFiles.Add(item);
                if (!_saltIndex.TryGetValue(saltHex, out var list)) _saltIndex[saltHex] = list = [];
                list.Add(item);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _log.Add($"共发现 {_dbFiles.Count} 个加密数据库，{_saltIndex.Count} 个不同 salt");
        return _dbFiles.Count;
    }

    /// <summary>Validate a raw 32-byte key against databases whose salt matches <paramref name="saltHint"/>.</summary>
    public int TryKey(string keyHex, string? saltHint)
    {
        List<string> targets;
        if (saltHint is not null && _saltIndex.ContainsKey(saltHint) && !_keys.ContainsKey(saltHint))
            targets = [saltHint];
        else
            targets = [.. _saltIndex.Keys.Where(salt => !_keys.ContainsKey(salt))];
        byte[] key;
        try { key = Convert.FromHexString(keyHex); }
        catch (FormatException) { return 0; }
        if (!QqCrypto.LooksLikeKey(key)) return 0;
        var hits = 0;
        foreach (var salt in targets)
        {
            var page1 = _saltIndex[salt][0].Page1;
            if (QqCrypto.VerifyEncKey(key, page1))
            {
                _keys[salt] = keyHex.ToLowerInvariant();
                hits++;
            }
        }
        return hits;
    }

    private const string PreLoginKey = "BD156D6710D54D8782F4"; // fixed key for global (non-account) databases

    /// <summary>Derive and validate an ASCII passphrase against every unresolved salt.</summary>
    public int TryPassphrase(string passphrase) => TryPassphrase(Encoding.ASCII.GetBytes(passphrase));

    private int TryPassphrase(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        var hits = 0;
        foreach (var salt in _saltIndex.Keys.Where(salt => !_keys.ContainsKey(salt)).ToList())
        {
            var page1 = _saltIndex[salt][0].Page1;
            var derived = QqCrypto.DeriveEncKey(bytes, page1.AsSpan(0, QqConstants.SaltSz).ToArray());
            if (QqCrypto.VerifyEncKey(derived, page1))
            {
                _keys[salt] = Convert.ToHexString(derived).ToLowerInvariant();
                hits++;
            }
        }
        return hits;
    }

    /// <summary>Scan one process for x'&lt;key&gt;&lt;salt&gt;' literals, keyed by each database's known salt.</summary>
    public int ScanProcess(int pid)
    {
        using var memory = QqProcessMemory.Open(pid);
        if (memory is null)
        {
            _log.Add($"OpenProcess 失败（PID={pid}），请尝试以管理员身份运行");
            return 0;
        }
        var remaining = _saltIndex.Keys.Where(salt => !_keys.ContainsKey(salt)).ToList();
        var hits = 0;
        long total = 0;
        foreach (var (_, data) in memory.ReadChunks(overlap: 100))
        {
            total += data.Length;
            if (remaining.Count == 0) break;
            for (var i = 0; i < remaining.Count;)
            {
                var keyHex = SearchKeyForSalt(data, remaining[i]);
                if (keyHex is not null)
                {
                    hits += TryKey(keyHex, remaining[i]);
                    remaining.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }
        _log.Add($"  扫描 {total / 1048576} MB");
        return hits;
    }

    /// <summary>Find x'&lt;64hex&gt;&lt;salt&gt;' in memory and return the 64-hex key before the salt.</summary>
    private static string? SearchKeyForSalt(ReadOnlySpan<byte> data, string saltHex)
    {
        var lower = Encoding.ASCII.GetBytes(saltHex);
        var upper = Encoding.ASCII.GetBytes(saltHex.ToUpperInvariant());
        foreach (var needle in new[] { lower, upper })
        {
            var off = 0;
            while (off + needle.Length <= data.Length)
            {
                var found = data[off..].IndexOf(needle);
                if (found < 0) break;
                var idx = off + found;
                if (idx >= 66 && idx + 33 <= data.Length &&
                    (data[idx - 66] == (byte)'x' || data[idx - 66] == (byte)'X') &&
                    data[idx - 65] == (byte)'\'' && data[idx + 32] == (byte)'\'')
                {
                    var allHex = true;
                    for (var j = idx - 64; j < idx; j++)
                        if (!IsHex(data[j])) { allHex = false; break; }
                    if (allHex)
                        return Encoding.ASCII.GetString(data.Slice(idx - 64, 64)).ToLowerInvariant();
                }
                off = idx + 1;
            }
        }
        return null;
    }

    private static bool IsHex(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    /// <summary>Run the full extraction pipeline over every QQ.exe process, largest working set first.</summary>
    public QqKeyResult Extract(string? passphrase = null)
    {
        if (_dbFiles.Count == 0) Collect();
        if (_saltIndex.Count == 0)
            throw new InvalidOperationException("未在 nt_db 中发现任何加密数据库。");
        QqProcessPrivilege.EnableDebugPrivilege();
        var processes = new List<(int Pid, long WorkingSet)>();
        foreach (var process in Process.GetProcessesByName("QQ"))
        {
            using (process)
            {
                long workingSet;
                try { workingSet = process.WorkingSet64; }
                catch (Win32Exception) { workingSet = 0; }
                processes.Add((process.Id, workingSet));
            }
        }
        processes.Sort((left, right) => right.WorkingSet.CompareTo(left.WorkingSet));
        if (processes.Count == 0)
            throw new InvalidOperationException("未检测到 QQ 进程，请先启动并登录 QQ。");
        _log.Add($"检测到 {processes.Count} 个 QQ.exe 进程");
        foreach (var (pid, _) in processes)
        {
            if (_keys.Count == _saltIndex.Count) break;
            _log.Add($"扫描进程 PID={pid} ...");
            var hits = ScanProcess(pid);
            _log.Add($"  命中 {hits} 个密钥（已解析 {_keys.Count}/{_saltIndex.Count}）");
        }
        if (_keys.Count != _saltIndex.Count && !string.IsNullOrEmpty(passphrase))
        {
            _log.Add("尝试手动口令派生 ...");
            var hits = TryPassphrase(passphrase);
            _log.Add($"  口令派生命中 {hits}");
        }
        if (_keys.Count != _saltIndex.Count)
        {
            _log.Add("尝试固定预登录密钥（全局库）...");
            var hits = TryPassphrase(Encoding.ASCII.GetBytes(PreLoginKey));
            _log.Add($"  预登录密钥命中 {hits}");
        }
        return new(_keys, Log);
    }

    /// <summary>Keys payload: relative db path -> { enc_key, salt }, matching the decryptor input format.</summary>
    public Dictionary<string, Dictionary<string, string>> ResultPayload()
    {
        var payload = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _dbFiles)
            if (_keys.TryGetValue(item.SaltHex, out var keyHex))
                payload[item.Rel] = new Dictionary<string, string>
                {
                    ["enc_key"] = keyHex,
                    ["salt"] = item.SaltHex,
                };
        return payload;
    }
}
