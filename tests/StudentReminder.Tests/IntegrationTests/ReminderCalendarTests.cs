using StudentReminder.App;
using StudentReminder.Core;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class ReminderCalendarTests
{
    [Fact]
    public void CalendarUsesLocalInstantsAndKeepsDateOnlyEntriesOnTheirDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test +08", TimeSpan.FromHours(8), "test +08", "test +08");
        var november = new DateOnly(2026, 11, 1);
        var october = november.AddDays(-1);
        var template = Event("confirmed");
        var entry = template with
        {
            Details = template.Details with
            {
                StartsAt = new DateTimeOffset(2026, 10, 31, 16, 30, 0, TimeSpan.Zero),
                DeadlineOn = october
            }
        };
        var index = ReminderCalendar.Index([entry], zone);
        var days = ReminderCalendar.BuildMonth(november, november, index);

        Assert.Equal(42, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), days[0].Date);
        Assert.Equal(new DateOnly(2026, 12, 6), days[^1].Date);
        var today = Assert.Single(days, day => day.IsToday);
        var start = Assert.Single(today.Entries);
        Assert.True(today.IsInMonth);
        Assert.False(start.IsDeadline);
        Assert.Equal(new TimeOnly(0, 30), start.Time);
        var deadline = Assert.Single(Assert.Single(days, day => day.Date == october).Entries);
        Assert.True(deadline.IsDeadline);
        Assert.Null(deadline.Time);
    }

    [Fact]
    public void CalendarKeepsBothTimesAndHidesCanceledAndDeletedEvents()
    {
        var day = new DateOnly(2026, 11, 14);
        var template = Event("active");
        var scheduled = template with
        {
            Details = template.Details with
            {
                StartsAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
                DeadlineAt = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero)
            }
        };
        var completed = template with { Id = "completed", Status = "completed", Details = template.Details with { DeadlineOn = day } };
        var canceled = completed with { Id = "canceled", Status = "canceled" };
        var deleted = completed with { Id = "deleted", Status = "deleted" };
        var index = ReminderCalendar.Index([scheduled, completed, canceled, deleted, template], TimeZoneInfo.Utc);
        var tile = Assert.Single(ReminderCalendar.BuildMonth(day, day, index), item => item.IsToday);

        Assert.Collection(tile.Entries,
            entry => Assert.True(entry.IsCompleted),
            entry => Assert.False(entry.IsDeadline),
            entry => Assert.True(entry.IsDeadline));
        Assert.Equal(2, tile.Preview.Count);
        Assert.Equal("+1 条", tile.More);
    }

    private static ReviewedEvent Event(string id) =>
        new(id, new EventCandidate { Title = id, Category = "assignment", Action = "提交作业", Evidence = [] },
            "active", 1, DateTimeOffset.UnixEpoch);
}
