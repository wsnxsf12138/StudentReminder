using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StudentReminder.AI;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.App;

public partial class TodayWindow : Window
{
    private readonly SqliteMessageRepository store;
    private readonly ISourceTokenStore tokens;
    private readonly Action<string, bool> openReview;
    private readonly Func<long> reminderVersion;
    private readonly Action<long> viewReminders;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? polishCancellation;
    private Task? polish;
    private DailyDigest? digest;
    private SectionView? highlights;
    private bool loading;
    private bool refreshPending;
    private bool ready;
    private bool requestsBlocked;

    internal TodayWindow(SqliteMessageRepository store, ISourceTokenStore tokens, Action<string, bool> openReview,
        Func<long> reminderVersion, Action<long> viewReminders)
    {
        this.store = store;
        this.tokens = tokens;
        this.openReview = openReview;
        this.reminderVersion = reminderVersion;
        this.viewReminders = viewReminders;
        InitializeComponent();
        Closing += Window_Closing;
        Activated += async (_, _) => await RefreshVisibleMissedRemindersAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        ready = true;
        await LoadAsync(force: true);
    }

    public Task RefreshCachedAsync() => ready ? LoadAsync(force: false) : Task.CompletedTask;

    public Task RefreshForOpenAsync() => ready ? LoadAsync(force: true) : Task.CompletedTask;

