using StudentReminder.Core;

namespace StudentReminder.App;

internal sealed record CalendarEntry(ReviewedEvent Event, DateOnly Date, TimeOnly? Time, bool IsDeadline)
{
    public string Title => Event.Details.Title;
    public string Kind => IsDeadline ? "截止" : "开始";
    public string When => Time is { } time ? $"{time:HH:mm} {Kind}" : $"{Kind} · 时间待定";
    public string Preview => $"{(Time is { } time ? $"{time:HH:mm}" : Kind)} {Title}";
    public string Category => ReviewLabels.Category(Event.Details.Category);
    public bool IsCompleted => Event.Status == "completed";
    public string Action => Event.Details.Action;
    public string? Location => Event.Details.Location;
}

internal sealed record CalendarDay(DateOnly? Date, bool IsInMonth, bool IsToday, IReadOnlyList<CalendarEntry> Entries)
{
    public string Number => Date?.Day.ToString() ?? "";
    public string Label => Date is { } date
        ? $"{date:yyyy年M月d日}，{(Entries.Count == 0 ? "无安排" : $"{Entries.Count} 条安排")}"
        : "";
    public IReadOnlyList<CalendarEntry> Preview => Entries.Take(2).ToArray();
    public string More => Entries.Count > 2 ? $"+{Entries.Count - 2} 条" : "";
}

internal static class ReminderCalendar
{
    public static ILookup<DateOnly, CalendarEntry> Index(IEnumerable<ReviewedEvent> events, TimeZoneInfo zone) =>
        events.Where(item => item.Status is "active" or "completed")
            .SelectMany(item => Entries(item, zone))
            .OrderBy(entry => entry.Time)
            .ThenBy(entry => entry.IsDeadline)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCulture)
            .ToLookup(entry => entry.Date);

    public static IReadOnlyList<CalendarDay> BuildMonth(DateOnly month, DateOnly today, ILookup<DateOnly, CalendarEntry> entries)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var mondayOffset = ((int)first.DayOfWeek + 6) % 7;
        return Enumerable.Range(first.DayNumber - mondayOffset, 42).Select(number =>
        {
            if (number < 0 || number > DateOnly.MaxValue.DayNumber) return new CalendarDay(null, false, false, []);
            var date = DateOnly.FromDayNumber(number);
            return new CalendarDay(date, date.Month == first.Month, date == today, entries[date].ToArray());
        }).ToArray();
    }

    private static IEnumerable<CalendarEntry> Entries(ReviewedEvent item, TimeZoneInfo zone)
    {
        var details = item.Details;
        foreach (var (instant, day, isDeadline) in new[]
        {
            (details.StartsAt, details.StartsOn, false), (details.DeadlineAt, details.DeadlineOn, true)
        })
        {
            if (instant is { } timestamp)
            {
                var local = TimeZoneInfo.ConvertTime(timestamp, zone);
                yield return new(item, DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime), isDeadline);
            }
            else if (day is { } date)
            {
                yield return new(item, date, null, isDeadline);
            }
        }
    }
}
