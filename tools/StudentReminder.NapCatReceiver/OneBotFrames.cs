using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace StudentReminder.NapCatReceiver;

internal static class OneBotFrames
{
    private const int MaxFrameBytes = 256 * 1024;

    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(ClientWebSocket socket,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bytes = new byte[MaxFrameBytes];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(bytes.AsMemory(length), cancellationToken);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    yield break;
                }
                length += frame.Count;
                if (length == MaxFrameBytes && !frame.EndOfMessage) yield break;
            } while (!frame.EndOfMessage);
            if (frame.MessageType == WebSocketMessageType.Text) yield return bytes.AsMemory(0, length);
        }
    }
}
