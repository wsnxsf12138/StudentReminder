using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class SyncStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "StudentReminderSyncTests", Guid.NewGuid().ToString("N"));
    private SqliteMessageRepository Repository => new(Path.Combine(directory, "messages.db"));
    internal static SourceDefinition Definition(string adapter = "wxlocal", string alias = "synthetic-account") => new()
    { Adapter = adapter, AccountAlias = alias };

    internal static NormalizedMessage Message(SourceDefinition source, string id = "m1", string conversation = "synthetic-group") =>
        ImportTests.Example() with { Source = source.Platform, Adapter = source.Adapter, AccountId = source.AccountAlias,
            ConversationId = conversation, MessageId = id };

    [Fact]
    public async Task WxLocalDiscoveryPersistsConversationsAndBindsTheDetectedAccount()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = new SourceDefinition { Adapter = "wxlocal", AccountAlias = "synthetic-wx", Port = 0 };
        await repository.SaveSourceAsync(source);
        var account = SourceKeys.Hash("wxlocal-account", "synthetic-account_ab12");
        var group = new SourceConversation("synthetic-group", "合成课程群", "group");

        await repository.MergeConversationsAsync(source.Id, [group], providerAccount: account);
        await repository.SelectConversationsAsync(source.Id, [group.Id]);
        await repository.MergeConversationsAsync(source.Id, [group], providerAccount: account);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MergeConversationsAsync(source.Id,
            [new("other-group", "另一个合成会话", "group")], providerAccount: SourceKeys.Hash("other-account")));

        Assert.Equal(account, Assert.Single(await repository.ReadSourcesAsync()).ProviderAccount);
        Assert.True(Assert.Single(await repository.ReadConversationsAsync(source.Id)).Selected);
        Assert.Equal(0, await repository.CountAsync());
    }

    [Fact]
    public async Task SourcesAndEveryConversationStartDisabledIncludingPrivateChats()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = Definition();
        await repository.SaveSourceAsync(source);
        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成群", "group"), new("synthetic-private", "合成私聊", "private")]);
        Assert.False(Assert.Single(await repository.ReadSourcesAsync()).Enabled);
        Assert.False(Assert.Single(await repository.ReadSourcesAsync()).UsePush);
        Assert.All(await repository.ReadConversationsAsync(source.Id), conversation => Assert.False(conversation.Selected));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CommitPageAsync(source, "synthetic-group", new([Message(source)], "{}", false)));
        Assert.Equal(0, await repository.CountAsync());
        Assert.Null(await repository.ReadCursorAsync(source.Id, "synthetic-group"));
    }

    [Fact]
    public async Task CheckpointAndMessagesCommitTogetherAndStaleConfigurationCannotWrite()
    {
        var repository = Repository;
        var source = await ConfigureAsync(repository);
        var page = new MessagePage([Message(source)], "checkpoint-one", false);
        Assert.Equal(1, (await repository.CommitPageAsync(source, "synthetic-group", page)).Added);
        Assert.Equal("checkpoint-one", await repository.ReadCursorAsync(source.Id, "synthetic-group"));
        await repository.SetSourceEnabledAsync(source.Id, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CommitPageAsync(source, "synthetic-group", page with
        { Messages = [Message(source, "m2")], NextCursor = "checkpoint-two" }));
        Assert.Equal(1, await repository.CountAsync());
        Assert.Equal("checkpoint-one", await repository.ReadCursorAsync(source.Id, "synthetic-group"));
    }

    [Fact]
    public async Task EarlyRecallSurvivesLaterPullAndReplay()
    {
        var repository = Repository;
        var source = await ConfigureAsync(repository);
        await repository.RecallAsync(source, "synthetic-group", "m1");
        var page = new MessagePage([Message(source)], "checkpoint", false);
        await repository.CommitPageAsync(source, "synthetic-group", page);
        await repository.CommitPageAsync(source, "synthetic-group", page);
        Assert.True(Assert.Single(await repository.ReadRecentAsync()).Message.IsRecalled);
    }

    [Fact]
    public async Task ExactlyOneWechatCollectorCanBeEnabledAndSavingTheSameSourcePausesIt()
    {
        var repository = Repository;
        var first = await ConfigureAsync(repository);
        var second = Definition(alias: "second-account");
        await repository.SaveSourceAsync(second);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => repository.SetSourceEnabledAsync(second.Id, true));
        await repository.SaveSourceAsync(first);
        Assert.True(Assert.Single(await repository.ReadConversationsAsync(first.Id)).Selected);
        Assert.False((await repository.ReadSourcesAsync()).Single(row => row.Id == first.Id).Enabled);
    }

    [Fact]
    public async Task VersionOneDatabaseUpgradesWithoutChangingPublishedMigrationOrMessageEvidence()
    {
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "messages.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var stream = typeof(SqliteMessageRepository).Assembly.GetManifestResourceStream("StudentReminder.Data.Migrations.001_initial.sql")!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();
            command.CommandText = await reader.ReadToEndAsync() + "\nPRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        // v1 SaveBatch does not require any v2 configuration; import a v1-shaped evidence row first.
        var repository = Repository;
        await repository.SaveBatchAsync([ImportTests.Example()]);
        await repository.InitializeAsync();
        await repository.InitializeAsync();
        Assert.Equal(ImportTests.Example().Text, Assert.Single(await repository.ReadRecentAsync()).Text);
        await repository.SaveSourceAsync(Definition());
        Assert.Single(await repository.ReadSourcesAsync());
    }

    [Fact]
    public async Task VersionTwoUpgradeRetiresHttpSourceAndPreservesItsEvidence()
    {
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "messages.db");
        var source = Definition("weflow") with { Port = 5031, Enabled = true, Revision = 7 };
        var repository = Repository;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        foreach (var migration in new[] { "001_initial.sql", "002_source_sync.sql" })
        {
            using var stream = typeof(SqliteMessageRepository).Assembly.GetManifestResourceStream($"StudentReminder.Data.Migrations.{migration}")!;
            using var reader = new StreamReader(stream);
            command.CommandText = await reader.ReadToEndAsync();
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = "PRAGMA user_version=2;";
        await command.ExecuteNonQueryAsync();
        await repository.SaveBatchAsync([Message(source, "existing")]);
        command.CommandText = """
            INSERT INTO source_configs(source_id,adapter,port,poll_seconds,lookback_days,use_push,revision)
            VALUES($id,'weflow',5031,60,7,1,7);
            UPDATE sources SET enabled=1 WHERE id=$id;
            UPDATE conversations SET selected=1 WHERE source_id=$id;
            """;
        command.Parameters.AddWithValue("$id", source.Id);
        await command.ExecuteNonQueryAsync();
        await repository.CommitPageAsync(source, "synthetic-group", new([Message(source, "checkpointed")], "old-checkpoint", false));

        await repository.InitializeAsync();
        await repository.InitializeAsync();
        Assert.Empty(await repository.ReadSourcesAsync());
        Assert.False(Assert.Single(await repository.ReadConversationsAsync(source.Id)).Selected);
        Assert.Null(await repository.ReadCursorAsync(source.Id, "synthetic-group"));
        Assert.Equal(2, await repository.CountAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveSourceAsync(source));
        await repository.SaveSourceAsync(Definition(alias: source.AccountAlias));
        Assert.False(Assert.Single(await repository.ReadSourcesAsync()).Enabled);
    }

    [Fact]
    public async Task QqLocalDiscoveryBackfillsOnlyLegacyNumericConversationNames()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = Definition("qqlocal") with { Port = 0 };
        await repository.SaveSourceAsync(source);
        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "synthetic-group", "group")]);
        await repository.SelectConversationsAsync(source.Id, ["synthetic-group"]);
        await repository.SetSourceEnabledAsync(source.Id, true);
        source = Assert.Single(await repository.ReadSourcesAsync());
        var numericName = Message(source) with { ConversationName = "synthetic-group" };
        var customName = Message(source, "m2") with { ConversationName = "用户保留的名称" };
        await repository.CommitPageAsync(source, "synthetic-group", new([numericName, customName], null, false));

        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成课程群", "group")]);

        var messages = await repository.ReadRecentAsync();
        Assert.Equal("合成课程群", Assert.Single(messages, row => row.Message.MessageId == "m1").Message.ConversationName);
        Assert.Equal("用户保留的名称", Assert.Single(messages, row => row.Message.MessageId == "m2").Message.ConversationName);
        Assert.Equal("合成课程群", Assert.Single(await repository.ReadConversationsAsync(source.Id)).Name);
    }

    [Fact]
    public async Task ReadAndMergeDropBogusZeroPlaceholderConversation()
    {
        var repository = Repository;
        await repository.InitializeAsync();
        var source = Definition("qqlocal") with { Port = 0 };
        await repository.SaveSourceAsync(source);

        // Simulate a stale bogus "0" conversation persisted by an older build.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "messages.db")};Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO conversations(id,source_id,external_id,name,type,selected) VALUES($id,$source,'0','0','group',0)";
            insert.Parameters.AddWithValue("$id", SourceKeys.Hash(source.Id, "0"));
            insert.Parameters.AddWithValue("$source", source.Id);
            insert.ExecuteNonQuery();
        }

        // Read path hides the placeholder immediately.
        Assert.DoesNotContain(await repository.ReadConversationsAsync(source.Id), c => c.Id == "0");

        // Re-reading sessions removes the stale placeholder for good.
        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成群", "group")]);
        Assert.Equal("合成群", Assert.Single(await repository.ReadConversationsAsync(source.Id)).Name);
    }

    internal static async Task<SourceDefinition> ConfigureAsync(SqliteMessageRepository repository)
    {
        await repository.InitializeAsync();
        var source = Definition();
        await repository.SaveSourceAsync(source);
        await repository.MergeConversationsAsync(source.Id, [new("synthetic-group", "合成群", "group")]);
        await repository.SelectConversationsAsync(source.Id, ["synthetic-group"]);
        await repository.SetSourceEnabledAsync(source.Id, true);
        return Assert.Single(await repository.ReadSourcesAsync());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
