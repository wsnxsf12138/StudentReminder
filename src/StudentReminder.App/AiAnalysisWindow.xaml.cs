using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudentReminder.AI;
using StudentReminder.Core;
using StudentReminder.Data;
using Microsoft.Win32;

namespace StudentReminder.App;

public partial class AiAnalysisWindow : Window
{
    private readonly SqliteMessageRepository repository;
    private readonly ISourceTokenStore tokens;
    private readonly ChatCompletionAiProvider provider = new();
    private readonly BundledLocalTriage localTriage = new();
    private readonly AiAnalysisRunner runner;
    private readonly TimeZoneInfo zone = TimeZoneInfo.Local;
    private readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operationCancellation;
    private Task? operation;
    private bool sourceChanging;
    private bool updatingControls;
    private bool ready;
    private bool readingStatistics;
    private IReadOnlyList<string> modelChoices = [];
    private bool modelListFromApi;
    private readonly Stack<AiHistoryCursor?> earlierPages = new();
    private AiHistoryCursor? pageStart;
    private AiHistoryCursor? nextPage;

    public AiAnalysisWindow(SqliteMessageRepository repository, ISourceTokenStore tokens)
    {
        this.repository = repository;
        this.tokens = tokens;
        runner = new(repository, provider, localTriage: localTriage);
        InitializeComponent();
    }

