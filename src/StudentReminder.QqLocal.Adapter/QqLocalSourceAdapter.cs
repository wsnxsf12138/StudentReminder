using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using StudentReminder.Core;
using StudentReminder.Ingestion;

namespace StudentReminder.QqLocal.Adapter;

/// <summary>
/// In-process QQ NT source adapter. Reads the decrypted shadow databases directly via
/// the QqLocal module; before each read it applies the live WAL delta, so newly arrived
/// messages surface in near real time without an external process. Real-time wake-ups are
/// driven by a <see cref="FileSystemWatcher"/> on the -wal files surfaced through
/// <see cref="SubscribeAsync"/>, while the coordinator's polling loop acts as a safety net.
/// </summary>
public sealed class QqLocalSourceAdapter : IDataSourceAdapter
{
    private readonly SourceDefinition source;
    private readonly QqLocalSyncSession? session;

    public QqLocalSourceAdapter(SourceDefinition source, DirectReadCache? cache = null)
    {
        source.Validate();
        this.source = source;
        if (QqPaths.FindAccount() is { } account)
            session = new QqLocalSyncSession(account, source.LookbackDays, cache);
    }

    public string AdapterId => "qqlocal";
    public bool SupportsSubscription => true;

    public Task<SourceHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var provider = ProviderAccount(RequireSession().Account);
            var processes = Process.GetProcessesByName("QQ");
            foreach (var process in processes) process.Dispose();
            if (processes.Length == 0)
                return Task.FromResult(new SourceHealth(false, "offline", DateTimeOffset.UtcNow, provider));
            return Task.FromResult(new SourceHealth(true, "ok", DateTimeOffset.UtcNow, provider));
        }
        catch (SourceFailureException error)
        {
            return Task.FromResult(new SourceHealth(false, error.Code, DateTimeOffset.UtcNow));
        }
    }

    public async Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var session = RequireSession();
            session.EnsureRefreshed();
            using var reader = session.OpenReader();
            return (IReadOnlyList<SourceConversation>)reader.ListConversations()
                .Select(conversation => new SourceConversation(conversation.Id, conversation.Name, conversation.Type))
                .ToArray();
        }, cancellationToken);
    }

    public async Task<MessagePage> FetchMessagesAsync(string conversationId, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var session = RequireSession();
            session.EnsureRefreshed();
            QqCursor? position = null;
            if (cursor is not null)
            {
                try { position = JsonSerializer.Deserialize<QqCursor>(cursor); }
                catch (JsonException) { throw new SourceFailureException("protocol_error"); }
                if (position is null || position.RowId < 0)
                    throw new SourceFailureException("protocol_error");
            }

            using var reader = session.OpenReader();
            var now = DateTimeOffset.UtcNow;
            var group = reader.IsGroup(conversationId)
                ?? throw new SourceFailureException("protocol_error");
            IReadOnlyList<QqMessage> rows = position is null
                ? reader.ReadMessages(conversationId, group, now.AddDays(-source.LookbackDays).ToUnixTimeSeconds())
                : reader.ReadMessagesAfter(conversationId, group, position.RowId);

            var conversationName = reader.GetConversationName(conversationId, group);
            var messages = new List<NormalizedMessage>();
            var last = position;
            var processed = 0;
            foreach (var message in rows)
            {
                processed++;
                if (message.Kind != "text" || string.IsNullOrWhiteSpace(message.Text)) continue;
                last = new QqCursor(message.RowId);
                messages.Add(QqMessageMapper.Map(message, source.AccountAlias, conversationId, conversationName, group, now));
                if (messages.Count >= limit) break;
            }

            var hasMore = processed < rows.Count;
            var next = last is null ? null : JsonSerializer.Serialize(last);
            return new MessagePage(messages, next, hasMore);
        }, cancellationToken);
    }

    public async IAsyncEnumerable<SourceNotification> SubscribeAsync(IReadOnlyList<string> conversationIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var account = RequireSession().Account;

        var channel = Channel.CreateBounded<SourceNotification>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        using var watcher = new FileSystemWatcher(account.NtDbDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            Filter = "*-wal",
        };
        void OnChanged(object sender, FileSystemEventArgs e) =>
            channel.Writer.TryWrite(new SourceNotification("message.new", null, null));
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.EnableRaisingEvents = true;
        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
                yield return notification;
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
        }
    }

    private static string ProviderAccount(QqAccount account) =>
        SourceKeys.Hash("qqlocal-account", account.Qq);

    private QqLocalSyncSession RequireSession()
    {
        if (session is null) throw new SourceFailureException("database_not_ready");
        if (source.ProviderAccount is { } binding && binding != ProviderAccount(session.Account))
            throw new SourceFailureException("account_changed");
        return session;
    }

    public void Dispose() { }

    private sealed record QqCursor(long RowId);
}
