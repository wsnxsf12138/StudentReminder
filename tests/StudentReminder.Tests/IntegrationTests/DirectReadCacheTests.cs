using StudentReminder.Ingestion;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class DirectReadCacheTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("student-reminder-cache-").FullName;

    [Fact]
    public async Task ClearingBothCachesPreservesImportedEvidenceAndOfficialClientFiles()
    {
        var data = Path.Combine(root, "assistant");
        var evidence = Write(data, "messages.db");
        var secret = Write(data, "secrets", "synthetic.dpapi");
        var official = Write(root, "official-client", "message.db");
        Write(data, "wxlocal", "synthetic-wechat", "keys.json");
        Write(data, "wxlocal", "synthetic-wechat", "message_0.db");
        Write(data, "wxlocal", "synthetic-wechat", "wal-cursor.json");
        Write(data, "qqlocal", "synthetic-qq", "nt_msg.db");
        Write(data, "qqlocal", ".snapshots", "temp", "nt_msg.db");

        var cache = new DirectReadCache(data);
        Assert.Equal(new(CacheRemoval.Removed, CacheRemoval.Removed), await cache.ClearAfterPauseAsync(Task.CompletedTask));
        Assert.Equal(new(CacheRemoval.Absent, CacheRemoval.Absent), await cache.ClearAfterPauseAsync(Task.CompletedTask));
        Assert.False(Directory.Exists(cache.WechatDirectory));
        Assert.False(Directory.Exists(cache.QqDirectory));
        Assert.All(new[] { evidence, secret, official }, file => Assert.Equal("synthetic", File.ReadAllText(file)));
    }

    [Fact]
    public async Task CacheSurvivesUntilCaptureHasStopped()
    {
        var cache = new DirectReadCache(root);
        var file = Write(root, "wxlocal", "keys.json");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearing = cache.ClearAfterPauseAsync(stopped.Task);

        Assert.False(clearing.IsCompleted);
        Assert.True(File.Exists(file));
        stopped.SetResult();
        Assert.Equal(CacheRemoval.Removed, (await clearing).Wechat);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task FailedPauseLeavesBothCachesIntact()
    {
        var wechat = Write(root, "wxlocal", "keys.json");
        var qq = Write(root, "qqlocal", "keys.json");
        var cache = new DirectReadCache(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.ClearAfterPauseAsync(Task.FromException(new InvalidOperationException())));
        Assert.True(File.Exists(wechat));
        Assert.True(File.Exists(qq));
    }

    [Fact]
    public async Task LockedCacheIsReportedAndCanBeRetriedIndependently()
    {
        var cache = new DirectReadCache(root);
        var locked = Write(root, "wxlocal", "message_0.db");
        Write(root, "qqlocal", "nt_msg.db");
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new(CacheRemoval.Unavailable, CacheRemoval.Removed),
                await cache.ClearAfterPauseAsync(Task.CompletedTask));
            Assert.True(File.Exists(locked) || File.Exists(Path.Combine(root, "wxlocal.clearing", "message_0.db")));
        }
        Assert.Equal(new(CacheRemoval.Removed, CacheRemoval.Absent), await cache.ClearAfterPauseAsync(Task.CompletedTask));
    }

    [Fact]
    public async Task InterruptedRemovalCanBeRetriedAfterCacheHasBeenRebuilt()
    {
        var cache = new DirectReadCache(root);
        var leftover = Write(root, "wxlocal.clearing", "message_0.db");
        var active = Write(root, "wxlocal", "keys.json");
        Assert.Equal(new(CacheRemoval.Removed, CacheRemoval.Absent), await cache.ClearAfterPauseAsync(Task.CompletedTask));
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(active));
    }

    [Fact]
    public async Task LegacyProbeExportsSurviveCacheRemoval()
    {
        var cache = new DirectReadCache(root);
        var export = Write(root, "wxlocal", "exports", "synthetic.json");
        var key = Write(root, "wxlocal", "keys.json");
        Assert.Equal(CacheRemoval.Removed, (await cache.ClearAfterPauseAsync(Task.CompletedTask)).Wechat);
        Assert.Equal(CacheRemoval.Absent, (await cache.ClearAfterPauseAsync(Task.CompletedTask)).Wechat);
        Assert.Equal("synthetic", File.ReadAllText(export));
        Assert.False(File.Exists(key));
    }

    [Fact]
    public async Task CleanupRefusesToDeleteAnotherInstanceInsideACacheDirectory()
    {
        var cache = new DirectReadCache(root);
        var nested = Write(root, "wxlocal", "other-instance", "messages.db");
        Write(root, "qqlocal", "keys.json");
        Assert.Equal(new(CacheRemoval.UnsafePath, CacheRemoval.Removed), await cache.ClearAfterPauseAsync(Task.CompletedTask));
        Assert.True(File.Exists(nested));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionIsRefusedWithoutTouchingTheTarget(bool nested)
    {
        var data = Path.Combine(root, "assistant");
        var cache = new DirectReadCache(data);
        var official = Write(root, "official-client", "message.db");
        var link = nested ? Path.Combine(cache.WechatDirectory, "linked") : cache.WechatDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Write(data, "qqlocal", "keys.json");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/d /c mklink /J \"{link}\" \"{Path.GetDirectoryName(official)}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal(new(CacheRemoval.UnsafePath, CacheRemoval.Removed),
                await cache.ClearAfterPauseAsync(Task.CompletedTask));
            Assert.Equal("synthetic", File.ReadAllText(official));
            Assert.True(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void WarnsWhenDataDirectoryIsInARecognizedSyncFolder()
    {
        Assert.True(new DirectReadCache(Path.Combine(root, "OneDrive - Synthetic", "StudentReminder")).IsInKnownSyncFolder());
        Assert.False(new DirectReadCache(Path.Combine(root, "OneDriveBackup", "StudentReminder")).IsInKnownSyncFolder());
    }

    private static string Write(string root, params string[] path)
    {
        var file = Path.Combine(root, Path.Combine(path));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "synthetic");
        return file;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
