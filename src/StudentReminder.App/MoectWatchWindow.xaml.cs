using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Ingestion;

namespace StudentReminder.App;

public partial class MoectWatchWindow : Window
{
    private readonly SqliteMessageRepository repository;
    private readonly Func<Func<Task>, Task> changeAnalysisScope;
    private readonly DispatcherTimer timer = new();
    private readonly CancellationTokenSource lifetime = new();
    private MoectGroup? group;
    private MoectWatchProfile? activeProfile;
    private CancellationTokenSource? currentCancellation;
    private Task? currentRun;
    private bool preparing;
    private bool ready;

    public MoectWatchWindow(SqliteMessageRepository repository, Func<Func<Task>, Task> changeAnalysisScope)
    {
        this.repository = repository;
        this.changeAnalysisScope = changeAnalysisScope;
        InitializeComponent();
        timer.Tick += Timer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        try
        {
            var profile = await Task.Run(() => repository.ReadMoectProfileAsync(lifetime.Token));
            if (lifetime.IsCancellationRequested) return;
            group = profile.Group;
            GroupLabel.Text = group?.Name ?? "尚未选择授权群";
            FolderBox.Text = profile.Folder;
            MinutesBox.Text = profile.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            AliasBox.Text = profile.AccountAlias;
            if (group is not null) ResultsGrid.ItemsSource = await Task.Run(() => repository.ReadMoectResultsAsync(profile, lifetime.Token));
            StatusLabel.Text = "已暂停。选择导出目录和授权群后，可以立即检查或开始定时导入。";
            ready = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusLabel.Text = "无法读取本地导入设置。"; }
        finally { SetControls(); }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Moect 导出目录" };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private async void ChooseGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择一个授权群的 Moect JSON 导出", Filter = "Moect JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        preparing = true;
        SetControls();
        try
        {
            group = await MoectInbox.ReadGroupAsync(dialog.FileName, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            GroupLabel.Text = group.Name;
            if (string.IsNullOrWhiteSpace(FolderBox.Text)) FolderBox.Text = Path.GetDirectoryName(dialog.FileName)!;
            ResultsGrid.ItemsSource = null;
            StatusLabel.Text = "已选择授权群。请确认目录是 Moect 后续导出的根目录。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            StatusLabel.Text = "无法读取该样本，请选择完整的 Moect 单群 JSON。";
        }
        finally { preparing = false; SetControls(); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await PrepareAndRunAsync(schedule: true);

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (activeProfile is { } profile) await BeginRunAsync(profile);
        else await PrepareAndRunAsync(schedule: false);
    }

    private bool captureBlocked;
    private int captureGeneration;

    private async Task PrepareAndRunAsync(bool schedule)
    {
        if (captureBlocked || preparing || currentRun is not null) return;
        var generation = captureGeneration;
        preparing = true;
        SetControls();
        try
        {
            if (!int.TryParse(MinutesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval))
                throw new ArgumentException("检查间隔请输入 1～1440 分钟。");
            var previous = await Task.Run(() => repository.ReadMoectProfileAsync(lifetime.Token));
            var profile = previous with
            {
                Folder = FolderBox.Text, Group = group, AccountAlias = AliasBox.Text.Trim(), IntervalMinutes = interval
            };
            profile.Validate();
            Task SaveAsync() => Task.Run(() => repository.SaveMoectProfileAsync(profile, lifetime.Token));
            if (previous.Group?.Id != profile.Group?.Id || previous.AccountAlias != profile.AccountAlias)
                await changeAnalysisScope(SaveAsync);
            else await SaveAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            if (generation != captureGeneration) return;
            if (schedule)
            {
                activeProfile = profile;
                timer.Interval = TimeSpan.FromMinutes(profile.IntervalMinutes);
                timer.Start();
                UpdateNext();
            }
            preparing = false;
            await BeginRunAsync(profile);
        }
        catch (ArgumentException error) { StatusLabel.Text = error.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusLabel.Text = "无法保存本地导入设置。"; }
        finally { preparing = false; SetControls(); }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (activeProfile is not { } profile) return;
        UpdateNext();
        await BeginRunAsync(profile);
    }

    private async Task BeginRunAsync(MoectWatchProfile profile)
    {
        if (currentRun is not null || lifetime.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        currentCancellation = cancellation;
        StatusLabel.Text = "正在检查 Moect 导出目录…";
        currentRun = RunCycleAsync(profile, cancellation.Token);
        SetControls();
        try { await currentRun; }
        finally { currentRun = null; currentCancellation = null; SetControls(); }
    }

    private async Task RunCycleAsync(MoectWatchProfile profile, CancellationToken ct)
    {
        try
        {
            var scan = await Task.Run(() => MoectInbox.ScanAsync(profile,
                (digest, messages, token) => repository.ImportMoectExportAsync(profile, digest, messages, token), ct), ct);
            var status = $"本轮找到 {scan.Files} 份授权导出，新增 {scan.Added} 条，重复 {scan.Duplicates} 条，冲突 {scan.Conflicts} 条，跳过 {scan.Skipped} 份。";
            ResultsGrid.ItemsSource = await Task.Run(() => repository.ReadMoectResultsAsync(profile, ct), ct);
            StatusLabel.Text = status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { StatusLabel.Text = "本轮未完成，请检查导出目录或本地数据库。下轮可重试。"; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        StopSchedule();
        if (currentRun is { } task) await task;
        StatusLabel.Text = "已暂停定时导入。";
        SetControls();
    }

    public async Task SuspendCaptureAsync()
    {
        captureBlocked = true;
        captureGeneration++;
        StopSchedule();
        if (currentRun is { } task) await task;
        StatusLabel.Text = "已暂停定时导入。";
        SetControls();
    }

    public void EnableCaptureControls()
    {
        captureBlocked = false;
        SetControls();
    }

    private void StopSchedule()
    {
        timer.Stop();
        activeProfile = null;
        currentCancellation?.Cancel();
        NextLabel.Text = "";
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopSchedule();
        lifetime.Cancel();
        if (currentRun is not { IsCompleted: false } task) return;
        e.Cancel = true;
        await task;
        if (IsLoaded) Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopSchedule();
        lifetime.Cancel();
        timer.Tick -= Timer_Tick;
    }

    private void UpdateNext() => NextLabel.Text = $"下次检查 {DateTime.Now.Add(timer.Interval):HH:mm:ss}";

    private void SetControls()
    {
        var idle = ready && !captureBlocked && !preparing && currentRun is null && !lifetime.IsCancellationRequested;
        SettingsPanel.IsEnabled = StartButton.IsEnabled = idle && activeProfile is null;
        RunButton.IsEnabled = idle;
        PauseButton.IsEnabled = activeProfile is not null || currentRun is not null;
    }
}