    public bool IsBusy => operation is { IsCompleted: false };
    private string ProviderId => (string)((ComboBoxItem)ProviderBox.SelectedItem).Tag;
    private string ProviderName => (string)((ComboBoxItem)ProviderBox.SelectedItem).Content;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        updatingControls = true;
        ProviderBox.SelectedIndex = 0;
        FromDate.SelectedDate = DateTime.Today.AddDays(-1);
        ToDate.SelectedDate = DateTime.Today;
        updatingControls = false;
        ready = true;
        await RunAsync(async ct =>
        {
            await LoadProviderAsync(ct);
            var settings = await Task.Run(() => repository.ReadHybridSettingsAsync(ct), ct);
            HybridEnabled.IsChecked = settings.Enabled;
            LocalModelPath.Text = string.IsNullOrWhiteSpace(settings.ModelPath) ? FindBundledModel() : settings.ModelPath;
            var batch = settings.Batch ?? new();
            BatchCountBox.Text = batch.Count.ToString(CultureInfo.InvariantCulture);
            BatchWaitBox.Text = batch.WaitSeconds.ToString(CultureInfo.InvariantCulture);
            await LoadScopesAsync(ct);
            RunStatusIcon.Tag = StatusGlyph.Paused;
        });
        if (!IsLoaded) return;
        statsTimer.Tick += StatsTimer_Tick;
        statsTimer.Start();
        await RefreshStatsAsync();
    }

    private async void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updatingControls || ProviderBox.SelectedItem is null) return;
        ClearPreview();
        await RunAsync(LoadProviderAsync);
    }

    private async Task LoadProviderAsync(CancellationToken ct)
    {
        var providerId = ProviderId;
        KeyBox.Clear();
        ModelBox.Text = "";
        ModelSearchBox.Clear();
        ApiAddress.Text = ChatCompletionAiProvider.ApiBaseUrl(providerId);
        var saved = await Task.Run(() => repository.ReadAiProfileAsync(providerId, ct), ct);
        if (saved is null)
        {
            var previous = await Task.Run(() => repository.ReadMoectProfileAsync(ct), ct);
            if (previous.Provider == providerId) saved = new(providerId, previous.Model);
        }
        ct.ThrowIfCancellationRequested();
        string[] presets = providerId == "deepseek" ? ["deepseek-flash", "deepseek-v4-pro"] : ["doubao-seed-2-1-pro-260915"];
        SetModelChoices(presets, saved?.Model ?? "", fromApi: false);
        ProviderHint.Text = providerId == "doubao" ? "可选模型，也可手填接入点 ID（ep-…）。" : "可从列表选择，也可手填。";
    }

    private void SetModelChoices(IReadOnlyList<string> models, string current, bool fromApi)
    {
        modelChoices = models;
        modelListFromApi = fromApi;
        ModelBox.ItemsSource = models.Append(current.Trim()).Where(model => model.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        ModelBox.SelectedIndex = -1;
        ModelBox.Text = current;
        RefreshModelList();
    }

    private void ModelSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ModelList is not null) RefreshModelList();
    }

    private void RefreshModelList()
    {
        var search = ModelSearchBox.Text.Trim();
        var visible = modelChoices.Where(id => id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        ModelList.ItemsSource = visible;
        ModelListCaption.Text = $"{(modelListFromApi ? "查询结果" : "预设模型")} · {visible.Length}/{modelChoices.Count}";
    }

    private void ModelList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is string id) ModelBox.Text = id;
    }

    private void ModelText_Changed(object sender, TextChangedEventArgs e)
    {
        if (ModelList?.SelectedItem is string id && e.OriginalSource is TextBox input &&
            !string.Equals(id, input.Text, StringComparison.Ordinal))
            ModelList.SelectedIndex = -1;
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var providerId = ProviderId;
        var current = ModelBox.Text;
        var key = await ReadApiKeyAsync(providerId, KeyBox.Password.Trim(), ct);
        RunStatus.Text = $"正在向 {ProviderName} 查询模型列表…";
        IReadOnlyList<string> models;
        try { models = await provider.ListModelsAsync(providerId, key, ct); }
        catch (AiFailureException error)
        {
            RunStatus.Text = error.Code is "format" or "model" ? "服务商暂未返回可读取的模型列表。可以选择预设或手填。"
                : AiFailureException.Describe(error.Code) + " 可以选择预设或手填。";
            return;
        }
        ct.ThrowIfCancellationRequested();
        if (models.Count == 0)
        {
            RunStatus.Text = "服务商未返回适用的模型。可以选择预设或手填。";
            return;
        }
        SetModelChoices(models, current, fromApi: true);
        RunStatus.Text = $"已获取 {models.Count} 个模型。选择模型后可测试连接。";
    });

    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        await ReadAndSaveProfileAsync(ct);
        RunStatus.Text = "模型设置和加密密钥已保存。";
    });

    private async void Test_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var (profile, key) = await ReadAndSaveProfileAsync(ct);
        RunStatus.Text = "正在用固定合成文本测试连接…";
        await provider.TestConnectionAsync(profile, key, ct);
        ct.ThrowIfCancellationRequested();
        RunStatus.Text = "连接和 JSON 响应测试通过。";
    });

    private async Task<(AiProfile Profile, string Key)> ReadAndSaveProfileAsync(CancellationToken ct)
    {
        var profile = new AiProfile(ProviderId, ModelBox.Text.Trim());
        var typedKey = KeyBox.Password.Trim();
        var keyId = SourceKeys.Hash("moect-ai", profile.Provider);
        var key = await ReadApiKeyAsync(profile.Provider, typedKey, ct);
        provider.ValidateConfig(profile, key);
        if (typedKey.Length != 0) await tokens.WriteAsync(keyId, key, ct);
        KeyBox.Clear();
        await Task.Run(() => repository.SaveAiProfileAsync(profile, ct), ct);
        ct.ThrowIfCancellationRequested();
        return (profile, key);
    }

    private async Task<string> ReadApiKeyAsync(string providerId, string typedKey, CancellationToken ct) =>
        typedKey.Length == 0 ? await tokens.ReadAsync(SourceKeys.Hash("moect-ai", providerId), ct) ?? "" : typedKey;

    private static string FindBundledModel()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Models");
        foreach (var name in new[] { "Qwen_Qwen3.5-2B-Q4_K_M.gguf", "Qwen_Qwen3.5-4B-Q4_K_M.gguf" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return "";
    }

    private void BrowseLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Qwen3.5 模型", Filter = "GGUF 模型 (*.gguf)|*.gguf" };
        if (dialog.ShowDialog(this) == true) LocalModelPath.Text = dialog.FileName;
    }

    private string RequireLocalModel()
    {
        var path = LocalModelPath.Text.Trim();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择本机存在的 Qwen3.5 GGUF 文件。");
        return path;
    }

    private async void SaveHybrid_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var enabled = HybridEnabled.IsChecked == true;
        var path = enabled ? RequireLocalModel() : LocalModelPath.Text.Trim();
        var storedPath = path.Equals(FindBundledModel(), StringComparison.OrdinalIgnoreCase) ? null : path;
        var batch = ReadBatchSettings();
        await Task.Run(() => repository.SaveHybridSettingsAsync(new(enabled, storedPath, batch), ct), ct);
        RunStatus.Text = "已保存本地初筛设置。";
    });

    private CloudBatchSettings ReadBatchSettings()
    {
        if (!int.TryParse(BatchCountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            !int.TryParse(BatchWaitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var wait))
            throw new ArgumentException("批次条数和最长等待请输入整数。");
        var settings = new CloudBatchSettings(count, wait);
        settings.Validate();
        return settings;
    }

    private async void TestLocalModel_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var path = RequireLocalModel();
        var now = DateTimeOffset.Now;
        var scope = new AiScope("synthetic", "manual", "manual", "synthetic", "合成测试", "group");
        AiWorkItem Example(string id, string text, DateTimeOffset sent) => new(scope, new StoredMessage(id, new()
        {
            Source = "manual", Adapter = "manual", AccountId = "synthetic", ConversationId = "synthetic",
            SenderId = "synthetic", MessageId = id, SentAt = sent, ReceivedAt = now, Text = text
        }, "active"), "pending");
        RunStatus.Text = "正在加载模型并在本机测试…";
        var result = await Task.Run(() => localTriage.EvaluateAsync(path, Example("target", "补充一下，18点前交", now),
            [Example("context", "明天交实验报告", now.AddMinutes(-1))], ct), ct);
        RunStatus.Text = result.Relevant && result.Related.Contains(1) ? "本地模型测试通过，已找出合成通知的关联前文。"
            : "模型已运行，合成样本的关联结果不完整。";
    });

    private async void Scopes_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadScopesAsync);

    private async Task LoadScopesAsync(CancellationToken ct)
    {
        var scopes = await Task.Run(() => repository.ReadAiScopesAsync(ct), ct);
        ct.ThrowIfCancellationRequested();
        AutomaticScopes.ItemsSource = scopes.Where(scope => scope.Adapter is "wxlocal" or "qqlocal").Select(scope => new ScopeChoice(scope)).ToArray();
        HistoryScope.ItemsSource = scopes;
        ClearPreview();
    }

    private async void Automatic_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var scopes = AutomaticScopes.Items.Cast<ScopeChoice>().Where(item => item.Selected).Select(item => item.Scope.Id).ToArray();
        if (scopes.Length == 0) throw new ArgumentException("请勾选至少一个本地直读会话。若列表为空，请先在消息来源保存授权会话。");
        var localModel = HybridEnabled.IsChecked == true ? RequireLocalModel() : null;
        var batch = localModel is null ? null : ReadBatchSettings();
        var (profile, key) = await ReadAndSaveProfileAsync(ct);
        var since = DateTimeOffset.UtcNow;
        var progress = MakeProgress(ct);
        await Task.Run(() => runner.RunAutomaticAsync(profile, key, scopes, since, zone, progress, ct, localModel, batch), ct);
    });

    private void History_Changed(object sender, RoutedEventArgs e)
    {
        if (ready) ClearPreview();
    }

    private void ClearPreview()
    {
        earlierPages.Clear();
        pageStart = nextPage = null;
        PreviewGrid.ItemsSource = null;
        PreviewText.Clear();
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        PageLabel.Text = "";
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        ClearPreview();
        await LoadHistoryAsync(ct);
    });

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (!earlierPages.TryPeek(out var previous)) return;
        await LoadHistoryAsync(ct, previous);
        earlierPages.Pop();
        UpdatePaging();
    });

    private async void Next_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (nextPage is not { } next) return;
        var previous = pageStart;
        await LoadHistoryAsync(ct, next);
        earlierPages.Push(previous);
        UpdatePaging();
    });

    private async Task LoadHistoryAsync(CancellationToken ct, AiHistoryCursor? before = null)
    {
        if (HistoryScope.SelectedItem is not AiScope scope || FromDate.SelectedDate is not { } from ||
            ToDate.SelectedDate is not { } to || to.Date < from.Date || to.Date == DateTime.MaxValue.Date)
            throw new ArgumentException("请选择会话和有效的起止日期。");
        var (page, previews) = await Task.Run(async () =>
        {
            var page = await repository.ReadAnalysisHistoryAsync(scope, StartOfDay(from), StartOfDay(to.AddDays(1)), ct, before);
            var previews = page.Items.Select(row =>
            {
                ct.ThrowIfCancellationRequested();
                return new PreviewRow(AiPreview.Create(row));
            }).ToArray();
            return (page, previews);
        }, ct);
        ct.ThrowIfCancellationRequested();
        pageStart = before;
        nextPage = page.Next;
        PreviewGrid.ItemsSource = previews;
        RunStatus.Text = page.Items.Count == 0 ? "这个范围暂无可预览的文本。" : $"已在本地预览 {page.Items.Count} 条文本。";
        UpdatePaging();
    }

    private void UpdatePaging()
    {
        PreviousPage.IsEnabled = earlierPages.Count > 0;
        NextPage.IsEnabled = nextPage is not null;
        PageLabel.Text = $"第 {earlierPages.Count + 1} 页";
    }

    private DateTimeOffset StartOfDay(DateTime day)
    {
        var time = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(time)) time = time.AddMinutes(1);
        return new(time, zone.GetUtcOffset(time));
    }

    private void Preview_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewGrid.SelectedItem is not PreviewRow row) { PreviewText.Clear(); return; }
        var sent = TimeZoneInfo.ConvertTime(row.Preview.Work.Message.Message.SentAt, zone);
        PreviewText.Text = $"发送时间 {sent:yyyy-MM-dd HH:mm:ss zzz} · 时区 {zone.Id}\n\n{row.Preview.OutboundText}";
    }

    private async void Manual_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var rows = PreviewGrid.Items.Cast<PreviewRow>().Where(row => row.Selected && row.CanSend).Select(row => row.Preview).ToArray();
        if (rows.Length == 0) throw new ArgumentException("请先预览并勾选要提交的历史消息。");
        if (MessageBox.Show(this, $"向 {ProviderName} 提交这 {rows.Length} 条消息的脱敏正文、发送时间和时区？",
            "确认提交", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ct.ThrowIfCancellationRequested();
        var (profile, key) = await ReadAndSaveProfileAsync(ct);
        var progress = MakeProgress(ct);
        await Task.Run(() => runner.RunManualAsync(profile, key, rows, zone, progress, ct), ct);
        var status = RunStatus.Text;
        await LoadHistoryAsync(ct, pageStart);
        RunStatus.Text = status;
    });

    private async void Combined_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var rows = PreviewGrid.Items.Cast<PreviewRow>().Where(row => row.Selected).Select(row => row.Preview).ToArray();
        var preview = AiJointPreview.Create(rows, zone);
        if (!ConfirmCombined(preview.Input, ct)) return;
        ct.ThrowIfCancellationRequested();
        var (profile, key) = await ReadAndSaveProfileAsync(ct);
        var progress = MakeProgress(ct);
        await Task.Run(() => runner.RunCombinedAsync(profile, key, preview, progress, ct), ct);
        var status = RunStatus.Text;
        await LoadHistoryAsync(ct, pageStart);
        RunStatus.Text = status;
    });

    private bool ConfirmCombined(AiInput input, CancellationToken ct)
    {
        var layout = new DockPanel { Margin = new(16) };
        var caption = new TextBlock { Text = $"将向 {ProviderName} 提交以下脱敏片段。E1 为本次分析的主消息。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        DockPanel.SetDock(caption, Dock.Top);
        layout.Children.Add(caption);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 10, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var dialog = new Window { Owner = this, Title = "联合分析出站预览", Width = 720, Height = 465,
            Icon = Icon, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout,
            FontFamily = FontFamily, FontSize = FontSize };
        var send = new Button { Content = "确认发送" };
        send.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(send);
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        layout.Children.Add(new TextBox { Text = ChatCompletionAiProvider.PreviewInput(input), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        using var closePreview = ct.Register(() => Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        return dialog.ShowDialog() == true;
    }

    private Progress<string> MakeProgress(CancellationToken ct) => new(text =>
    {
        if (!ct.IsCancellationRequested && IsLoaded) RunStatus.Text = text;
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || sourceChanging || lifetime.IsCancellationRequested) return;
        RunStatusIcon.Tag = StatusGlyph.None;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationCancellation = cancellation;
        operation = ExecuteAsync(action, cancellation.Token);
        SetControls();
        try { await operation; }
        finally
        {
            operation = null;
            operationCancellation = null;
            localTriage.Dispose();
            SetControls();
            await RefreshStatsAsync();
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            ct.ThrowIfCancellationRequested();
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AiFailureException error)
        {
            RunStatusIcon.Tag = StatusGlyph.Failure;
            RunStatus.Text = AiFailureException.Describe(error.Code) + " 分析已暂停。";
        }
        catch (ArgumentException error) { RunStatus.Text = error.Message; }
        catch (Exception)
        {
            RunStatusIcon.Tag = StatusGlyph.Failure;
            RunStatus.Text = "操作未完成，请检查本地数据库或凭据。分析已暂停。";
        }
    }

    private void SetControls()
    {
        AnalysisTabs.IsEnabled = ready && !IsBusy && !sourceChanging && !lifetime.IsCancellationRequested;
        PauseButton.IsEnabled = IsBusy && !sourceChanging && !lifetime.IsCancellationRequested;
    }

    private async Task CancelOperationAsync()
    {
        operationCancellation?.Cancel();
        if (operation is { } task) await task;
        // RunAsync also clears the operation and restores the controls on the dispatcher.
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        await CancelOperationAsync();
        RunStatusIcon.Tag = StatusGlyph.Paused;
        RunStatus.Text = "分析已暂停。";
    }

    public async Task SuspendForSourceChangeAsync()
    {
        sourceChanging = true;
        SetControls();
        await CancelOperationAsync();
    }

    public async void SourceChangeFinished()
    {
        sourceChanging = false;
        RunStatus.Text = "来源设置已更新，分析已暂停。";
        await RunAsync(async ct =>
        {
            await LoadScopesAsync(ct);
            RunStatusIcon.Tag = StatusGlyph.Paused;
        });
    }

    private async void StatsTimer_Tick(object? sender, EventArgs e) => await RefreshStatsAsync();

    private async Task RefreshStatsAsync()
    {
        if (readingStatistics || lifetime.IsCancellationRequested) return;
        readingStatistics = true;
        try
        {
            var stats = await Task.Run(() => repository.ReadAiStatisticsAsync(DateTimeOffset.UtcNow, zone, lifetime.Token), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            UsageLabel.Text = $"今日请求 {stats.Requests}/{AiLimits.DailyRequests} · 输入 {stats.PromptTokens} / 输出 {stats.CompletionTokens} Token" +
                $" · 用量未知 {stats.MissingUsage} 次 · 已筛过 {stats.Filtered} · 待重试 {stats.Retrying} · 失败 {stats.Failed}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { UsageLabel.Text = "暂时无法读取本地用量。"; }
        finally { readingStatistics = false; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!IsBusy) return;
        e.Cancel = true;
        await SuspendForSourceChangeAsync();
        if (IsLoaded) Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        statsTimer.Stop();
        statsTimer.Tick -= StatsTimer_Tick;
        provider.Dispose();
        localTriage.Dispose();
    }

    private sealed class ScopeChoice(AiScope scope)
    {
        public AiScope Scope { get; } = scope;
        public string Label => Scope.ToString();
        public bool Selected { get; set; }
    }

    private sealed class PreviewRow(AiPreview preview)
    {
        public AiPreview Preview { get; } = preview;
        public bool Selected { get; set; }
        public bool CanSend => Preview.Work.Status is not ("empty" or "extracted");
        public string Snippet => Preview.OutboundText.Replace('\n', ' ').Replace('\r', ' ');
        public string Time => Preview.Work.Message.LocalSentAt.ToString("yyyy-MM-dd HH:mm");
        public StatusGlyph Glyph => Preview.Work.Status is "retry" or "failed" ? StatusGlyph.Failure : StatusGlyph.None;
        public string Status => Preview.Work.Status switch
        {
            "filtered" when Preview.Work.ErrorCode == "local_filtered" => "本地初筛跳过",
            "filtered" => "轻筛跳过", "empty" => "无事项", "extracted" => "已提取",
            "retry" => "待重试 · " + AiFailureException.Describe(Preview.Work.ErrorCode ?? ""),
            "failed" => "失败 · " + AiFailureException.Describe(Preview.Work.ErrorCode ?? ""),
            _ => "待分析"
        };
    }
}
