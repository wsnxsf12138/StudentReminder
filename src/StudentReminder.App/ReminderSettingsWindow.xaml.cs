using System.Globalization;
using System.Windows;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Ingestion;

namespace StudentReminder.App;

public partial class ReminderSettingsWindow : Window
{
    private readonly SqliteMessageRepository store;
    private readonly Action<ReminderPreferences> changed;
    private readonly Action openSources;
    private readonly Action openAi;
    private readonly Func<Task<CacheRemovalResult>> clearCache;
    private bool? startup;

    internal ReminderSettingsWindow(SqliteMessageRepository store, ReminderPreferences preferences, bool defaultDataDirectory,
        Action<ReminderPreferences> changed, Action openSources, Action openAi, DirectReadCache cache,
        Func<Task<CacheRemovalResult>> clearCache)
    {
        this.store = store;
        this.changed = changed;
        this.openSources = openSources;
        this.openAi = openAi;
        this.clearCache = clearCache;
        InitializeComponent();
        CachePaths.Text = $"微信：{cache.WechatDirectory}\nQQ：{cache.QqDirectory}";
        if (cache.IsInKnownSyncFolder()) SyncFolderWarning.Visibility = Visibility.Visible;
        StartRules.Text = string.Join(", ", preferences.BeforeStartMinutes);
        DeadlineRules.Text = string.Join(", ", preferences.BeforeDeadlineMinutes);
        DigestTimeBox.Text = preferences.DigestTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        CloseToTray.IsChecked = preferences.CloseToTray;
        if (defaultDataDirectory)
        {
            try { startup = LoginStartup.IsEnabled(); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            { Status.Text = "无法读取 Windows 登录启动设置。"; }
        }
        else StartupBox.ToolTip = "登录启动仅适用于默认数据目录。";
        StartupBox.IsEnabled = startup.HasValue;
        StartupBox.IsChecked = startup == true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        var requestedStartup = StartupBox.IsChecked == true;
        try
        {
            if (!TimeOnly.TryParseExact(DigestTimeBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)) throw new ArgumentException("摘要时间请填写为 HH:mm，例如 07:30。");
            var preferences = new ReminderPreferences
            {
                BeforeStartMinutes = ParseRules(StartRules.Text), BeforeDeadlineMinutes = ParseRules(DeadlineRules.Text),
                DigestTime = time, CloseToTray = CloseToTray.IsChecked == true
            };
            await Task.Run(() => store.SaveReminderPreferencesAsync(preferences));
            changed(preferences);
            if (startup is { } previous && previous != requestedStartup)
            {
                try
                {
                    LoginStartup.SetEnabled(requestedStartup);
                    startup = requestedStartup;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException
                    or System.IO.IOException or InvalidOperationException)
                {
                    StartupBox.IsChecked = previous;
                    Status.Text = "提醒和摘要设置已保存。Windows 登录启动设置未能更新，请检查启动路径或权限。";
                    return;
                }
            }
            Status.Text = "设置已保存。";
        }
        catch (ArgumentException error) { Status.Text = error.Message; }
        catch (Exception) { Status.Text = "无法保存本地设置，请重试。"; }
        finally { SaveButton.IsEnabled = true; }
    }

    private static int[] ParseRules(string text) => string.IsNullOrWhiteSpace(text) ? [] :
        text.Split([',', '，'], StringSplitOptions.TrimEntries).Select(value =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                ? minutes : throw new ArgumentException("提醒时间请输入用逗号分隔的整数分钟。")).ToArray();

    private void Sources_Click(object sender, RoutedEventArgs e) => openSources();
    private void Ai_Click(object sender, RoutedEventArgs e) => openAi();

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this,
            "应用将暂停采集和分析，清除本实例的微信与 QQ 解密副本、密钥、缓存游标和临时快照。已入库消息、事项、同步进度和 API Key 会保留。下次读取会话或手动同步时会重建缓存。",
            "清除直读缓存", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        ClearCacheButton.IsEnabled = false;
        CacheStatus.Text = "正在暂停采集和分析并清理缓存…";
        try
        {
            var result = await clearCache();
            CacheStatus.Text = $"微信：{Describe(result.Wechat)}\nQQ：{Describe(result.Qq)}\n采集和分析已暂停。";
        }
        catch (Exception) { CacheStatus.Text = "暂停操作未完成，未开始删除。请检查来源状态后重试。"; }
        finally { ClearCacheButton.IsEnabled = true; }
    }

    private static string Describe(CacheRemoval result) => result switch
    {
        CacheRemoval.Removed => "已清除",
        CacheRemoval.Absent => "没有缓存",
        CacheRemoval.UnsafePath => "路径含链接、助理数据库或其他异常目录，请检查后重试",
        _ => "未清除干净，请关闭占用文件的程序或检查权限后重试"
    };
}
