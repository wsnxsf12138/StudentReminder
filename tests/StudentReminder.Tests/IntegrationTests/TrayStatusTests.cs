using StudentReminder.App;
using StudentReminder.Core;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class TrayStatusTests
{
    private static SourceDefinition Wechat => new() { Adapter = "wxlocal", AccountAlias = "synthetic-wechat" };
    private static SourceDefinition Qq => new() { Adapter = "qqlocal", AccountAlias = "synthetic-qq" };

    [Fact]
    public void ShowsEachPlatformAndPrioritizesAnEnabledSourceFailure()
    {
        var tray = new TrayStatus();
        Assert.Equal(TrayIcon.Ready, tray.Icon);
        Assert.Contains("微信：未配置 · QQ：未配置", tray.ToolTip);

        tray.UpdateSources([Wechat, Qq]);
        Assert.Equal(TrayIcon.Paused, tray.Icon);

        var wechat = Wechat with { Enabled = true, Health = "pending" };
        var qq = Qq with { Enabled = true, Health = "ok" };
        tray.UpdateSources([wechat, Qq]);
        Assert.Equal(TrayIcon.Syncing, tray.Icon);
        Assert.Contains("微信：等待同步 · QQ：已暂停", tray.ToolTip);

        tray.UpdateSources([wechat, qq]);
        Assert.Equal(TrayIcon.Syncing, tray.Icon);
        Assert.Contains("微信：等待同步 · QQ：后台同步", tray.ToolTip);

        tray.UpdateSources([wechat, qq with { Health = "offline" }]);
        Assert.Equal(TrayIcon.Attention, tray.Icon);
        Assert.Equal(qq.Id, tray.SourceNeedingAttention);
        Assert.Contains("QQ：服务离线", tray.ToolTip);

        tray.UpdateSources([Wechat, qq]);
        Assert.Equal(TrayIcon.Syncing, tray.Icon);
        Assert.Null(tray.SourceNeedingAttention);
        Assert.Contains("微信：已暂停 · QQ：后台同步", tray.ToolTip);
    }

    [Fact]
    public void ViewingAnOlderReminderListKeepsNewlyReportedRemindersVisible()
    {
        var tray = new TrayStatus();
        var failedQq = Qq with { Enabled = true, Health = "offline" };
        tray.UpdateSources([failedQq]);
        tray.ReportMissedReminders();
        var displayedVersion = tray.ReminderVersion;
        tray.ReportMissedReminders();

        tray.ViewReminders(displayedVersion);
        Assert.True(tray.HasUnviewedReminders);
        Assert.Contains("有错过的提醒", tray.ToolTip);

        tray.ViewReminders(tray.ReminderVersion);
        Assert.False(tray.HasUnviewedReminders);
        Assert.Equal(TrayIcon.Attention, tray.Icon);
        Assert.Equal(failedQq.Id, tray.SourceNeedingAttention);

        tray.UpdateSources([Qq]);
        Assert.Equal(TrayIcon.Paused, tray.Icon);
    }
}
