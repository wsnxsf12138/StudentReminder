using StudentReminder.Core;

namespace StudentReminder.App;

internal enum TrayIcon { Ready, Syncing, Paused, Attention }

internal sealed class TrayStatus
{
    private SourceDefinition? wechat;
    private SourceDefinition? qq;
    private long viewedReminderVersion;

    public long ReminderVersion { get; private set; }
    public bool HasUnviewedReminders => ReminderVersion > viewedReminderVersion;
    public string? SourceNeedingAttention =>
        NeedsAttention(wechat) ? wechat!.Id : NeedsAttention(qq) ? qq!.Id : null;

    public TrayIcon Icon => HasUnviewedReminders || SourceNeedingAttention is not null ? TrayIcon.Attention
        : IsSyncing(wechat) || IsSyncing(qq) ? TrayIcon.Syncing
        : wechat is not null || qq is not null ? TrayIcon.Paused : TrayIcon.Ready;

    public string ToolTip => $"学生事务助理 · 微信：{Describe(wechat)} · QQ：{Describe(qq)}"
        + (HasUnviewedReminders ? " · 有错过的提醒" : "");

    public void UpdateSources(IReadOnlyList<SourceDefinition> sources)
    {
        wechat = PickSource(sources, "wxlocal");
        qq = PickSource(sources, "qqlocal");
    }

    public void ReportMissedReminders() => ReminderVersion++;

    public void ViewReminders(long version) =>
        viewedReminderVersion = Math.Max(viewedReminderVersion, Math.Min(version, ReminderVersion));

    public static StatusGlyph SourceGlyph(SourceDefinition source) => NeedsAttention(source) ? StatusGlyph.Failure
        : IsSyncing(source) ? StatusGlyph.Syncing : StatusGlyph.Paused;

    private static SourceDefinition? PickSource(IReadOnlyList<SourceDefinition> sources, string adapter) =>
        sources.FirstOrDefault(source => source.Adapter == adapter && source.Enabled)
        ?? sources.FirstOrDefault(source => source.Adapter == adapter);

    private static bool NeedsAttention(SourceDefinition? source) =>
        source is { Enabled: true, Health: not ("ok" or "pending" or "stopped" or "disabled") };

    private static bool IsSyncing(SourceDefinition? source) =>
        source is { Enabled: true, Health: "ok" or "pending" };

    private static string Describe(SourceDefinition? source) => source switch
    {
        null => "未配置",
        { Enabled: false } or { Health: "disabled" or "stopped" } => "已暂停",
        { Health: "ok" } => "后台同步",
        { Health: "account_changed" } => "账号已变化",
        { Health: "recall_unresolved" } => "撤回需复核",
        _ => SourceStatus.Describe(source.Health)
    };
}
