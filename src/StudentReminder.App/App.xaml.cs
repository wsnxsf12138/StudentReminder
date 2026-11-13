using System.IO;
using System.Windows;
using StudentReminder.Data;
using StudentReminder.Ingestion;
using StudentReminder.Core;
using StudentReminder.WxLocal.Adapter;
using StudentReminder.QqLocal.Adapter;

namespace StudentReminder.App;

public partial class App : Application
{
    private SyncCoordinator? coordinator;
    private Mutex? instanceMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var defaultDirectory = DirectReadCache.DefaultDataDirectory;
#if STUDENTREMINDER_TRIAL
        var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var dataDirectory = Path.Combine(defaultDirectory + "-Trial", SourceKeys.Hash(packageDirectory.ToUpperInvariant()));
#else
        var dataDirectory = defaultDirectory;
#endif
        var startup = e.Args.FirstOrDefault() == "--startup";
        var arguments = startup ? e.Args[1..] : e.Args;
        if (arguments.Length > 0)
        {
            if (arguments.Length != 2 || arguments[0] != "--data-dir")
            {
                MessageBox.Show("启动参数：[--startup] [--data-dir <本地数据目录>]", "学生事务助理");
                Shutdown(1);
                return;
            }
            dataDirectory = Path.GetFullPath(arguments[1]);
        }
        dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var repository = new SqliteMessageRepository(Path.Combine(dataDirectory, "messages.db"));
        instanceMutex = new Mutex(false, "Local\\StudentReminder-" + SourceKeys.Hash(Path.GetFullPath(dataDirectory).ToUpperInvariant()));
        bool ownsMutex;
        try { ownsMutex = instanceMutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            instanceMutex.Dispose(); instanceMutex = null;
            if (!startup) MessageBox.Show("此数据目录已有一个学生事务助理实例正在运行。", "学生事务助理");
            Shutdown(0); return;
        }
        var tokens = new DpapiSourceTokenStore(dataDirectory);
        var cache = new DirectReadCache(dataDirectory);
        var factory = new RoutingSourceAdapterFactory(new WxLocalSourceAdapterFactory(cache), new QqLocalSourceAdapterFactory(cache));
        coordinator = new SyncCoordinator(repository, factory);
        var window = new MainWindow(repository, new ManualImportService(repository), new AppLog(dataDirectory), cache,
            tokens, factory, coordinator, dataDirectory.Equals(defaultDirectory, StringComparison.OrdinalIgnoreCase));
        MainWindow = window;
        if (!startup) window.Show();
        if (!await window.InitializeAsync() && startup) window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
