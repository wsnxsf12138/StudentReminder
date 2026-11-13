using System.Text.Json.Serialization;

namespace StudentReminder.Core;

public sealed record EventCandidate
{
    public required string Title { get; init; }
    public required string Category { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? DeadlineAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? StartsOn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? DeadlineOn { get; init; }
    public string? StartText { get; init; }
    public string? DeadlineText { get; init; }
    public string? Location { get; init; }
    public required string Action { get; init; }
    public string Certainty { get; init; } = "uncertain";
    public string Priority { get; init; } = "normal";
    public bool NeedsConfirmation { get; init; } = true;
    public required IReadOnlyList<MessageEvidence> Evidence { get; init; }
    public IReadOnlyList<string> Ambiguities { get; init; } = [];
}

public sealed record MessageEvidence(string Source, string ConversationId, string MessageId)
{
    // Required on future multi-account event creation. The plan's minimal sample can omit it.
    public string? AccountId { get; init; }
}
