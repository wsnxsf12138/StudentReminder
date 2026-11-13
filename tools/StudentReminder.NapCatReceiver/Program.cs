using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StudentReminder.NapCatReceiver;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 && !Console.IsInputRedirected)
{
    args = [Prompt("NapCat 正向 WebSocket 端口"), Prompt("NapCat 登录的测试 QQ 号"),
        Prompt("授权测试群号（跳过填 -）"), Prompt("授权测试好友号（跳过填 -）")];
    if (Prompt("在本机显示正文？输入 y 确认").Equals("y", StringComparison.OrdinalIgnoreCase))
        args = [.. args, "--show-text"];
}
var showText = args.Length == 5 && args[4] == "--show-text";
if ((!showText && args.Length != 4) ||
    !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535 ||
    !NapCatEvent.IsQqId(args[1]) || (args[2] != "-" && !NapCatEvent.IsQqId(args[2])) ||
    (args[3] != "-" && !NapCatEvent.IsQqId(args[3])) || (args[2] == "-" && args[3] == "-"))
{
    Console.Error.WriteLine("用法：QQMessageTest <正向WS端口> <登录QQ号> <授权群号或-> <授权好友号或-> [--show-text]");
    return 2;
}

var token = Environment.GetEnvironmentVariable("STUDENTREMINDER_NAPCAT_TOKEN");
if (string.IsNullOrEmpty(token) && !Console.IsInputRedirected) token = ReadToken();
if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
{
    Console.Error.WriteLine("请输入不含空白的 OneBot Token。");
    return 2;
}

var endpoint = new Uri($"ws://127.0.0.1:{port}/");
var mapper = new NapCatEvent(args[1], args[2], args[3]);
var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    using var transport = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    var retrySeconds = 1;
    while (!stop.IsCancellationRequested)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", token).ToString());
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(endpoint, transport, connectTimeout.Token);
            }
            retrySeconds = 1;
            Console.WriteLine("OneBot 事件流已连接。");
            await foreach (var frame in OneBotFrames.ReadAsync(socket, stop.Token))
            {
                if (mapper.Read(frame, DateTimeOffset.UtcNow) is not { } message) continue;
                Console.WriteLine(showText
                    ? JsonSerializer.Serialize(new { kind = message.ConversationType, sentAt = message.SentAt,
                        conversation = message.ConversationId, messageId = message.MessageId, sender = message.SenderId, senderName = message.SenderName,
                        text = message.Text }, jsonOptions)
                    : JsonSerializer.Serialize(new { kind = message.ConversationType, sentAt = message.SentAt }));
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { Console.WriteLine("连接超时。"); }
        catch (WebSocketException) { Console.WriteLine("连接失败或已断开。请检查端口、Token 和 NapCat 状态。"); }
        if (stop.IsCancellationRequested) break;
        Console.WriteLine($"{retrySeconds} 秒后重连。");
        await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stop.Token);
        retrySeconds = Math.Min(30, retrySeconds * 2);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally { Console.CancelKeyPress -= cancel; }
return 0;

static string Prompt(string label)
{
    Console.Write(label + "：");
    return Console.ReadLine()?.Trim() ?? "";
}

static string ReadToken()
{
    Console.Write("OneBot Token（隐藏输入）：");
    var token = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && token.Length > 0) token.Length--;
        else if (!char.IsControl(key.KeyChar)) token.Append(key.KeyChar);
    }
    Console.WriteLine();
    return token.ToString();
}
