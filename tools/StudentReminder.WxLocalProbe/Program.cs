using System.Text;
using System.Text.Json;
using StudentReminder.WxLocal;

if (args.Length == 0)
{
    Console.WriteLine("""
        StudentReminder.WxLocalProbe — 微信 4.x 本地聊天记录读取探针

        用法：
          info                                  查看微信安装/数据目录/进程环境
          refresh [--wxid ID] [--cache DIR]     提取密钥并解密全部数据库到缓存目录
          decrypt-only [--wxid ID] [--cache DIR] 用已保存的 keys.json 重新解密（不扫进程）
          sessions [--cache DIR]                列出会话（需先 refresh）
          export <会话ID> [--cache DIR] [--out FILE] [--days N] [--wxid ID]
                                                导出该会话为应用可导入的单群 JSON
          tail <会话ID> [--seq N] [--id N] [--cache DIR] [--wxid ID]
                                                增量解密 WAL 后，读取游标之后的新消息

        默认缓存目录：%LOCALAPPDATA%\StudentReminder\wxlocal
        需要微信 4.x 已登录运行；密钥提取建议以管理员身份运行本工具。
        """);
    return 0;
}

Console.OutputEncoding = new UTF8Encoding(false);
var command = args[0].ToLowerInvariant();
var options = ParseOptions(args);
var cacheDir = options.TryGetValue("--cache", out var cache)
    ? Path.GetFullPath(cache)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StudentReminder", "wxlocal");

try
{
    return command switch
    {
        "info" => RunInfo(),
        "refresh" => RunRefresh(),
        "decrypt-only" => RunDecryptOnly(),
        "sessions" => RunSessions(),
        "export" => RunExport(),
        "tail" => RunTail(),
        _ => Unknown(command),
    };
}
catch (Exception error)
{
    Console.Error.WriteLine("[错误] " + error.Message);
    return 2;
}

int RunInfo()
{
    var env = WxLocalReader.Describe();
    Console.WriteLine($"微信安装目录 : {env.InstallPath ?? "未检测到"}");
    Console.WriteLine($"数据根目录   : {env.XwechatRoot ?? "未检测到（请确认微信 4.x 已在本机使用过）"}");
    Console.WriteLine($"已登录账号   : {(env.LoggedInWxids.Count > 0 ? string.Join(", ", env.LoggedInWxids) : "无")}");
    Console.WriteLine($"运行进程     : {(env.RunningProcesses.Count > 0
        ? string.Join(", ", env.RunningProcesses.Select(p => $"PID={p.Pid}({p.WorkingSetBytes / 1024 / 1024}MB)")) : "微信未运行")}");
    foreach (var account in env.Accounts)
        Console.WriteLine($"账号目录     {account.DirName}  ->  {account.DbStorage}");
    if (env.Accounts.Count == 0) Console.WriteLine("未找到账号目录。请先在微信中登录，并确认“设置 → 文件管理”中的数据位置。");
    return 0;
}

int RunRefresh()
{
    var account = RequireAccount();
    Console.WriteLine($"账号：{account.DirName}");
    var stats = WxLocalReader.Refresh(account, cacheDir, Console.Out);
    Console.WriteLine($"解密完成：成功 {stats.Ok}，失败 {stats.Failed}，跳过 {stats.Skipped}");
    Console.WriteLine($"缓存目录：{cacheDir}");
    return stats.Failed > 0 && stats.Ok == 0 ? 2 : 0;
}

int RunDecryptOnly()
{
    var account = RequireAccount();
    var stats = WxLocalReader.DecryptOnly(account, cacheDir, Console.Out);
    Console.WriteLine($"解密完成：成功 {stats.Ok}，失败 {stats.Failed}，跳过 {stats.Skipped}");
    return stats.Failed > 0 && stats.Ok == 0 ? 2 : 0;
}

int RunSessions()
{
    using var reader = RequireReader(out var wxid);
    var sessions = reader.ListSessions();
    Console.WriteLine($"共 {sessions.Count} 个会话：");
    foreach (var session in sessions.Take(200))
        Console.WriteLine($"{ DateTimeOffset.FromUnixTimeSeconds(session.LastTime).ToLocalTime():yyyy-MM-dd HH:mm}" +
            $"  [{session.Category}] {session.Display,-24} {session.Username}  未读 {session.Unread}");
    return 0;
}

