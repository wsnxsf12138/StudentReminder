using System.Runtime.CompilerServices;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Ingestion;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class SyncCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "StudentReminderCoordinatorTests", Guid.NewGuid().ToString("N"));
    private SqliteMessageRepository Repository => new(Path.Combine(directory, "messages.db"));

    private sealed class Factory(Func<SourceDefinition, IDataSourceAdapter> create) : ISourceAdapterFactory
    {
        public int Calls;
        public Task<IDataSourceAdapter> CreateAsync(SourceDefinition source, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(create(source)); }
    }

    private sealed class Adapter(Func<string?, CancellationToken, Task<MessagePage>> fetch) : IDataSourceAdapter
    {
        public string AdapterId => "synthetic";
        public bool SupportsSubscription => false;
        public Task<SourceHealth> HealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SourceHealth(true, "ok", DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MessagePage> FetchMessagesAsync(string conversationId, string? cursor, int limit, CancellationToken cancellationToken = default)
        { Assert.Equal("synthetic-group", conversationId); return fetch(cursor, cancellationToken); }
        public async IAsyncEnumerable<SourceNotification> SubscribeAsync(IReadOnlyList<string> conversationIds,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }

    [Fact]
    public async Task DisabledSourceAndEmptySelectionMakeNoAdapterOrNetworkCalls()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = SyncStoreTests.Definition();
        await repository.SaveSourceAsync(source);
        var factory = new Factory(_ => throw new InvalidOperationException("No adapter should be created"));
        await using var coordinator = new SyncCoordinator(repository, factory);
        await coordinator.RunOnceAsync(source.Id);
        await repository.SetSourceEnabledAsync(source.Id, true);
        await coordinator.RunOnceAsync(source.Id);
        await coordinator.StartAsync(source.Id);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task DisconnectedPageResumesFromLastCommittedCursorWithoutDuplicateEvidence()
    {
        var repository = Repository;
        var source = await SyncStoreTests.ConfigureAsync(repository);
        var failOnce = true;
        var factory = new Factory(_ => new Adapter((cursor, _) =>
        {
            if (cursor is null) return Task.FromResult(new MessagePage([SyncStoreTests.Message(source, "m1")], "page-two", true));
            Assert.Equal("page-two", cursor);
            if (failOnce) { failOnce = false; throw new SourceFailureException("offline"); }
            return Task.FromResult(new MessagePage([SyncStoreTests.Message(source, "m1"), SyncStoreTests.Message(source, "m2")], "complete", false));
        }));
        await using var coordinator = new SyncCoordinator(repository, factory);
        await Assert.ThrowsAsync<SourceFailureException>(() => coordinator.RunOnceAsync(source.Id));
        Assert.Equal(1, await repository.CountAsync());
        Assert.Equal("page-two", await repository.ReadCursorAsync(source.Id, "synthetic-group"));
        var result = await coordinator.RunOnceAsync(source.Id);
        Assert.Equal(new ImportResult(1, 1, 0, 0), result);
        Assert.Equal(2, await repository.CountAsync());
        Assert.Equal("complete", await repository.ReadCursorAsync(source.Id, "synthetic-group"));
    }

    [Fact]
    public async Task SelectionChangedDuringHttpRequestRejectsResponseAndCursor()
    {
        var repository = Repository;
        var source = await SyncStoreTests.ConfigureAsync(repository);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory(_ => new Adapter(async (_, token) =>
        {
            received.SetResult(); await release.Task.WaitAsync(token);
            return new([SyncStoreTests.Message(source)], "must-not-commit", false);
        }));
        await using var coordinator = new SyncCoordinator(repository, factory);
        var pending = coordinator.RunOnceAsync(source.Id);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await repository.SelectConversationsAsync(source.Id, []);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, await repository.CountAsync());
        Assert.Null(await repository.ReadCursorAsync(source.Id, "synthetic-group"));
    }

    [Fact]
    public async Task StopAllCancelsAndWaitsForBothPlatformWorkers()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        foreach (var source in new[] { SyncStoreTests.Definition("wxlocal"), SyncStoreTests.Definition("qqlocal") })
        {
            await repository.SaveSourceAsync(source);
            await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成群", "group")]);
            await repository.SelectConversationsAsync(source.Id, ["synthetic-group"]);
            await repository.SetSourceEnabledAsync(source.Id, true);
        }

        var started = 0;
        var stopped = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory(_ => new Adapter(async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Increment(ref stopped); }
            throw new InvalidOperationException();
        }));

        await using var coordinator = new SyncCoordinator(repository, factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var source in await repository.ReadSourcesAsync(timeout.Token))
            await coordinator.StartAsync(source.Id, timeout.Token);
        await bothStarted.Task.WaitAsync(timeout.Token);
        await coordinator.StopAllAsync().WaitAsync(timeout.Token);
        Assert.Equal(2, stopped);
        Assert.Equal(0, await repository.CountAsync(timeout.Token));
    }

    [Fact]
    public async Task StopCancelsInFlightPollingAndWaitsForItBeforeReturning()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = SyncStoreTests.Definition();
        await repository.SaveSourceAsync(source);
        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成群", "group")]);
        await repository.SelectConversationsAsync(source.Id, ["synthetic-group"]);
        await repository.SetSourceEnabledAsync(source.Id, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        var factory = new Factory(_ => new Adapter(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled = token.IsCancellationRequested; }
            throw new InvalidOperationException();
        }));
        await using var coordinator = new SyncCoordinator(repository, factory);
        await coordinator.StartAsync(source.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(source.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await repository.SetSourceEnabledAsync(source.Id, false);
        Assert.True(canceled);
        Assert.Equal(0, await repository.CountAsync());
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task WritingFailureDoesNotAdvanceSyncCheckpoint()
    {
        var repository = Repository;
        var source = await SyncStoreTests.ConfigureAsync(repository);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "messages.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER simulated_failure BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'synthetic'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var factory = new Factory(_ => new Adapter((_, _) => Task.FromResult(new MessagePage([SyncStoreTests.Message(source)], "not-committed", false))));
        await using var coordinator = new SyncCoordinator(repository, factory);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => coordinator.RunOnceAsync(source.Id));
        Assert.Null(await repository.ReadCursorAsync(source.Id, "synthetic-group"));
        Assert.Equal(0, await repository.CountAsync());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
