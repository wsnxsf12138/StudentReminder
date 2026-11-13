namespace StudentReminder.Core;

public sealed record DigestEntry(string? Id, bool IsCandidate, string Title, string Category,
    DateTimeOffset? StartsAt = null, DateTimeOffset? DeadlineAt = null, string? Detail = null,
    DateOnly? StartsOn = null, DateOnly? DeadlineOn = null);

public sealed record DigestSection(string Title, IReadOnlyList<DigestEntry> Entries);
public sealed record DailyDigest(DateOnly Date, DateTimeOffset GeneratedAt, IReadOnlyList<DigestSection> Sections);
public sealed record DigestEvent(ReviewedEvent Event, DateTimeOffset CreatedAt);

public static class DigestCalendar
{
    public static DateOnly Day(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    public static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DailyDigest Build(DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<DigestEvent> events,
        IReadOnlyList<ReviewCandidate> candidates, IReadOnlyList<SourceDefinition> sources)
    {
        var day = Day(now, zone);
        var today = At(day, TimeOnly.MinValue, zone);
        var tomorrow = At(day.AddDays(1), TimeOnly.MinValue, zone);
        var active = events.Select(row => row.Event).Where(item => item.Status == "active").ToArray();
        return new(day, now,
        [
            new("今日安排和截止", active
                .Where(item => Today(item.Details.StartsAt) || Today(item.Details.DeadlineAt) ||
                    item.Details.StartsOn == day || item.Details.DeadlineOn == day)
                .OrderBy(item => new[] { item.Details.StartsAt, item.Details.DeadlineAt }.Where(Today).Min())
                .ThenBy(item => item.Id, StringComparer.Ordinal).Select(Entry).ToArray()),
            new("未来 72 小时截止", active.Where(item => item.Details.DeadlineAt >= now && item.Details.DeadlineAt < now.AddHours(72))
                .OrderBy(item => item.Details.DeadlineAt).ThenBy(item => item.Id, StringComparer.Ordinal).Select(Entry).ToArray()),
            new("未完成任务", active.OrderBy(item => item.Details.DeadlineAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.Id, StringComparer.Ordinal).Select(Entry).ToArray()),
            new("重要待确认", candidates.Where(item => item.Status == "pending" && Important(item.Draft))
                .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new DigestEntry(item.Id, true, item.Draft.Title, item.Draft.Category,
                    item.Draft.StartsAt, item.Draft.DeadlineAt, ReviewLabels.Change(item.ChangeKind),
                    item.Draft.StartsOn, item.Draft.DeadlineOn)).ToArray()),
            new("过去 24 小时新增的重要事项", events
                .Where(item => item.CreatedAt > now.AddHours(-24) && item.CreatedAt <= now &&
                    item.Event.Status != "canceled" && Important(item.Event.Details))
                .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Event.Id, StringComparer.Ordinal)
                .Select(item => Entry(item.Event)).ToArray()),
            new("消息来源异常", sources.Where(item => item.Enabled && item.Health is not ("ok" or "pending" or "disabled" or "stopped"))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new DigestEntry(null, false, $"{item.Adapter} · {item.AccountAlias}", "",
                    Detail: SourceStatus.Describe(item.Health) + (item.LastOkAt is { } last
                        ? $" · 上次成功 {TimeZoneInfo.ConvertTime(last, zone):MM-dd HH:mm}" : " · 尚无成功同步"))).ToArray())
        ]);

        bool Today(DateTimeOffset? instant) => instant >= today && instant < tomorrow;
    }

    private static bool Important(EventCandidate item) =>
        item.Priority == "high" || item.Category is "exam" or "payment";

    private static DigestEntry Entry(ReviewedEvent item) =>
        new(item.Id, false, item.Details.Title, item.Details.Category, item.Details.StartsAt, item.Details.DeadlineAt,
            StartsOn: item.Details.StartsOn, DeadlineOn: item.Details.DeadlineOn);
}
