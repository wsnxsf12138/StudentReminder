using System.Text;
using System.Text.Json;
using StudentReminder.QqLocal;

if (args.Length == 0)
{
    Console.WriteLine("""
        StudentReminder.QqLocalProbe — QQ NT 本地聊天记录读取探针

        用法：
          info                                  查看 QQ 账号目录与进程环境
          refresh [--qq ID] [--key PASS] [--cache DIR]  提取密钥并解密全部数据库到缓存目录
          decrypt-only [--qq ID] [--cache DIR]   用已保存的 keys.json 重新解密（不扫进程）
          sessions [--cache DIR]                 列出会话（需先 refresh）
          export <会话ID> [--group] [--cache DIR] [--out FILE] [--days N]
                                                导出该会话为 JSON
          tail <会话ID> [--group] [--lastrow N] [--cache DIR]
                                                增量解密 WAL 后，读取游标之后的新消息

        默认缓存目录：%LOCALAPPDATA%\StudentReminder\qqlocal
        需要 QQ NT 已登录运行；密钥提取建议以管理员身份运行本工具。
        """);
    return 0;
}

Console.OutputEncoding = new UTF8Encoding(false);
var command = args[0].ToLowerInvariant();
var options = ParseOptions(args);
var cacheDir = options.TryGetValue("--cache", out var cache)
    ? Path.GetFullPath(cache)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StudentReminder", "qqlocal");

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
    var env = QqLocalReader.Describe();
    Console.WriteLine($"腾讯文件目录 : {QqPaths.GetTencentFilesRoot() ?? "未检测到"}");
    Console.WriteLine($"运行进程     : {(env.RunningProcesses.Count > 0
        ? string.Join(", ", env.RunningProcesses.Select(p => $"PID={p.Pid}({p.WorkingSetBytes / 1024 / 1024}MB)")) : "QQ 未运行")}");
    foreach (var account in env.Accounts)
        Console.WriteLine($"账号         {account.Qq}  ->  {account.NtDbDir}");
    if (env.Accounts.Count == 0) Console.WriteLine("未找到账号目录。请先在本机登录 QQ NT。");
    Console.WriteLine("提示         refresh 只能提取「当前登录中」账号的密钥；多账号时请对已登录的账号执行 refresh。");
    return 0;
}

int RunRefresh()
{
    var account = RequireAccount();
    Console.WriteLine($"账号：{account.Qq}");
    var passphrase = options.GetValueOrDefault("--key");
    var stats = QqLocalReader.Refresh(account, cacheDir, passphrase, Console.Out);
    Console.WriteLine($"解密完成：成功 {stats.Ok}，失败 {stats.Failed}，跳过 {stats.Skipped}");
    Console.WriteLine($"缓存目录：{cacheDir}");
    return stats.Failed > 0 && stats.Ok == 0 ? 2 : 0;
}

int RunDecryptOnly()
{
    var account = RequireAccount();
    var stats = QqLocalReader.DecryptOnly(account, cacheDir, Console.Out);
    Console.WriteLine($"解密完成：成功 {stats.Ok}，失败 {stats.Failed}，跳过 {stats.Skipped}");
    return stats.Failed > 0 && stats.Ok == 0 ? 2 : 0;
}

int RunSessions()
{
    using var reader = RequireReader();
    var conversations = reader.ListConversations();
    Console.WriteLine($"共 {conversations.Count} 个会话：");
    foreach (var conversation in conversations.Take(200))
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeSeconds(conversation.LastTime).ToLocalTime():yyyy-MM-dd HH:mm}" +
            $"  [{conversation.Type}] {conversation.Name,-20} {conversation.Id}  共 {conversation.Count} 条");
    return 0;
}

