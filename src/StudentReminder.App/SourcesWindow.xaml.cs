using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudentReminder.Core;
using StudentReminder.Ingestion;

namespace StudentReminder.App;

public partial class SourcesWindow : Window
{
    private readonly ISyncStore store;
    private readonly ISourceAdapterFactory factory;
    private readonly SyncCoordinator coordinator;
    private readonly Func<Func<Task>, Task> changeAnalysisScope;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> runCapture;
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private SourceDefinition? selected;
    private string? pendingSourceId;
    private bool busy;
    private bool loading;

    public SourcesWindow(ISyncStore store, ISourceAdapterFactory factory,
        SyncCoordinator coordinator, Func<Func<Task>, Task> changeAnalysisScope,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runCapture)
    {
        this.store = store; this.factory = factory; this.coordinator = coordinator;
        this.changeAnalysisScope = changeAnalysisScope;
        this.runCapture = runCapture;
        InitializeComponent();
        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            try { await RefreshHealthAsync(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception)
            {
                HealthIcon.Tag = StatusGlyph.Failure;
                HealthLabel.Text = "无法刷新本地同步状态。";
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => ReloadAsync(null));
        timer.Start();
    }

    private async Task ReloadAsync(string? preferredId)
    {
        var sources = await Task.Run(() => store.ReadSourcesAsync(lifetime.Token));
        loading = true;
        SourceList.ItemsSource = sources.Select(source => new SourceRow(source)).ToArray();
        SourceList.SelectedItem = SourceList.Items.Cast<SourceRow>().FirstOrDefault(row => row.Source.Id == preferredId);
        loading = false;
        if (SourceList.SelectedItem is SourceRow row) await DisplayAsync(row.Source);
    }

    internal void NavigateTo(string sourceId)
    {
        pendingSourceId = sourceId;
        _ = NavigatePendingAsync();
    }

    private Task NavigatePendingAsync()
    {
        if (!IsLoaded || busy || lifetime.IsCancellationRequested || pendingSourceId is not { } id)
            return Task.CompletedTask;
        pendingSourceId = null;
        return RunAsync(() => ReloadAsync(id));
    }

    private async Task DisplayAsync(SourceDefinition source)
    {
        selected = source;
        loading = true;
        AdapterBox.SelectedIndex = source.Adapter == "qqlocal" ? 1 : 0;
        AliasBox.Text = source.AccountAlias;
        AliasBox.IsEnabled = false;
        PollBox.Text = source.PollSeconds.ToString(CultureInfo.InvariantCulture);
        DaysBox.Text = source.LookbackDays.ToString(CultureInfo.InvariantCulture);
        PushBox.IsChecked = source.UsePush;
        loading = false;
        var conversations = await Task.Run(() => store.ReadConversationsAsync(source.Id, lifetime.Token));
        ConversationGrid.ItemsSource = conversations.Select(conversation => new ConversationChoice(conversation)).ToArray();
        await RefreshHealthAsync();
    }

    private async Task RefreshHealthAsync()
    {
        if (selected is null) return;
        var id = selected.Id;
        var source = (await Task.Run(() => store.ReadSourcesAsync(lifetime.Token))).Single(row => row.Id == id);
        if (selected?.Id != id) return;
        HealthIcon.Tag = TrayStatus.SourceGlyph(source);
        HealthLabel.Text = $"{SourceStatus.Describe(source.Health)} · 最近成功同步：{(source.LastOkAt is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") : "尚无记录")}";
    }

    private async void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || busy || SourceList.SelectedItem is not SourceRow row) return;
        await RunAsync(() => DisplayAsync(row.Source));
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        loading = true; selected = null; SourceList.SelectedItem = null; AdapterBox.SelectedIndex = 0;
        AliasBox.Clear(); AliasBox.IsEnabled = true; PollBox.Text = "60"; DaysBox.Text = "7";
        PushBox.IsChecked = true;
        ConversationGrid.ItemsSource = null; HealthLabel.Text = "最近成功同步：尚无记录";
        HealthIcon.Tag = StatusGlyph.None;
        StatusLabel.Text = "新配置默认关闭。填写本地账号别名并保存后，可检查连接。"; loading = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollBox.Text, out var poll) || !int.TryParse(DaysBox.Text, out var days))
        { StatusLabel.Text = "间隔和回溯天数必须是整数。"; return; }
        var source = new SourceDefinition { Adapter = ((ComboBoxItem)AdapterBox.SelectedItem).Tag.ToString()!,
            AccountAlias = AliasBox.Text, PollSeconds = poll, LookbackDays = days, UsePush = PushBox.IsChecked == true };
        await RunAsync(() => changeAnalysisScope(async () =>
        {
            source.Validate();
            // Platform is part of source identity. Pause the old identity before a
            // cross-platform edit creates another profile, including its write fence.
            if (selected is { } previous && previous.Id != source.Id)
            {
                await coordinator.StopAsync(previous.Id);
                await Task.Run(() => store.SetSourceEnabledAsync(previous.Id, false, lifetime.Token));
            }
            await coordinator.StopAsync(source.Id);
            await Task.Run(() => store.SaveSourceAsync(source, lifetime.Token));
            await ReloadAsync(source.Id);
            StatusLabel.Text = "配置已保存并暂停；尚未读取任何消息。";
        }));
    }

    private SourceDefinition RequiredSource() => selected ?? throw new ArgumentException("先保存配置。");

    private async void Health_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var source = RequiredSource();
        var result = await Task.Run(async () => { using var adapter = await factory.CreateAsync(source, lifetime.Token); return await adapter.HealthAsync(lifetime.Token); });
        StatusLabel.Text = result.Available
            ? "已发现本地账号目录，点击「读取会话」载入会话。"
            : SourceStatus.Describe(result.StatusCode);
    });

    private async void List_Click(object sender, RoutedEventArgs e) => await RunAsync(() => runCapture(async ct =>
    {
        var source = RequiredSource();
        var result = await Task.Run(async () =>
        {
            using var adapter = await factory.CreateAsync(source, ct);
            var health = await adapter.HealthAsync(ct);
            if (!health.Available) throw new SourceFailureException(health.StatusCode);
            return (Conversations: await adapter.ListConversationsAsync(ct), health.ProviderAccount);
        }, ct);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => store.MergeConversationsAsync(source.Id, result.Conversations, ct, result.ProviderAccount), ct);
        await ReloadAsync(source.Id);
        StatusLabel.Text = $"已读取 {result.Conversations.Count} 个会话的名称与类型；新增会话默认不选，尚未读取其消息。";
    }, lifetime.Token));

    private async void Select_Click(object sender, RoutedEventArgs e) => await RunAsync(() => changeAnalysisScope(async () =>
    {
        var source = RequiredSource();
        ConversationGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ConversationGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var ids = ConversationGrid.Items.Cast<ConversationChoice>().Where(row => row.Selected).Select(row => row.Id).ToArray();
        await coordinator.StopAsync(source.Id);
        await Task.Run(async () => { await store.SetSourceEnabledAsync(source.Id, false, lifetime.Token); await store.SelectConversationsAsync(source.Id, ids, lifetime.Token); });
        await ReloadAsync(source.Id);
        StatusLabel.Text = $"已保存 {ids.Length} 个授权会话；同步已暂停。";
    }));

    private async void Once_Click(object sender, RoutedEventArgs e) => await RunAsync(() => runCapture(async ct =>
    {
        var source = RequiredSource();
        if (!(await store.ReadConversationsAsync(source.Id, ct)).Any(row => row.Selected)) throw new ArgumentException("未选择会话。");
        await coordinator.StopAsync(source.Id);
        await Task.Run(() => store.SetSourceEnabledAsync(source.Id, true, ct));
        try
        {
            var result = await Task.Run(() => coordinator.RunOnceAsync(source.Id, ct));
            StatusLabel.Text = $"单次同步完成：新增 {result.Added}，重复 {result.Duplicates}，撤回更新 {result.Updated}，冲突 {result.Conflicts}。主窗口点击刷新可查看。";
        }
        finally { await Task.Run(() => store.SetSourceEnabledAsync(source.Id, false)); await RefreshHealthAsync(); }
    }, lifetime.Token));

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(() => runCapture(async ct =>
    {
        var source = RequiredSource();
        if (!(await store.ReadConversationsAsync(source.Id, ct)).Any(row => row.Selected)) throw new ArgumentException("未选择会话。");
        await coordinator.StopAsync(source.Id);
        await Task.Run(() => store.SetSourceEnabledAsync(source.Id, true, ct));
        try { await coordinator.StartAsync(source.Id, ct); }
        catch
        {
            await Task.Run(() => store.SetSourceEnabledAsync(source.Id, false));
            throw;
        }
        await RefreshHealthAsync();
        StatusLabel.Text = "后台同步已启动，仅处理已保存的授权会话；连接状态和最近成功时间将定时刷新。";
    }, lifetime.Token));

    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(() => changeAnalysisScope(async () =>
    {
        var source = RequiredSource();
        await coordinator.StopAsync(source.Id);
        await Task.Run(() => store.SetSourceEnabledAsync(source.Id, false, lifetime.Token));
        await RefreshHealthAsync(); StatusLabel.Text = "已暂停；在途采集请求已取消并等待结束。";
    }));

    private async Task RunAsync(Func<Task> action, bool disablePanel = true)
    {
        if (busy) return;
        busy = true;
        if (disablePanel) ConfigurationPanel.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SourceFailureException exception) { StatusLabel.Text = SourceStatus.Describe(exception.Code); }
        catch (InvalidOperationException) { StatusLabel.Text = "无法继续，请检查当前来源配置或等在途同步结束后重试。"; }
        catch (ArgumentException) { StatusLabel.Text = "请先保存来源，检查字段范围，并选择授权会话。"; }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        { StatusLabel.Text = "无法启用：同一时刻只允许一个同平台来源采集，请先暂停另一个。"; }
        catch (Exception) { StatusLabel.Text = "操作失败：请检查本地存储、Windows 用户凭据或第三方服务状态；未展示敏感异常信息。"; }
        finally { busy = false; ConfigurationPanel.IsEnabled = true; }
        await NavigatePendingAsync();
    }

    private void Window_Closed(object? sender, EventArgs e) { timer.Stop(); lifetime.Cancel(); }

    private sealed record SourceRow(SourceDefinition Source)
    {
        public override string ToString() => $"{Source.Adapter} · {Source.AccountAlias}";
    }

    public sealed class ConversationChoice(SelectedConversation conversation)
    {
        public string Id => conversation.Id;
        public string Name => conversation.Name;
        public string DisplayType => conversation.Type == "group" ? "群聊" : "私聊";
        public bool Selected { get; set; } = conversation.Selected;
        public override string ToString() => Name;
    }
}