    internal void SelectMissedReminders() => Tabs.SelectedItem = MissedTab;

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs) await RefreshVisibleMissedRemindersAsync();
    }

    private bool IsViewingMissedReminders =>
        IsActive && WindowState != WindowState.Minimized && Tabs.SelectedItem == MissedTab;

    private Task RefreshVisibleMissedRemindersAsync() =>
        ready && IsViewingMissedReminders ? LoadAsync(force: false) : Task.CompletedTask;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(force: true);
    }

    private async Task LoadAsync(bool force)
    {
        if (lifetime.IsCancellationRequested) return;
        if (loading)
        {
            refreshPending = true;
            return;
        }
        loading = true;
        SetControls();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var zone = TimeZoneInfo.Local;
            var ct = lifetime.Token;
            var version = reminderVersion();
            var loaded = await Task.Run(async () =>
            {
                var cached = force ? null : await store.ReadDailyDigestAsync(DigestCalendar.Day(now, zone), ct);
                return (Digest: cached ?? await store.GenerateDailyDigestAsync(now, zone, scheduled: false, ct),
                    Missed: await store.ReadMissedRemindersAsync(ct));
            }, ct);
            if (ct.IsCancellationRequested) return;
            if (digest?.GeneratedAt != loaded.Digest?.GeneratedAt)
            {
                digest = loaded.Digest;
                highlights = null;
                RenderDigest();
            }
            Missed.ItemsSource = loaded.Missed.Select(item => new LinkRow(item.EventId, false,
                $"{item.FireAt.ToLocalTime():MM-dd HH:mm zzz} · {(item.Rule.StartsWith("start:", StringComparison.Ordinal) ? "开始提醒" : "截止提醒")} · " +
                $"{item.Title} · {MissedStatus(item.Status)}", StatusGlyph.Failure)).ToArray();
            MissedCaption.Text = loaded.Missed.Count == 0 ? "暂无错过的提醒。" : $"共 {loaded.Missed.Count} 条，显示原计划时间。";
            if (IsViewingMissedReminders) viewReminders(version);
            if (force) StatusLabel.Text = "已刷新本地摘要。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusLabel.Text = "暂时无法读取本地摘要或提醒，请刷新重试。"; }
        finally
        {
            loading = false;
            SetControls();
            if (refreshPending)
            {
                refreshPending = false;
                _ = LoadAsync(force: false);
            }
        }
    }

    private void RenderDigest()
    {
        if (digest is null) return;
        GeneratedLabel.Text = $"{digest.Date:yyyy-MM-dd} · {digest.GeneratedAt.ToLocalTime():HH:mm} 生成";
        var groups = digest.Sections.Select(section => new SectionView(section.Title,
            section.Entries.Count == 0 ? [new(null, false, "暂无")] :
            section.Entries.Select(item => new LinkRow(item.Id, item.IsCandidate, Format(item),
                section.Title == "未来 72 小时截止" ? StatusGlyph.Reminder : StatusGlyph.None)).ToArray()));
        Sections.ItemsSource = highlights is { } header ? groups.Prepend(header).ToArray() : groups.ToArray();
    }

    private async void Polish_Click(object sender, RoutedEventArgs e)
    {
        if (requestsBlocked || digest is null || polish is { IsCompleted: false }) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        polishCancellation = cancellation;
        polish = PolishAsync(digest, cancellation.Token);
        SetControls();
        try { await polish; }
        finally { polish = null; polishCancellation = null; SetControls(); }
    }

    private async Task PolishAsync(DailyDigest current, CancellationToken ct)
    {
        try
        {
            var selected = (ComboBoxItem)ProviderBox.SelectedItem;
            var providerId = (string)selected.Tag;
            var profile = await Task.Run(() => store.ReadAiProfileAsync(providerId, ct), ct);
            var key = await tokens.ReadAsync(SourceKeys.Hash("moect-ai", providerId), ct);
            if (profile is null || string.IsNullOrEmpty(key))
            {
                StatusLabel.Text = "请先在「AI 分析」中保存所选服务商的配置。";
                return;
            }
            using var provider = new ChatCompletionAiProvider();
            provider.ValidateConfig(profile, key);
            var entries = current.Sections.Take(3).SelectMany(section => section.Entries)
                .DistinctBy(item => item.Id).Take(8).ToArray();
            if (entries.Length == 0)
            {
                StatusLabel.Text = "当前摘要没有可润色的已确认事项。";
                return;
            }
            var inputs = entries.Select((item, index) => new AiDigestItem(index + 1, AnalysisText.Prepare(item.Title),
                item.Category, item.StartsAt, item.DeadlineAt)).ToArray();
            ct.ThrowIfCancellationRequested();
            if (!ConfirmPayload($"{selected.Content} · {profile.Model}", ChatCompletionAiProvider.SerializeDigestInput(inputs), ct)) return;
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(digest, current))
            {
                StatusLabel.Text = "摘要已更新，请重新预览后再提交。";
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var reservation = await Task.Run(() => store.ReserveDigestRequestAsync(profile, now, TimeZoneInfo.Local, ct), ct);
            if (reservation.RequestId is not { } requestId)
            {
                StatusLabel.Text = reservation.DailyLimit ? "已达到今日 AI 请求上限，本地摘要仍可查看。"
                    : $"AI 请求暂受限，请于 {reservation.AvailableAt?.ToLocalTime():HH:mm:ss} 后重试。";
                return;
            }
            StatusLabel.Text = "正在润色摘要…";
            AiDigestPolish result;
            try { result = await provider.GenerateDigestAsync(profile, key, inputs, ct); }
            catch (AiFailureException error)
            {
                var cooldown = error.RetryAfter is { } retry ? DateTimeOffset.UtcNow + retry : (DateTimeOffset?)null;
                await Task.Run(() => store.FinishDigestRequestAsync(requestId, error.Usage, cooldown));
                StatusLabel.Text = "AI 润色暂未完成，本地摘要仍可查看。";
                return;
            }
            await Task.Run(() => store.FinishDigestRequestAsync(requestId, result.Usage, null));
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(digest, current))
            {
                StatusLabel.Text = "摘要已更新，可以重新润色。";
                return;
            }
            highlights = new("今日重点 · AI 排序", result.Choices.Select(choice =>
            {
                var item = entries[choice.Id - 1];
                return new LinkRow(item.Id, false, Format(item, choice.Style == "time-first"));
            }).ToArray());
            RenderDigest();
            StatusLabel.Text = "已生成今日重点。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusLabel.Text = "已取消润色。"; }
        catch (Exception) { StatusLabel.Text = "无法完成 AI 润色，本地摘要仍可查看。"; }
    }

    private bool ConfirmPayload(string provider, string payload, CancellationToken ct)
    {
        var layout = new DockPanel { Margin = new(20) };
        var dialog = new Window
        {
            Owner = this, Icon = Icon, Title = "预览摘要润色内容", Width = 700, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily, FontSize = FontSize, Content = layout
        };
        var heading = new TextBlock { Text = $"发送给 {provider} 的事项数据", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        var send = new Button { Content = "确认发送" };
        send.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(send);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        var preview = new TextBox { Text = payload, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(preview, "即将发送的数据");
        layout.Children.Add(preview);
        using var closePreview = ct.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        return dialog.ShowDialog() == true;
    }

    private static string Format(DigestEntry entry, bool timeFirst = false)
    {
        var fields = new List<string>();
        if (entry.StartsAt is { } start) fields.Add($"开始 {start.ToLocalTime():MM-dd HH:mm}");
        else if (entry.StartsOn is { } startDay) fields.Add($"开始 {startDay:MM-dd}（时刻待补）");
        if (entry.DeadlineAt is { } due) fields.Add($"截止 {due.ToLocalTime():MM-dd HH:mm}");
        else if (entry.DeadlineOn is { } dueDay) fields.Add($"截止 {dueDay:MM-dd}（时刻待补）");
        if (timeFirst) fields.Add(entry.Title);
        else fields.Insert(0, entry.Title);
        if (entry.Category.Length > 0) fields.Add(ReviewLabels.Category(entry.Category));
        if (entry.Detail is { } detail) fields.Add(detail);
        return string.Join(" · ", fields);
    }

    private static string MissedStatus(string status) => status switch
    {
        "missed" => "已错过", "uncertain" => "送达不明", _ => "通知提交失败"
    };

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is LinkRow { Id: { } id } row) openReview(id, row.IsCandidate);
    }

    public async Task SuspendPolishAsync()
    {
        requestsBlocked = true;
        polishCancellation?.Cancel();
        if (polish is { IsCompleted: false } operation) await operation;
        SetControls();
    }

    public void EnablePolish()
    {
        requestsBlocked = false;
        SetControls();
    }

    private void SetControls()
    {
        var active = polish is { IsCompleted: false };
        RefreshButton.IsEnabled = !loading && !active;
        PolishButton.IsEnabled = !loading && !active && !requestsBlocked && digest is not null;
        CancelButton.IsEnabled = active;
    }

    private void CancelPolish_Click(object sender, RoutedEventArgs e) => polishCancellation?.Cancel();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (polish is not { IsCompleted: false }) return;
        e.Cancel = true;
        await SuspendPolishAsync();
        if (IsLoaded) Close();
    }

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    private sealed record SectionView(string Title, IReadOnlyList<LinkRow> Rows);
    private sealed record LinkRow(string? Id, bool IsCandidate, string Text, StatusGlyph Glyph = StatusGlyph.None)
    {
        public Visibility LinkVisibility => Id is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
