namespace StudentReminder.Core;

public static class MessageValidation
{
    public const int MaxMessages = 10000;
    public const int MaxTextLength = 65536;

    public static NormalizedMessage Normalize(NormalizedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Required(message.Source, "source");
        Required(message.Adapter, "adapter");
        Required(message.AccountId, "account_id");
        Required(message.ConversationId, "conversation_id");
        Required(message.SenderId, "sender_id");
        if (message.Source is not ("manual" or "wechat" or "qq"))
            throw new ArgumentException("source 只支持 manual、wechat 或 qq。");
        if (message.ConversationType is not ("group" or "private"))
            throw new ArgumentException("conversation_type 只支持 group 或 private。");
        Required(message.ContentType, "content_type");
        if (message.OriginEvent is not ("message.new" or "message.revoke"))
            throw new ArgumentException("不支持的 origin_event。");
        if (message.Text is null || (message.ContentType == "text" && string.IsNullOrWhiteSpace(message.Text)))
            throw new ArgumentException("文本消息不能为空。");
        if (message.Text.Length > MaxTextLength)
            throw new ArgumentException("单条消息超过 65536 字符上限。");
        if (message.SentAt == default || message.ReceivedAt == default)
            throw new ArgumentException("消息必须有有效的发送和接收时间。");
        if (message.MessageId is not null) Required(message.MessageId, "message_id");
        if (message.ConversationName is null || message.SenderName is null || message.Metadata is null)
            throw new ArgumentException("名称和 metadata 不得为 null。");
        return message with
        {
            SentAt = message.SentAt.ToUniversalTime(),
            ReceivedAt = message.ReceivedAt.ToUniversalTime(),
            IsRecalled = message.IsRecalled || message.OriginEvent == "message.revoke"
        };
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException($"字段 {field} 必须为 1～512 个字符的非空值。");
    }
}
