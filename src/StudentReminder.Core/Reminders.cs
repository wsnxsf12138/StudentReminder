namespace StudentReminder.Core;

public sealed record ReminderPreferences
{
    public int[] BeforeStartMinutes { get; init; } = [1440, 120];
    public int[] BeforeDeadlineMinutes { get; init; } = [1440, 120];
    public TimeOnly DigestTime { get; init; } = new(7, 30);
    public bool CloseToTray { get; init; }

    public void Validate()
    {
        foreach (var rules in new[] { BeforeStartMinutes, BeforeDeadlineMinutes })
            if (rules is null || rules.Length > 8 || rules.Any(value => value is < 0 or > 43200) ||
                rules.Distinct().Count() != rules.Length)
                throw new ArgumentException("每类最多设置 8 个不同的提前分钟数，范围为 0～43200。");
    }
}

public sealed record PlannedReminder(string Rule, DateTimeOffset FireAt);

public sealed record ReminderRecord(string Id, string EventId, string Title, string Rule,
    DateTimeOffset FireAt, string Status, string? Error);

public sealed record ReminderBatch(IReadOnlyList<ReminderRecord> Due, int NewlyMissed);

public interface IReminderStore
{
    Task<ReminderBatch> ClaimRemindersAsync(DateTimeOffset now, DateTimeOffset missedBefore, bool recoverClaims, CancellationToken ct);
    Task<IReadOnlyList<ReminderRecord>> ReadClaimedRemindersAsync(CancellationToken ct);
    Task FinishRemindersAsync(IReadOnlyList<ReminderRecord> reminders, bool submitted, CancellationToken ct);
}

public static class ReminderRules
{
    public static IEnumerable<PlannedReminder> Plan(EventCandidate item, string status, ReminderPreferences preferences)
    {
        if (status != "active") yield break;
        foreach (var (kind, time, offsets) in new[]
        {
            ("start", item.StartsAt, preferences.BeforeStartMinutes),
            ("deadline", item.DeadlineAt, preferences.BeforeDeadlineMinutes)
        })
        {
            if (time is not { } instant) continue;
            var utc = instant.ToUniversalTime();
            foreach (var minutes in offsets)
            {
                var offset = TimeSpan.FromMinutes(minutes);
                if (utc - DateTimeOffset.MinValue >= offset)
                    yield return new(FormattableString.Invariant($"{kind}:{minutes}"), utc - offset);
            }
        }
    }
}
