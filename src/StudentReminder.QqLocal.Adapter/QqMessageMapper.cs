using System.Globalization;
using StudentReminder.Core;

namespace StudentReminder.QqLocal.Adapter;

/// <summary>Maps a raw <see cref="QqMessage"/> into the unified <see cref="NormalizedMessage"/> contract.</summary>
public static class QqMessageMapper
{
    public static NormalizedMessage Map(QqMessage message, string accountId, string conversationId,
        string conversationName, bool group, DateTimeOffset receivedAt)
    {
        return MessageValidation.Normalize(new NormalizedMessage
        {
            Source = "qq",
            Adapter = "qqlocal",
            AccountId = accountId,
            ConversationId = conversationId,
            ConversationName = string.IsNullOrWhiteSpace(conversationName) ? conversationId : conversationName,
            ConversationType = group ? "group" : "private",
            MessageId = message.MsgId == 0 ? null : message.MsgId.ToString(CultureInfo.InvariantCulture),
            SenderId = !string.IsNullOrEmpty(message.SenderQq) ? message.SenderQq
                : !string.IsNullOrEmpty(message.SenderUid) ? message.SenderUid
                : "unknown",
            SenderName = message.Sender,
            SentAt = DateTimeOffset.FromUnixTimeSeconds(message.CreateTime),
            ReceivedAt = receivedAt,
            ContentType = "text",
            Text = message.Text,
        });
    }
}
