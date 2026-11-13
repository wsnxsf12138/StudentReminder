using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class QQMessageTestTests
{
    private const string Token = "synthetic-onebot-token";

    [Fact]
    public async Task ForwardSocketShowsOnlyApprovedGroupAndFriendMessagesAfterReconnect()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        var connections = 0;
        app.MapGet("/", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + Token || !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            if (Interlocked.Increment(ref connections) == 1)
            {
                await SendAsync(socket, Event("group", 10009, 10002, 20001, "other account"), ct);
                await SendAsync(socket, Event("group", 10001, 10002, 20009, "other group"), ct);
                await SendAsync(socket, Event("group", 10001, 10001, 20001, "own message"), ct);
                await SendAsync(socket, Event("private", 10001, 10009, 0, "other friend"), ct);
                await SendAsync(socket, Event("private", 10001, 10002, 0, "temporary chat", "group"), ct);
                await SendAsync(socket, "{malformed"u8.ToArray(), ct);
                await SendAsync(socket, Event("group", 10001, 10002, 20001, "合成群通知"), ct);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }
            else
            {
                await SendAsync(socket, Event("private", 10001, 10002, 0, "合成私聊"), ct);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }
        });
        await app.StartAsync(ct);

        var port = new Uri(app.Urls.Single()).Port.ToString(CultureInfo.InvariantCulture);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal) ? "Release" : "Debug";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/StudentReminder.NapCatReceiver/bin", configuration, "net10.0/QQMessageTest.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { path, port, "10001", "20001", "10002", "--show-text" }) start.ArgumentList.Add(argument);
        start.Environment["STUDENTREMINDER_NAPCAT_TOKEN"] = Token;
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("OneBot 事件流已连接。", await process.StandardOutput.ReadLineAsync(ct));
            using var group = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(ct))!);
            Assert.Equal("group", group.RootElement.GetProperty("kind").GetString());
            Assert.Equal("合成群通知", group.RootElement.GetProperty("text").GetString());
            Assert.Contains("重连", await process.StandardOutput.ReadLineAsync(ct));
            Assert.Equal("OneBot 事件流已连接。", await process.StandardOutput.ReadLineAsync(ct));
            using var friend = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(ct))!);
            Assert.Equal("private", friend.RootElement.GetProperty("kind").GetString());
            Assert.Equal("10002", friend.RootElement.GetProperty("sender").GetString());
            Assert.Equal("合成私聊", friend.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task SendAsync(WebSocket socket, byte[] frame, CancellationToken cancellationToken)
    {
        const int firstLength = 4;
        await socket.SendAsync(frame.AsMemory(0, firstLength), WebSocketMessageType.Text, false, cancellationToken);
        await socket.SendAsync(frame.AsMemory(firstLength), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static byte[] Event(string kind, int account, int sender, int group, string text, string subType = "friend") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            post_type = "message", message_type = kind, sub_type = subType, self_id = account, group_id = group,
            user_id = sender, message_id = 6, time = 1794376800,
            message = new[] { new { type = "text", data = new { text } } }
        });
}
