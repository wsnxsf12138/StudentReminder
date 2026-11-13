using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StudentReminder.WxLocal;

public sealed record WxKeyResult(IReadOnlyDictionary<string, string> Keys, string Report);

/// <summary>
/// Extracts SQLCipher database keys from a running Weixin.exe process.
/// Main path (4.1.11+): locate the WCDB Config.Cipher object in memory, read its
/// XOR-masked key blob, recover x'&lt;key&gt;&lt;salt&gt;' literals and verify them
/// against page 1 HMAC of every database. Fallbacks: 4.0.x plaintext literals,
/// then expensive per-database passphrase derivation.
/// </summary>
public sealed partial class WxKeyExtractor
{
    private sealed record DbItem(string Rel, string Path, string SaltHex, byte[] Page1);

    [GeneratedRegex(@"[xX]'([0-9a-fA-F]{64,192})'")]
    private static partial Regex HexLiteralPattern();

    private const ulong MaxUserAddress = 0x0000_8000_0000_0000UL;

    private readonly string _dbStorage;
    private readonly List<DbItem> _dbFiles = [];
    private readonly Dictionary<string, List<DbItem>> _saltIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal); // salt hex -> key hex
    private readonly List<string> _log = [];
    private int _passphraseAttempts;

    public WxKeyExtractor(string dbStorage)
    {
        if (!Directory.Exists(dbStorage))
            throw new ArgumentException($"db_storage 目录不存在：{dbStorage}");
        _dbStorage = dbStorage;
    }

    public IReadOnlyDictionary<string, string> Keys => _keys;
    public string Log => string.Join(Environment.NewLine, _log);

    /// <summary>Collect every encrypted database under db_storage with its salt and first page.</summary>
    public int Collect()
    {
        foreach (var path in Directory.EnumerateFiles(_dbStorage, "*.db", SearchOption.AllDirectories))
        {
            try
            {
                if (new FileInfo(path).Length < WxConstants.PageSz) continue;
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var page1 = new byte[WxConstants.PageSz];
                if (file.Read(page1) < WxConstants.PageSz) continue;
                var saltHex = Convert.ToHexString(page1, 0, WxConstants.SaltSz).ToLowerInvariant();
                var item = new DbItem(Path.GetRelativePath(_dbStorage, path), path, saltHex, page1);
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

    private int TryKey(string keyHex, string? saltHint, bool allowPassphrase = false)
    {
        List<string> targets;
        if (saltHint is not null && _saltIndex.ContainsKey(saltHint) && !_keys.ContainsKey(saltHint))
            targets = [saltHint];
        else
            targets = [.. _saltIndex.Keys.Where(salt => !_keys.ContainsKey(salt))];
        byte[] key;
        try { key = Convert.FromHexString(keyHex); }
        catch (FormatException) { return 0; }
        if (!WxCrypto.LooksLikeKey(key)) return 0;
        var hits = 0;
        foreach (var salt in targets)
        {
            var page1 = _saltIndex[salt][0].Page1;
            if (WxCrypto.VerifyEncKey(key, page1))
            {
                _keys[salt] = keyHex.ToLowerInvariant();
                hits++;
                continue;
            }
            if (allowPassphrase && _passphraseAttempts < WxConstants.MaxPassphraseAttempts)
            {
                _passphraseAttempts++;
                var derived = WxCrypto.DeriveEncKey(key, page1.AsSpan(0, WxConstants.SaltSz).ToArray());
                if (WxCrypto.VerifyEncKey(derived, page1))
                {
                    _keys[salt] = Convert.ToHexString(derived).ToLowerInvariant();
                    hits++;
                }
            }
        }
        return hits;
    }

    /// <summary>Scan one process through the Config.Cipher object layout.</summary>
    public int ScanProcess(int pid)
    {
        using var memory = WxProcessMemory.Open(pid);
        if (memory is null)
        {
            _log.Add($"OpenProcess 失败（PID={pid}），请尝试以管理员身份运行");
            return 0;
        }
        var needleAddresses = FindNeedleAddresses(memory);
        if (needleAddresses.Count == 0) return 0;
        var patterns = needleAddresses.Select(address =>
        {
            var pattern = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(pattern, address);
            BinaryPrimitives.WriteUInt64LittleEndian(pattern.AsSpan(8), (ulong)WxConstants.CipherNeedle.Length);
            return pattern;
        }).ToList();
        return ChaseCipherObjects(memory, patterns, needleAddresses, allowPassphrase: false);
    }

    /// <summary>Fallback for 4.0.x: scan memory for plaintext x'key+salt' literals.</summary>
    public int ScanLegacy(int pid)
    {
        using var memory = WxProcessMemory.Open(pid);
        if (memory is null) return 0;
        var hits = 0;
        foreach (var (_, data) in memory.ReadChunks(overlap: 200))
        {
            if (_keys.Count == _saltIndex.Count) break;
            foreach (var match in HexLiteralPattern().Matches(Encoding.Latin1.GetString(data)).Cast<Match>())
            {
                var run = match.Groups[1].Value.ToLowerInvariant();
                if (run.Length < 96) continue;
                hits += TryKey(run[..64], run.Substring(64, 32));
            }
        }
        return hits;
    }

    /// <summary>Fallback for 4.1+ builds that only keep a passphrase in memory (slow PBKDF2 per database).</summary>
    public int ScanPassphrase(int pid)
    {
        using var memory = WxProcessMemory.Open(pid);
        if (memory is null) return 0;
        var needleAddresses = FindNeedleAddresses(memory);
        if (needleAddresses.Count == 0) return 0;
        var patterns = needleAddresses.Select(address =>
        {
            var pattern = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(pattern, address);
            BinaryPrimitives.WriteUInt64LittleEndian(pattern.AsSpan(8), (ulong)WxConstants.CipherNeedle.Length);
            return pattern;
        }).ToList();
        return ChaseCipherObjects(memory, patterns, needleAddresses, allowPassphrase: true);
    }

    /// <summary>Run the full extraction pipeline over every Weixin.exe process.</summary>
    public WxKeyResult Extract()
    {
        if (_dbFiles.Count == 0) Collect();
        WxProcessPrivilege.EnableDebugPrivilege();
        var processes = new List<(int Pid, long WorkingSet)>();
        foreach (var process in Process.GetProcessesByName("weixin"))
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
            throw new InvalidOperationException("未检测到微信进程 Weixin.exe，请先启动并登录微信。");
        _log.Add($"检测到 {processes.Count} 个 Weixin.exe 进程");
        foreach (var (pid, _) in processes)
        {
            if (_keys.Count == _saltIndex.Count) break;
            _log.Add($"扫描进程 PID={pid} ...");
            var hits = ScanProcess(pid);
            _log.Add($"  Config.Cipher 命中 {hits} 个密钥（已解析 {_keys.Count}/{_saltIndex.Count}）");
        }
        if (_keys.Count != _saltIndex.Count)
        {
            _log.Add("尝试 4.0.x 兼容路径（明文 raw key 扫描）...");
            foreach (var (pid, _) in processes)
            {
                if (_keys.Count == _saltIndex.Count) break;
                var hits = ScanLegacy(pid);
                if (hits > 0) _log.Add($"  PID={pid} 命中 {hits}");
            }
        }
        if (_keys.Count == 0)
        {
            _log.Add("尝试 passphrase 路径（较慢）...");
            foreach (var (pid, _) in processes)
            {
                if (_keys.Count > 0) break;
                var hits = ScanPassphrase(pid);
                if (hits > 0) _log.Add($"  PID={pid} 用 passphrase 派生命中 {hits}");
            }
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

    /// <summary>XOR-decode a Config.Cipher blob and slide-window (key, salt) candidates out of x'...' literals.</summary>
    public static IReadOnlyList<(string KeyHex, string? SaltHex)> CandidatesFromConfigBlob(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length > WxConstants.ConfigBlobMax) return [];
        var decoded = new byte[blob.Length];
        for (int i = 0; i < blob.Length; i++)
            decoded[i] = (byte)(blob[i] ^ WxConstants.CipherXorMask[i % WxConstants.CipherXorMask.Length]);
        var results = new List<(string, string?)>();
        foreach (var match in HexLiteralPattern().Matches(Encoding.Latin1.GetString(decoded)).Cast<Match>())
        {
            var run = match.Groups[1].Value.ToLowerInvariant();
            var starts = new List<int> { 0 };
            if (run.Length > 96)
            {
                for (int start = 0; start <= run.Length - 63; start += 32) starts.Add(start);
                starts.Add(run.Length - 64);
            }
            foreach (var start in starts.Distinct())
            {
                if (start < 0 || start + 64 > run.Length) continue;
                var keyHex = run.Substring(start, 64);
                try
                {
                    if (!WxCrypto.LooksLikeKey(Convert.FromHexString(keyHex))) continue;
                }
                catch (FormatException) { continue; }
                var salt = start + 96 <= run.Length ? run.Substring(start + 64, 32) : null;
                results.Add((keyHex, salt));
            }
        }
        return results;
    }

    private HashSet<ulong> FindNeedleAddresses(WxProcessMemory memory)
    {
        var addresses = new HashSet<ulong>();
        foreach (var (baseAddress, data) in memory.ReadChunks(overlap: WxConstants.CipherNeedle.Length - 1))
            FindAll(data, WxConstants.CipherNeedle, offset => addresses.Add(baseAddress + (ulong)offset));
        return addresses;
    }

    private int ChaseCipherObjects(WxProcessMemory memory, List<byte[]> patterns, HashSet<ulong> needleAddresses,
        bool allowPassphrase)
    {
        var hits = 0;
        foreach (var (baseAddress, data) in memory.ReadChunks(overlap: 0x80))
        {
            if (!allowPassphrase && _keys.Count == _saltIndex.Count) break;
            if (allowPassphrase && (_keys.Count > 0 || _passphraseAttempts >= WxConstants.MaxPassphraseAttempts)) break;
            foreach (var pattern in patterns)
                FindAll(data, pattern, offset =>
                {
                    if (!allowPassphrase && _keys.Count == _saltIndex.Count) return;
                    if (allowPassphrase && _keys.Count > 0) return;
                    var node = memory.Read(baseAddress + (ulong)offset - 0x10, 0x50);
                    if (node is not { Length: >= 0x40 }) return;
                    if (!needleAddresses.Contains(BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x10)))) return;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x18)) != (ulong)WxConstants.CipherNeedle.Length) return;
                    var config = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x28));
                    if (config is < 0x10000 or >= MaxUserAddress) return;
                    var blobInfo = memory.Read(config + 0x88, 0x28);
                    if (blobInfo is not { Length: >= 0x18 }) return;
                    var dataPtr = BinaryPrimitives.ReadUInt64LittleEndian(blobInfo.AsSpan(0x8));
                    var dataLen = BinaryPrimitives.ReadUInt64LittleEndian(blobInfo.AsSpan(0x10));
                    if (dataLen is 0 or > (ulong)WxConstants.ConfigBlobMax) return;
                    if (dataPtr is < 0x10000 or >= MaxUserAddress) return;
                    var blob = memory.Read(dataPtr, (int)dataLen);
                    if (blob is null || blob.Length != (int)dataLen) return;
                    foreach (var (keyHex, saltHex) in CandidatesFromConfigBlob(blob))
                        hits += TryKey(keyHex, saltHex, allowPassphrase);
                });
        }
        return hits;
    }

    private static void FindAll(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle, Action<int> found)
    {
        int consumed = 0;
        while (data.Length > 0)
        {
            var index = data.IndexOf(needle);
            if (index < 0) break;
            found(consumed + index);
            consumed += index + 1;
            data = data[(index + 1)..];
        }
    }
}
