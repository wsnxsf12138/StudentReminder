namespace StudentReminder.Core;

public interface IMessageRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ImportResult> SaveBatchAsync(IReadOnlyList<NormalizedMessage> messages, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMessage>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
    Task<long> CountAsync(CancellationToken cancellationToken = default);
}

public sealed record ImportResult(int Added, int Duplicates, int Updated, int Conflicts);
public sealed record StoredMessage(string Id, NormalizedMessage Message, string Status)
{
    public string Source => Message.Source;
    public string Account => Message.AccountId;
    public string Conversation => Message.ConversationName;
    public string Sender => Message.SenderName;
    public DateTimeOffset LocalSentAt => Message.SentAt.ToLocalTime();
    public string Text => Message.Text;
}