int RunExport()
{
    if (!options.TryGetValue("session", out var session) && args.Length > 1 && !args[1].StartsWith("--"))
        session = args[1];
    if (string.IsNullOrEmpty(session))
    {
        Console.Error.WriteLine("用法：export <会话ID> [--out FILE] [--days N]");
        return 2;
    }
    using var reader = RequireReader(out var wxid);
    long? start = null;
    if (options.TryGetValue("--days", out var days) && int.TryParse(days, out var daysValue))
        start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - daysValue * 86400L;
    var messages = reader.ReadMessages(session, start);
    var display = reader.ListSessions().FirstOrDefault(s => s.Username == session)?.Display ?? session;

    var rows = new List<Dictionary<string, object?>>();
    foreach (var message in messages)
    {
        if (message.Kind != "text" || message.Text.Length == 0) continue;
        rows.Add(new Dictionary<string, object?>
        {
            ["session"] = session,
            ["kind"] = "text",
            ["create_time"] = message.CreateTime,
            ["server_id"] = long.TryParse(message.ServerId, out var serverId) && serverId > 0 ? serverId : null,
            ["sender_wxid"] = message.SenderWxid,
            ["sender"] = message.Sender,
            ["text"] = message.Text,
        });
    }
    var payload = new Dictionary<string, object?>
    {
        ["metadata"] = new Dictionary<string, object?>
        {
            ["application"] = "Moect",
            ["session"] = session,
            ["name"] = display,
            ["generator"] = "StudentReminder.WxLocalProbe",
        },
        ["count"] = rows.Count,
        ["messages"] = rows,
    };
    var outPath = options.TryGetValue("--out", out var explicitOut) ? Path.GetFullPath(explicitOut)
        : Path.Combine(cacheDir, "exports", SafeName(session) + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
    Console.WriteLine($"会话：{display}（{session}）");
    Console.WriteLine($"消息：{messages.Count} 条（其中文字 {rows.Count} 条），已导出到 {outPath}");
    Console.WriteLine("该文件可直接放入应用的 Moect 监听目录，或通过“消息来源 → Moect JSON”导入。");
    return 0;
}

int RunTail()
{
    if (!options.TryGetValue("session", out var session) && args.Length > 1 && !args[1].StartsWith("--"))
        session = args[1];
    if (string.IsNullOrEmpty(session))
    {
        Console.Error.WriteLine("用法：tail <会话ID> [--seq N] [--id N]");
        return 2;
    }
    var account = RequireAccount();
    var keysPath = Path.Combine(cacheDir, "keys.json");
    if (!File.Exists(keysPath))
        throw new InvalidOperationException($"找不到 {keysPath}，请先运行 refresh。");
    var keys = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(keysPath))
        ?? throw new InvalidOperationException("keys.json 内容无效。");

    // 增量解密 WAL（游标持久化到 tail-wal.json），复用已提取的密钥，不做整库解密。
    var walCursorPath = Path.Combine(cacheDir, "tail-wal.json");
    var walStates = File.Exists(walCursorPath)
        ? JsonSerializer.Deserialize<Dictionary<string, WxWalState?>>(File.ReadAllText(walCursorPath))
            ?? new Dictionary<string, WxWalState?>(StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, WxWalState?>(StringComparer.OrdinalIgnoreCase);
    var applied = 0;
    foreach (var (rel, info) in keys)
    {
        if (!info.TryGetValue("enc_key", out var keyHex)) continue;
        var src = Path.Combine(account.DbStorage, rel);
        var shadow = Path.Combine(cacheDir, rel);
        if (!File.Exists(src) || !File.Exists(shadow)) continue;
        var state = walStates.TryGetValue(rel, out var existing) ? existing : null;
        var result = WxDbDecryptor.ApplyWalDelta(shadow, src + "-wal", Convert.FromHexString(keyHex), state);
        if (result.Status == WxWalDeltaStatus.NeedsFullRefresh)
        {
            walStates[rel] = null;
            Console.WriteLine($"  [需整库重刷] {rel}（WAL 世代已重置，请重新 refresh）");
        }
        else if (result.State is not null)
        {
            walStates[rel] = result.State;
            applied += result.AppliedFrames;
        }
    }
    File.WriteAllText(walCursorPath, JsonSerializer.Serialize(walStates));
    Console.WriteLine($"WAL 增量：本次应用 {applied} 帧。");

    long seq = 0;
    if (options.TryGetValue("--seq", out var seqText) && long.TryParse(seqText, out var parsedSeq))
        seq = parsedSeq;
    long id = 0;
    if (options.TryGetValue("--id", out var idText) && long.TryParse(idText, out var parsedId))
        id = parsedId;
    using var reader = WxLocalReader.Open(cacheDir, account.Wxid);
    var messages = reader.ReadMessagesAfter(session, seq, id);
    Console.WriteLine($"会话 {session}：游标(seq={seq}, id={id}) 之后共 {messages.Count} 条消息");
    var visible = messages.Count <= 20 ? messages : messages.Skip(messages.Count - 5).ToList();
    foreach (var m in visible)
        Console.WriteLine($"  {DateTimeOffset.FromUnixTimeSeconds(m.CreateTime).ToLocalTime():yyyy-MM-dd HH:mm:ss} [{m.Sender}] {m.Text}");
    if (messages.Count > 0)
        Console.WriteLine($"最新游标：--seq {messages[^1].SortSeq} --id {messages[^1].LocalId}");
    return 0;
}

static string SafeName(string name)
{
    var prefix = name.Length <= 40 ? name : name[..40];
    return string.Concat(prefix.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
}

WxAccount RequireAccount()
{
    var wxid = options.GetValueOrDefault("--wxid");
    var account = WxPaths.FindAccount(wxid);
    if (account is null)
        throw new InvalidOperationException("未找到微信账号目录。请先运行 info 检查环境，确认微信 4.x 已在本机登录过。");
    return account;
}

WxMessageReader RequireReader(out string wxid)
{
    if (!Directory.Exists(cacheDir) || !Directory.EnumerateFiles(cacheDir, "*.db", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException($"缓存目录没有解密库：{cacheDir}。请先运行 refresh。");
    wxid = options.GetValueOrDefault("--wxid") ?? WxPaths.FindAccount()?.Wxid ?? "";
    return WxLocalReader.Open(cacheDir, wxid);
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1] : "";
            options[args[i]] = value;
            if (value.Length > 0) i++;
        }
        else if (i == 1)
            options["session"] = args[i];
    }
    return options;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"未知命令：{command}（可用：info / refresh / decrypt-only / sessions / export）");
    return 2;
}
