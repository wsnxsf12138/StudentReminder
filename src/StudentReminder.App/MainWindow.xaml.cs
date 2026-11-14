using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Ingestion;

namespace StudentReminder.App;

public partial class MainWindow : Window
{
    private readonly SqliteMessageRepository repository;
    private readonly ManualImportService importer;
    private readonly AppLog log;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private readonly ISourceTokenStore sourceTokens;
    private readonly ISourceAdapterFactory adapterFactory;
    private readonly SyncCoordinator coordinator;
    private readonly DirectReadCache cache;
    private SourcesWindow? sourcesWindow;
    private MoectWatchWindow? moectWindow;
    private AiAnalysisWindow? aiWindow;
    private EventReviewWindow? reviewWindow;
    private readonly SemaphoreSlim sourceChanges = new(1);
    private readonly bool defaultDataDirectory;
    private ReminderPreferences preferences = new();
    private DesktopReminderRuntime? desktop;
    private TodayWindow? todayWindow;
    private ReminderSettingsWindow? settingsWindow;
    private Task? pauseOperation;
    private Task? captureOperation;
    private CancellationTokenSource? captureCancellation;
    private bool closing;
    private bool shutdownReady;
    private bool explicitExit;
    private bool initialized;
    private int calendarRead;

    public MainWindow(SqliteMessageRepository repository, ManualImportService importer, AppLog log, DirectReadCache cache,
        ISourceTokenStore sourceTokens, ISourceAdapterFactory adapterFactory, SyncCoordinator coordinator, bool defaultDataDirectory)
    {
        this.repository = repository;
        this.importer = importer;
        this.log = log;
        this.cache = cache;
        this.defaultDataDirectory = defaultDataDirectory;
        this.sourceTokens = sourceTokens; this.adapterFactory = adapterFactory; this.coordinator = coordinator;
        InitializeComponent();
        Closing += MainWindow_Closing;
        CalendarView.OpenEvent += id => ShowReview(id, isCandidate: false);
        Activated += async (_, _) => await RefreshCalendarAsync();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && desktop is not null) HideToTray();
        };
        SentAtBox.Text = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        StorageLabel.Text = $"本地数据目录：{cache.DataDirectory}";
    }

    internal async Task<bool> InitializeAsync()
    {
        await RunAsync(async () =>
        {
            preferences = await Task.Run(async () =>
            {
                var ct = lifetime.Token;
                await repository.InitializeAsync(ct);
                foreach (var source in await repository.ReadSourcesAsync(ct))
                    if (source.Enabled) await repository.SetSourceEnabledAsync(source.Id, false, ct);
                return await repository.ReadReminderPreferencesAsync(ct);
            });
            lifetime.Token.ThrowIfCancellationRequested();
            await ReloadAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            desktop = new DesktopReminderRuntime(Dispatcher, repository, log, ShowToday, ShowMissedReminders,
                ShowSourceNeedingAttention, PauseFromTray, ExitFromTray, RefreshToday);
            SourcesButton.IsEnabled = MoectButton.IsEnabled = AiButton.IsEnabled = ReviewButton.IsEnabled =
                TodayButton.IsEnabled = SettingsButton.IsEnabled = true;
            initialized = true;
            log.Write(Operation.Startup);
            StatusLabel.Text = "本地存储已就绪。";
            await RefreshCalendarAsync();
        });
        return initialized || lifetime.IsCancellationRequested;
    }

    private async void HomeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == HomeTabs && HomeTabs.SelectedItem == CalendarTab) await RefreshCalendarAsync();
    }

    private async Task RefreshCalendarAsync()
    {
        if (!initialized || closing || !IsVisible || HomeTabs.SelectedItem != CalendarTab) return;
        var read = ++calendarRead;
        var ct = lifetime.Token;
        try
        {
            var events = await Task.Run(() => repository.ReadEventsAsync(ct), ct);
            if (read == calendarRead && !ct.IsCancellationRequested) CalendarView.SetEvents(events);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            log.Write(Operation.Failure, errorType: error.GetType().Name);
            if (read == calendarRead && !ct.IsCancellationRequested) CalendarView.ShowLoadError();
        }
    }

    private async void Sample_Click(object sender, RoutedEventArgs e) => await ImportAsync(() =>
        importer.ImportFileAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "messages.synthetic.json"), lifetime.Token));

    private async void Json_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "消息 JSON (*.json)|*.json", Title = "导入统一消息 JSON 数组" };
        if (dialog.ShowDialog(this) == true)
            await ImportAsync(() => importer.ImportFileAsync(dialog.FileName, lifetime.Token));
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageBox.Text;
        var conversation = ConversationBox.Text;
        if (!DateTimeOffset.TryParseExact(SentAtBox.Text, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp))
        {
            StatusLabel.Text = "请填写带时区的时间，例如 2026-11-10T09:00:00+08:00。";
            return;
        }
        await ImportAsync(() => importer.ImportTextAsync(text, conversation, timestamp, lifetime.Token));
    }

    private async Task ImportAsync(Func<Task<ImportResult>> action) => await RunAsync(async () =>
    {
        StatusLabel.Text = "正在校验并保存消息…";
        var result = await Task.Run(action, lifetime.Token);
        var resultText = $"导入完成：新增 {result.Added} 条，重复 {result.Duplicates} 条，撤回更新 {result.Updated} 条，冲突 {result.Conflicts} 条。";
        log.Write(Operation.Import, result.Added, result.Duplicates);
        try
        {
            await ReloadAsync();
            StatusLabel.Text = resultText;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Write(Operation.Failure, errorType: exception.GetType().Name);
            StatusLabel.Text = resultText + " 数据已提交，但列表刷新失败，请稍后点击刷新。";
        }
    });

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await ReloadAsync();
        StatusLabel.Text = "已刷新本地消息。";
        log.Write(Operation.Refresh);
    });

    private async Task ReloadAsync()
    {
        var rows = await Task.Run(() => repository.ReadRecentAsync(200, lifetime.Token));
        var count = await Task.Run(() => repository.CountAsync(lifetime.Token));
        MessagesGrid.ItemsSource = rows.Select(row => new MessageRow(row)).ToArray();
        CountLabel.Text = $"共 {count} 条 · 展示最近 {rows.Count} 条 · 时间按本地时区显示";
        EvidenceBox.Text = "选择一条消息，查看内容、账号别名、来源编号和时间。";
    }

    private void Message_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesGrid.SelectedItem is not MessageRow row) return;
        var stored = row.Stored;
        var message = stored.Message;
        EvidenceBox.Text = $"{message.Text}\n\n来源：{message.Source} / {message.Adapter}  |  账号别名：{message.AccountId}\n"
            + $"会话：{message.ConversationName} ({message.ConversationId})  |  发送者：{message.SenderName}\n"
            + $"发送：{message.SentAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  |  上游编号：{message.MessageId ?? "无（使用复合指纹）"}\n"
            + $"本地证据编号：{stored.Id}  |  撤回：{(message.IsRecalled ? "是" : "否")}  |  状态：{row.DisplayStatus}";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        ImportPanel.IsEnabled = SampleButton.IsEnabled = RefreshButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Write(Operation.Failure, errorType: exception.GetType().Name);
            StatusLabel.Text = exception switch
            {
                JsonException => "导入失败：JSON 格式、必需字段或带时区的时间不符合协议；本批未写入。",
                ArgumentException => "导入失败：请检查消息是否为空、来源字段、时间和大小限制；本批未写入。",
                IOException or UnauthorizedAccessException => "操作失败：无法读写本地文件，请检查路径、访问权限或占用情况。",
                _ => "操作失败：本地存储暂不可用。请检查磁盘空间或文件占用后重试；诊断日志不含消息正文。"
            };
        }
        finally
        {
            busy = false;
            ImportPanel.IsEnabled = SampleButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    private void Sources_Click(object sender, RoutedEventArgs e) => ShowSources();

    private void ShowSourceNeedingAttention(string sourceId)
    {
        if (closing || !initialized || pauseOperation is { IsCompleted: false }) return;
        if (!IsVisible) ShowWindow(this);
        ShowSources();
        sourcesWindow?.NavigateTo(sourceId);
    }

    private void ShowSources()
    {
        if (closing || pauseOperation is { IsCompleted: false }) return;
        if (sourcesWindow?.IsLoaded == true) { ShowWindow(sourcesWindow); return; }
        sourcesWindow = new SourcesWindow(repository, adapterFactory, coordinator,
            ChangeAnalysisScopeAsync, RunCaptureAsync) { Owner = this };
        sourcesWindow.Show();
    }

    private void Moect_Click(object sender, RoutedEventArgs e)
    {
        if (closing || pauseOperation is { IsCompleted: false }) return;
        if (moectWindow?.IsLoaded == true) { ShowWindow(moectWindow); return; }
        moectWindow = new MoectWatchWindow(repository, ChangeAnalysisScopeAsync) { Owner = this };
        moectWindow.Show();
    }

    private void Ai_Click(object sender, RoutedEventArgs e) => ShowAi();

    private void ShowAi()
    {
        if (closing || pauseOperation is { IsCompleted: false }) return;
        if (aiWindow?.IsLoaded == true) { ShowWindow(aiWindow); return; }
        aiWindow = new AiAnalysisWindow(repository, sourceTokens) { Owner = this };
        aiWindow.Show();
    }

    private void Review_Click(object sender, RoutedEventArgs e) => ShowReview(null, false);

    private async void ShowReview(string? id, bool isCandidate)
    {
        if (closing) return;
        if (reviewWindow?.IsLoaded != true)
        {
            reviewWindow = new EventReviewWindow(repository) { Owner = this };
            reviewWindow.EventsChanged += async () => await RefreshCalendarAsync();
        }
        var window = reviewWindow;
        ShowWindow(window);
        if (id is not null) await window.NavigateAsync(id, isCandidate);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => ShowToday();

    private void ShowToday() => OpenToday();

    private void ShowMissedReminders() => OpenToday(showMissed: true);

    private async void OpenToday(bool showMissed = false)
    {
        if (closing || !initialized || pauseOperation is { IsCompleted: false }) return;
        if (!IsVisible)
        {
            WindowState = WindowState.Normal;
            Show();
        }
        if (todayWindow?.IsLoaded != true)
            todayWindow = new TodayWindow(repository, sourceTokens, ShowReview,
                () => desktop?.ReminderVersion ?? 0, version => desktop?.ViewReminders(version)) { Owner = this };
        if (showMissed) todayWindow.SelectMissedReminders();
        ShowWindow(todayWindow);
        await todayWindow.RefreshForOpenAsync();
    }

    private async void RefreshToday()
    {
        CalendarView.RefreshTodayMarker();
        if (todayWindow is { IsVisible: true } window) await window.RefreshCachedAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        if (settingsWindow?.IsLoaded != true)
            settingsWindow = new ReminderSettingsWindow(repository, preferences, defaultDataDirectory, saved =>
            {
                preferences = saved;
                desktop?.RequestCheck();
            }, ShowSources, ShowAi, cache, ClearDirectReadCacheAsync) { Owner = this };
        ShowWindow(settingsWindow);
    }

    private static void ShowWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void HideToTray()
    {
        foreach (Window child in OwnedWindows) child.Hide();
        Hide();
    }

    private async Task ChangeAnalysisScopeAsync(Func<Task> change)
    {
        await sourceChanges.WaitAsync();
        AiButton.IsEnabled = false;
        var analysis = aiWindow?.IsLoaded == true ? aiWindow : null;
        try
        {
            if (analysis is not null) await analysis.SuspendForSourceChangeAsync();
            await change();
        }
        finally
        {
            if (analysis?.IsLoaded == true) analysis.SourceChangeFinished();
            AiButton.IsEnabled = true;
            sourceChanges.Release();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (shutdownReady) return;
        e.Cancel = true;
        if (!explicitExit && !closing && preferences.CloseToTray && desktop is not null)
        {
            _ = Dispatcher.BeginInvoke(HideToTray);
            return;
        }
        if (closing) return;
        closing = true;
        await Dispatcher.Yield();
        if (initialized)
        {
            try { await (pauseOperation ??= PauseCaptureAsync()); }
            catch (Exception error) { log.Write(Operation.Failure, errorType: error.GetType().Name); }
        }
        lifetime.Cancel();
        if (desktop is not null)
        {
            try { await desktop.DisposeAsync(); }
            catch (Exception error) { log.Write(Operation.Failure, errorType: error.GetType().Name); }
        }
        shutdownReady = true;
        Close();
    }

    private void ExitFromTray()
    {
        explicitExit = true;
        Close();
    }

    private async void PauseFromTray()
    {
        if (closing || pauseOperation is { IsCompleted: false }) return;
        try
        {
            pauseOperation = PauseCaptureAsync();
            await pauseOperation;
            StatusLabel.Text = "已暂停采集和 AI 分析。提醒和每日摘要继续运行。";
            if (!closing) desktop?.ShowMessage(StatusLabel.Text);
        }
        catch (Exception error)
        {
            log.Write(Operation.Failure, errorType: error.GetType().Name);
            StatusLabel.Text = "暂停操作未全部完成，请查看来源状态后重试。";
            if (!closing) desktop?.ShowMessage(StatusLabel.Text);
        }
        finally { pauseOperation = null; }
    }

    private async Task<CacheRemovalResult> ClearDirectReadCacheAsync()
    {
        if (closing || pauseOperation is { IsCompleted: false })
            throw new InvalidOperationException("正在暂停采集。");

        CacheRemovalResult result = default;
        pauseOperation = PauseCaptureAsync(async stopped =>
        {
            result = await cache.ClearAfterPauseAsync(stopped);
        });
        try { await pauseOperation; return result; }
        finally { pauseOperation = null; }
    }

    private async Task PauseCaptureAsync(Func<Task, Task>? afterPause = null)
    {
        // Register pauseOperation before starting so UI callbacks cannot start another reader.
        await Dispatcher.Yield();
        captureCancellation?.Cancel();
        var sourceRun = captureOperation;
        var moect = moectWindow is { IsLoaded: true } ? moectWindow : null;
        var today = todayWindow is { IsLoaded: true } ? todayWindow : null;
        var stopMoect = moect?.SuspendCaptureAsync() ?? Task.CompletedTask;
        var stopPolish = today?.SuspendPolishAsync() ?? Task.CompletedTask;
        try
        {
            await ChangeAnalysisScopeAsync(async () =>
            {
                var stopped = Task.WhenAll(StopSourcesAsync(sourceRun), stopMoect, stopPolish);
                if (afterPause is null) await stopped;
                else await afterPause(stopped);
            });
        }
        finally
        {
            if (!closing)
            {
                moect?.EnableCaptureControls();
                today?.EnablePolish();
            }
        }
    }

    private async Task StopSourcesAsync(Task? sourceRun)
    {
        if (sourceRun is not null)
        {
            try { await sourceRun; }
            catch (Exception error) { log.Write(Operation.Failure, errorType: error.GetType().Name); }
        }
        var failures = new List<Exception>();
        try { await coordinator.StopAllAsync(); }
        catch (Exception error) { failures.Add(error); }
        foreach (var source in await Task.Run(() => repository.ReadSourcesAsync()))
        {
            if (!source.Enabled) continue;
            try { await Task.Run(() => repository.SetSourceEnabledAsync(source.Id, false)); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private async Task RunCaptureAsync(Func<CancellationToken, Task> run, CancellationToken ct)
    {
        if (closing || pauseOperation is { IsCompleted: false } || captureCancellation is not null)
            throw new InvalidOperationException("正在处理或暂停采集。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        captureCancellation = cancellation;
        try
        {
            captureOperation = run(cancellation.Token);
            await captureOperation;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { captureOperation = null; captureCancellation = null; }
    }

    private sealed record MessageRow(StoredMessage Stored)
    {
        public string Conversation => Stored.Conversation;
        public string Text => Stored.Text.Replace('\n', ' ').Replace('\r', ' ');
        public string DisplayStatus => Stored.Status switch { "recalled" => "已撤回", "conflict" => "待复核", _ => "已保存" };
        public override string ToString() => $"{Conversation}：{Text}";
    }
}
