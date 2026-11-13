using System.Text.Json;

namespace StudentReminder.QqLocal;

public sealed record QqLocalEnvironment(
    IReadOnlyList<QqAccount> Accounts, IReadOnlyList<(int Pid, long WorkingSetBytes)> RunningProcesses);

/// <summary>
/// One-stop facade over the QQ NT local reading pipeline: locate account → extract keys from
/// the running QQ process → decrypt databases into a cache directory → read conversations and
/// messages. Each step can also be driven separately.
/// </summary>
public sealed class QqLocalReader
{
    /// <summary>Probe the local environment without touching any data.</summary>
    public static QqLocalEnvironment Describe()
    {
        var processes = new List<(int, long)>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("QQ"))
        {
            using (process)
            {
                try { processes.Add((process.Id, process.WorkingSet64)); }
                catch (System.ComponentModel.Win32Exception) { processes.Add((process.Id, 0)); }
            }
        }
        processes.Sort((left, right) => right.Item2.CompareTo(left.Item2));
        return new(QqPaths.ListAccounts(), processes);
    }

    /// <summary>Extract keys for an account and decrypt all of its databases into the cache directory.</summary>
    public static QqDecryptStats Refresh(QqAccount account, string cacheDir, string? passphrase = null,
        TextWriter? progress = null, string? snapshotRoot = null)
    {
        progress?.WriteLine($"[1/3] 收集 {account.NtDbDir} 下的加密库 ...");
        var extractor = new QqKeyExtractor(account.NtDbDir);
        var collected = extractor.Collect();
        progress?.WriteLine($"      共发现 {collected} 个加密数据库");
        progress?.WriteLine("[2/3] 从 QQ.exe 进程内存提取密钥（需要 QQ 已登录）...");
        extractor.Extract(passphrase);
        progress?.Write(extractor.Log);
        progress?.WriteLine();
        if (extractor.Keys.Count == 0)
            throw new QqDecryptException(
                "没有提取到任何密钥。QQ NT 只会把「当前登录中的账号」的密钥载入进程内存：请确认目标账号 " +
                $"{account.Qq} 已在 QQ 客户端登录（多账号时只对已登录的账号提钥），并以管理员身份重试；" +
                "若仍失败可用 --key 手动提供 16 字节口令。");
        progress?.WriteLine($"      已解析 {extractor.Keys.Count} 个 salt 的密钥");
        var keysPath = Path.Combine(cacheDir, "keys.json");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(keysPath, JsonSerializer.Serialize(extractor.ResultPayload()));
        progress?.WriteLine($"      密钥清单已写入 {keysPath}");
        progress?.WriteLine("[3/3] 解密数据库（含 WAL 增量）...");
        return QqDbDecryptor.DecryptAll(extractor.ResultPayload(), account.NtDbDir, cacheDir, progress, snapshotRoot);
    }

    /// <summary>Load keys.json produced by <see cref="Refresh"/> and decrypt only (no process scan).</summary>
    public static QqDecryptStats DecryptOnly(QqAccount account, string cacheDir, TextWriter? progress = null)
    {
        var keysPath = Path.Combine(cacheDir, "keys.json");
        if (!File.Exists(keysPath)) throw new QqDecryptException($"找不到 {keysPath}，请先执行密钥提取。");
        var keys = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(keysPath))
            ?? throw new QqDecryptException("keys.json 内容无效。");
        return QqDbDecryptor.DecryptAll(keys, account.NtDbDir, cacheDir, progress);
    }

    /// <summary>Open the decrypted cache directory for reading.</summary>
    public static QqMessageReader Open(string cacheDir) => new(cacheDir);
}
