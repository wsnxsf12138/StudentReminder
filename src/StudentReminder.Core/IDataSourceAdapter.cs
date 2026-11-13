namespace StudentReminder.Core;

public interface IDataSourceAdapter : IDisposable
{
    string AdapterId { get; }
    bool SupportsSubscription { get; }
    Task<SourceHealth> HealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(CancellationToken cancellationToken = default);
    Task<MessagePage> FetchMessagesAsync(string conversationId, string? cursor, int limit,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<SourceNotification> SubscribeAsync(IReadOnlyList<string> conversationIds,
        CancellationToken cancellationToken = default);
}

public sealed record SourceHealth(bool Available, string StatusCode, DateTimeOffset CheckedAt, string? ProviderAccount = null);
public sealed record SourceConversation(string Id, string Name, string Type);
public sealed record MessagePage(IReadOnlyList<NormalizedMessage> Messages, string? NextCursor, bool HasMore);
