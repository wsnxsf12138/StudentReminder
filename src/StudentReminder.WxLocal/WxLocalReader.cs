using System.Text.Json;

namespace StudentReminder.WxLocal;

public sealed record WxLocalEnvironment(
    string? InstallPath, string? XwechatRoot, IReadOnlyList<WxAccount> Accounts,
    IReadOnlyList<string> LoggedInWxids, IReadOnlyList<(int Pid, long WorkingSetBytes)> RunningProcesses);

/// <summary>
/// One-stop facade over the WeChat 4.x local reading pipeline:
/// locate account → extract keys from the running process → decrypt databases into a cache
/// directory → read sessions and messages. Each step can also be driven separately.
/// </summary>
public sealed class WxLocalReader
{
    /// <summary>Probe the local environment without touching any data.</summary>
    public static WxLocalEnvironment Describe()
    {
        var processes = new List<(int, long)>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("weixin"))
        {
            using (process)
            {
                try { processes.Add((process.Id, process.WorkingSet64)); }
                catch (System.ComponentModel.Win32Exception) { processes.Add((process.Id, 0)); }
            }
        }
        processes.Sort((left, right) => right.Item2.CompareTo(left.Item2));
        return new(WxPaths.GetInstallPath(), WxPaths.GetXwechatRoot(), WxPaths.ListAccountDirs(),
            WxPaths.ListLoggedInWxids(), processes);
    }

    /// <summary>Extract keys for an account and decrypt all of its databases into the cache directory.</summary>
    public static WxDecryptStats Refresh(WxAccount account, string cacheDir, TextWriter? progress = null,
        string? snapshotRoot = null)
    {
        progress?.WriteLine($"[1/3] 收集 {account.DbStorage} 下的加密库 ...");
        var extractor = new WxKeyExtractor(account.DbStorage);
        extractor.Collect();
        progress?.WriteLine("[2/3] 从 Weixin.exe 进程内存提取密钥（需要微信已登录）...");
        extractor.Extract();
        if (extractor.Keys.Count == 0)
            throw new WxDecryptException("没有提取到任何密钥。请确认微信已登录并以管理员身份运行。");
        progress?.WriteLine($"      已解析 {extractor.Keys.Count} 个 salt 的密钥");
        var keysPath = Path.Combine(cacheDir, "keys.json");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(keysPath, JsonSerializer.Serialize(extractor.ResultPayload()));
        progress?.WriteLine($"      密钥清单已写入 {keysPath}");
        progress?.WriteLine("[3/3] 解密数据库（含 WAL 增量）...");
        return WxDbDecryptor.DecryptAll(extractor.ResultPayload(), account.DbStorage, cacheDir, progress, snapshotRoot);
    }

    /// <summary>Load keys.json produced by <see cref="Refresh"/> and decrypt only (no process scan).</summary>
    public static WxDecryptStats DecryptOnly(WxAccount account, string cacheDir, TextWriter? progress = null)
    {
        var keysPath = Path.Combine(cacheDir, "keys.json");
        if (!File.Exists(keysPath)) throw new WxDecryptException($"找不到 {keysPath}，请先执行密钥提取。");
        var keys = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(keysPath))
            ?? throw new WxDecryptException("keys.json 内容无效。");
        return WxDbDecryptor.DecryptAll(keys, account.DbStorage, cacheDir, progress);
    }

    /// <summary>Open the decrypted cache directory for reading.</summary>
    public static WxMessageReader Open(string cacheDir, string wxid = "") => new(cacheDir, wxid);
}
