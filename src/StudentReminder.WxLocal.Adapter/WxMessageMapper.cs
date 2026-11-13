using StudentReminder.Core;

namespace StudentReminder.WxLocal.Adapter;

/// <summary>Maps a raw <see cref="WxMessage"/> into the unified <see cref="NormalizedMessage"/> contract.</summary>
public static class WxMessageMapper
{
    public static NormalizedMessage Map(WxMessage message, string accountId, string conversationId,
        DateTimeOffset receivedAt)
    {
        return MessageValidation.Normalize(new NormalizedMessage
        {
            Source = "wechat",
            Adapter = "wxlocal",
            AccountId = accountId,
            ConversationId = conversationId,
            ConversationName = conversationId,
            ConversationType = conversationId.EndsWith("@chatroom", StringComparison.Ordinal) ? "group" : "private",
            MessageId = string.IsNullOrWhiteSpace(message.ServerId) || message.ServerId == "0" ? null : message.ServerId,
            SenderId = string.IsNullOrEmpty(message.SenderWxid) ? "unknown" : message.SenderWxid,
            SenderName = message.Sender,
            SentAt = DateTimeOffset.FromUnixTimeSeconds(message.CreateTime),
            ReceivedAt = receivedAt,
            ContentType = "text",
            Text = message.Text,
        });
    }
}