int RunExport()
{
    if (!options.TryGetValue("session", out var session) && args.Length > 1 && !args[1].StartsWith("--"))
        session = args[1];
    if (string.IsNullOrEmpty(session))
    {
        Console.Error.WriteLine("用法：export <会话ID> [--group] [--out FILE] [--days N]");
        return 2;
    }
    using var reader = RequireReader();
    var group = options.ContainsKey("--group") || reader.IsGroup(session) == true;
    long? start = null;
    if (options.TryGetValue("--days", out var days) && int.TryParse(days, out var daysValue))
        start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - daysValue * 86400L;
    var messages = reader.ReadMessages(session, group, start);

    var rows = new List<Dictionary<string, object?>>();
    foreach (var message in messages)
    {
        rows.Add(new Dictionary<string, object?>
        {
            ["rowid"] = message.RowId,
            ["msg_id"] = message.MsgId,
            ["create_time"] = message.CreateTime,
            ["sender_uid"] = message.SenderUid,
            ["sender_qq"] = message.SenderQq,
            ["sender"] = message.Sender,
            ["sent_by_me"] = message.SentByMe,
            ["kind"] = message.Kind,
            ["text"] = message.Text,
        });
    }
    var payload = new Dictionary<string, object?>
    {
        ["application"] = "QqLocalProbe",
        ["conversation"] = session,
        ["type"] = group ? "group" : "private",
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
    Console.WriteLine($"会话：{session}（{(group ? "群聊" : "私聊")}）");
    Console.WriteLine($"消息：{messages.Count} 条，已导出到 {outPath}");
    return 0;
}

int RunTail()
{
    if (!options.TryGetValue("session", out var session) && args.Length > 1 && !args[1].StartsWith("--"))
        session = args[1];
    if (string.IsNullOrEmpty(session))
    {
        Console.Error.WriteLine("用法：tail <会话ID> [--group] [--lastrow N]");
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
        ? JsonSerializer.Deserialize<Dictionary<string, QqWalState?>>(File.ReadAllText(walCursorPath))
            ?? new Dictionary<string, QqWalState?>(StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, QqWalState?>(StringComparer.OrdinalIgnoreCase);
    var applied = 0;
    foreach (var (rel, info) in keys)
    {
        if (!info.TryGetValue("enc_key", out var keyHex)) continue;
        var src = Path.Combine(account.NtDbDir, rel);
        var shadow = Path.Combine(cacheDir, rel);
        if (!File.Exists(src) || !File.Exists(shadow)) continue;
        var state = walStates.TryGetValue(rel, out var existing) ? existing : null;
        var result = QqDbDecryptor.ApplyWalDelta(shadow, src + "-wal", Convert.FromHexString(keyHex), state);
        if (result.Status == QqWalDeltaStatus.NeedsFullRefresh)
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

    long lastRowid = 0;
    if (options.TryGetValue("--lastrow", out var lastText) && long.TryParse(lastText, out var parsedRow))
        lastRowid = parsedRow;
    using var reader = QqLocalReader.Open(cacheDir);
    var group = options.ContainsKey("--group") || reader.IsGroup(session) == true;
    var messages = reader.ReadMessagesAfter(session, group, lastRowid);
    Console.WriteLine($"会话 {session}：rowid > {lastRowid} 共 {messages.Count} 条消息");
    var visible = messages.Count <= 20 ? messages : messages.Skip(messages.Count - 5).ToList();
    foreach (var m in visible)
        Console.WriteLine($"  {DateTimeOffset.FromUnixTimeSeconds(m.CreateTime).ToLocalTime():yyyy-MM-dd HH:mm:ss} [{m.Sender}] {m.Text}");
    if (messages.Count > 0)
        Console.WriteLine($"最新游标：--lastrow {messages[^1].RowId}");
    return 0;
}

static string SafeName(string name)
{
    var prefix = name.Length <= 40 ? name : name[..40];
    return string.Concat(prefix.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
}

QqAccount RequireAccount()
{
    var qq = options.GetValueOrDefault("--qq");
    var account = QqPaths.FindAccount(qq);
    if (account is null)
        throw new InvalidOperationException("未找到 QQ 账号目录。请先运行 info 检查环境，确认 QQ NT 已在本机登录过。");
    return account;
}

QqMessageReader RequireReader()
{
    if (!File.Exists(Path.Combine(cacheDir, "nt_msg.db")))
        throw new InvalidOperationException($"缓存目录没有解密库：{cacheDir}。请先运行 refresh。");
    return QqLocalReader.Open(cacheDir);
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
    Console.Error.WriteLine($"未知命令：{command}（可用：info / refresh / decrypt-only / sessions / export / tail）");
    return 2;
}
