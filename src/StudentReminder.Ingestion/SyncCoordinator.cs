using System.Collections.Concurrent;
using StudentReminder.Core;

namespace StudentReminder.Ingestion;

public interface ISourceAdapterFactory
{
    Task<IDataSourceAdapter> CreateAsync(SourceDefinition source, CancellationToken cancellationToken = default);
}

/// <summary>Explicit starts only. Each successful page is checkpointed with its messages.</summary>
public sealed class SyncCoordinator(ISyncStore store, ISourceAdapterFactory factory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Worker> workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private bool disposed;

    public async Task<ImportResult> RunOnceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var gate = gates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var source = (await store.ReadSourcesAsync(cancellationToken)).SingleOrDefault(row => row.Id == sourceId);
            if (source is null || !source.Enabled) return new(0, 0, 0, 0);
            var selected = (await store.ReadConversationsAsync(sourceId, cancellationToken)).Where(row => row.Selected).ToArray();
            if (selected.Length == 0) return new(0, 0, 0, 0);
            using var adapter = await factory.CreateAsync(source, cancellationToken);
            var health = await adapter.HealthAsync(cancellationToken);
            if (!health.Available) throw new SourceFailureException(health.StatusCode);
            var total = new ImportResult(0, 0, 0, 0);
            foreach (var conversation in selected)
            {
                var cursor = await store.ReadCursorAsync(sourceId, conversation.Id, cancellationToken);
                for (var pageNumber = 0; ; pageNumber++)
                {
                    if (pageNumber >= 200) throw new SourceFailureException("protocol_error");
                    var page = await adapter.FetchMessagesAsync(conversation.Id, cursor, 200, cancellationToken);
                    if (page.HasMore && (page.NextCursor is null || page.NextCursor == cursor)) throw new SourceFailureException("protocol_error");
                    // The selected metadata, not an event payload, determines which authorized conversation is saved.
                    page = page with { Messages = page.Messages.Select(message => message with
                    { ConversationName = conversation.Name, ConversationType = conversation.Type }).ToArray() };
                    var result = await store.CommitPageAsync(source, conversation.Id, page, cancellationToken);
                    total = new(total.Added + result.Added, total.Duplicates + result.Duplicates,
                        total.Updated + result.Updated, total.Conflicts + result.Conflicts);
                    if (!page.HasMore) break;
                    cursor = page.NextCursor;
                }
            }
            return total;
        }
        catch (SourceFailureException exception)
        {
            await store.SetHealthAsync(sourceId, exception.Code, false, cancellationToken);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (workers.ContainsKey(sourceId)) return;
            var source = (await store.ReadSourcesAsync(cancellationToken)).SingleOrDefault(row => row.Id == sourceId);
            if (source is null || !source.Enabled) return;
            if (!(await store.ReadConversationsAsync(sourceId, cancellationToken)).Any(row => row.Selected)) return;
            var worker = new Worker();
            workers.Add(sourceId, worker);
            worker.Task = Task.Run(() => WorkAsync(source, worker));
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(string sourceId)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (workers.Remove(sourceId, out var worker)) await StopWorkerAsync(worker);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAllAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopWorkersAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task StopWorkersAsync()
    {
        var active = workers.Values.ToArray();
        workers.Clear();
        await Task.WhenAll(active.Select(StopWorkerAsync));
    }

    private static async Task StopWorkerAsync(Worker worker)
    {
        try { await worker.Cancellation.CancelAsync(); }
        finally
        {
            try { await worker.Task; }
            catch (OperationCanceledException) { }
            finally { worker.Cancellation.Dispose(); worker.Wake.Dispose(); }
        }
    }

    private async Task WorkAsync(SourceDefinition source, Worker worker)
    {
        var cancellationToken = worker.Cancellation.Token;
        try
        {
            // Subscribe independently, then pull. Reconnect wakes the pull loop even if no event was delivered.
            var polling = PollAsync(source, worker);
            if (!source.UsePush) { await polling; return; }
            var subscription = StreamAsync(source, worker);
            try { await await Task.WhenAny(subscription, polling); }
            finally
            {
                await worker.Cancellation.CancelAsync();
                try { await Task.WhenAll(subscription, polling); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SourceFailureException exception)
        {
            await store.SetHealthAsync(source.Id, exception.Code, false, CancellationToken.None);
        }
        catch (Exception)
        {
            await store.SetHealthAsync(source.Id, "error", false, CancellationToken.None);
        }
    }

    private async Task PollAsync(SourceDefinition source, Worker worker)
    {
        var token = worker.Cancellation.Token;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var current = (await store.ReadSourcesAsync(token)).Single(row => row.Id == source.Id);
                if (!current.Enabled || current.Revision != source.Revision) return;
                await RunOnceAsync(source.Id, token);
                failures = 0;
                if (worker.UnresolvedRecall) await store.SetHealthAsync(source.Id, "recall_unresolved", false, token);
                await worker.Wake.WaitAsync(TimeSpan.FromSeconds(source.PollSeconds), token);
            }
            catch (SourceFailureException exception)
            {
                await store.SetHealthAsync(source.Id, exception.Code, false, token);
                if (exception.Code is "missing_token" or "unauthorized" or "license_invalid" or "account_changed") return;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures++, 6))), token);
            }
        }
    }

    private async Task StreamAsync(SourceDefinition source, Worker worker)
    {
        var token = worker.Cancellation.Token;
        var selected = (await store.ReadConversationsAsync(source.Id, token)).Where(row => row.Selected).Select(row => row.Id).ToArray();
        if (selected.Length == 0) return;
        using var adapter = await factory.CreateAsync(source, token);
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Wake(worker);
                await foreach (var notification in adapter.SubscribeAsync(selected, token))
                {
                    failures = 0;
                    if (notification.Kind == "message.revoke" && notification.ConversationId is { } conversationId)
                    {
                        if (notification.ReliableMessageId && notification.MessageId is { Length: > 0 } messageId)
                            await store.RecallAsync(source, conversationId, messageId, token);
                        else
                        {
                            worker.UnresolvedRecall = true;
                            await store.SetHealthAsync(source.Id, "recall_unresolved", false, token);
                        }
                    }
                    Wake(worker);
                }
                await store.SetHealthAsync(source.Id, "backoff", false, token);
            }
            catch (SourceFailureException exception)
            {
                await store.SetHealthAsync(source.Id, exception.Code, false, token);
                if (exception.Code is "missing_token" or "unauthorized" or "license_invalid") return;
            }
            catch (IOException) { await store.SetHealthAsync(source.Id, "backoff", false, token); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures++, 6))), token);
        }
    }

    private static void Wake(Worker worker)
    {
        try { worker.Wake.Release(); } catch (SemaphoreFullException) { }
    }

    private sealed class Worker
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim Wake { get; } = new(0, 1);
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile bool UnresolvedRecall;
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            disposed = true;
            await StopWorkersAsync();
        }
        finally { lifecycle.Release(); }
    }
}
