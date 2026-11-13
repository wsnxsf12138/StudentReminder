using System.Text.Json;

namespace StudentReminder.Core;

/// <summary>Internal protocol. AccountId is a local alias, never a raw platform account.</summary>
public sealed record NormalizedMessage
{
    public required string Source { get; init; }
    public required string Adapter { get; init; }
    public required string AccountId { get; init; }
    public required string ConversationId { get; init; }
    public string ConversationName { get; init; } = "";
    public string ConversationType { get; init; } = "group";
    public string? MessageId { get; init; }
    public required string SenderId { get; init; }
    public string SenderName { get; init; } = "";
    public required DateTimeOffset SentAt { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public string ContentType { get; init; } = "text";
    public required string Text { get; init; }
    public bool IsRecalled { get; init; }
    public string OriginEvent { get; init; } = "message.new";
    public Dictionary<string, JsonElement> Metadata { get; init; } = [];
}
