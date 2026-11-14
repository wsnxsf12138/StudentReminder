using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Notifications;
using Forms = System.Windows.Forms;
using Icon = System.Drawing.Icon;

namespace StudentReminder.App;

internal sealed class DesktopReminderRuntime : IReminderNotification, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon icon;
    private readonly Dictionary<TrayIcon, Icon> trayIcons;
    private readonly TrayStatus trayStatus = new();
    private readonly Forms.ContextMenuStrip menu;
    private readonly SqliteMessageRepository store;
    private readonly ReminderDispatcher reminders;
    private readonly AppLog log;
    private readonly Action refreshToday;
    private readonly CancellationTokenSource lifetime = new();
    private Task? running;
    private bool startup = true;
    private bool recovery = true;
    private bool suspended;
    private bool timeChanged;
    private DateTimeOffset? lastCheck;

    public DesktopReminderRuntime(Dispatcher dispatcher, SqliteMessageRepository store, AppLog log,
        Action showToday, Action showMissedReminders, Action<string> showSource,
        Action pause, Action exit, Action refreshToday)
    {
        this.dispatcher = dispatcher;
        this.store = store;
        this.log = log;
        this.refreshToday = refreshToday;
        reminders = new(store, this);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开今日", null, (_, _) => dispatcher.BeginInvoke(showToday));
        menu.Items.Add("暂停采集", null, (_, _) => dispatcher.BeginInvoke(pause));
        menu.Items.Add("退出", null, (_, _) => dispatcher.BeginInvoke(exit));
        trayIcons = Enum.GetValues<TrayIcon>().ToDictionary(state => state, LoadTrayIcon);
        icon = new Forms.NotifyIcon
        {
            Text = trayStatus.ToolTip,
            Icon = trayIcons[TrayIcon.Ready],
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => dispatcher.BeginInvoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            if (trayStatus.HasUnviewedReminders) showMissedReminders();
            else if (trayStatus.SourceNeedingAttention is { } id) showSource(id);
            else showToday();
        });
        icon.BalloonTipClicked += (_, _) => dispatcher.BeginInvoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            if (trayStatus.HasUnviewedReminders) showMissedReminders();
            else showToday();
        });
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += Tick;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.TimeChanged += TimeChanged;
        timer.Start();
        RequestCheck();
    }

    private static Icon LoadTrayIcon(TrayIcon state)
    {
        var name = state.ToString().ToLowerInvariant();
        var uri = new Uri($"pack://application:,,,/StudentReminder.App;component/Assets/Icons/tray-{name}.ico");
        using var stream = Application.GetResourceStream(uri).Stream;
        using var resourceIcon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)resourceIcon.Clone();
    }

    public long ReminderVersion => trayStatus.ReminderVersion;

    public void ViewReminders(long version)
    {
        if (lifetime.IsCancellationRequested) return;
        trayStatus.ViewReminders(version);
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        var image = trayIcons[trayStatus.Icon];
        if (icon.Icon != image) icon.Icon = image;
        var toolTip = trayStatus.ToolTip;
        if (icon.Text != toolTip) icon.Text = toolTip;
    }

    public void RequestCheck()
    {
        if (lifetime.IsCancellationRequested || suspended || running is { IsCompleted: false }) return;
        var now = DateTimeOffset.UtcNow;
        var recovered = recovery || (lastCheck is { } last && now - last > TimeSpan.FromMinutes(1));
        var refreshDigest = timeChanged;
        recovery = false;
        timeChanged = false;
        lastCheck = now;
        running = CheckAsync(now, recovered, refreshDigest);
    }

    private async Task CheckAsync(DateTimeOffset now, bool recovered, bool refreshDigest)
    {
        var ct = lifetime.Token;
        var refresh = recovered;
        try
        {
            refresh |= await Task.Run(() => reminders.DispatchAsync(now, recovered, startup, ct), ct);
            startup = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            log.Write(Operation.Failure, errorType: error.GetType().Name);
            recovery |= recovered;
            refresh = true;
        }
        try
        {
            var digest = await Task.Run(async () =>
            {
                var zone = TimeZoneInfo.Local;
                var scheduled = await store.GenerateDailyDigestAsync(now, zone, scheduled: true, ct);
                if (scheduled is not null || !refreshDigest) return scheduled;
                return await store.GenerateDailyDigestAsync(now, zone, scheduled: false, ct);
            }, ct);
            if ((refresh || digest is not null) && !ct.IsCancellationRequested) refreshToday();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { log.Write(Operation.Failure, errorType: error.GetType().Name); }

        try
        {
            var sources = await Task.Run(() => store.ReadSourcesAsync(ct), ct);
            if (ct.IsCancellationRequested) return;
            trayStatus.UpdateSources(sources);
            UpdateTrayIcon();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { log.Write(Operation.Failure, errorType: error.GetType().Name); }
    }

    private void Tick(object? sender, EventArgs e) => RequestCheck();

    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) suspended = true;
        if (e.Mode != PowerModes.Resume) return;
        suspended = false;
        recovery = true;
        TimeZoneInfo.ClearCachedData();
        timeChanged = true;
        RequestCheck();
    });

    private void TimeChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(() =>
    {
        TimeZoneInfo.ClearCachedData();
        timeChanged = true;
        RequestCheck();
    });

    public async Task SubmitAsync(ReminderBatch batch, CancellationToken ct)
    {
        await dispatcher.InvokeAsync(() =>
        {
            if (batch.NewlyMissed > 0)
            {
                trayStatus.ReportMissedReminders();
                UpdateTrayIcon();
            }
            if (suspended) throw new InvalidOperationException("系统正在休眠。");
            var lines = new List<string>();
            if (batch.Due.Count > 0)
            {
                lines.Add($"{batch.Due.Count} 条事项提醒已到时间。");
                lines.AddRange(batch.Due.Select(item => item.Title).Distinct(StringComparer.Ordinal).Take(2)
                    .Select(ShortTitle));
            }
            if (batch.NewlyMissed > 0) lines.Add($"{batch.NewlyMissed} 条错过或送达不明的提醒。");
            ShowMessage(string.Join("\n", lines) + "\n点击打开「今日」。");
        }, DispatcherPriority.Normal, ct).Task;
    }

    public void ShowMessage(string message) =>
        icon.ShowBalloonTip(5000, "学生事务助理", message, Forms.ToolTipIcon.Info);

    private static string ShortTitle(string title)
    {
        var runes = title.Replace('\r', ' ').Replace('\n', ' ').EnumerateRunes().Take(37).ToArray();
        return string.Concat(runes.Take(36)) + (runes.Length > 36 ? "…" : "");
    }

    public async ValueTask DisposeAsync()
    {
        timer.Stop();
        timer.Tick -= Tick;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.TimeChanged -= TimeChanged;
        await lifetime.CancelAsync();
        if (running is not null) await running;
        icon.Visible = false;
        icon.Dispose();
        foreach (var image in trayIcons.Values) image.Dispose();
        menu.Dispose();
        lifetime.Dispose();
    }
}
