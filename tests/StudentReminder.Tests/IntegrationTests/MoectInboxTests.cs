using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Ingestion;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class MoectInboxTests : IDisposable
{
    private const string GroupId = "synthetic-group@chatroom";
    private const string GroupName = "合成课程群";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "StudentReminderMoect", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SchemaThreeUpgradePreservesMessagesAndQueuesOnlyNewApprovedRows()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            foreach (var migration in new[] { "001_initial.sql", "002_source_sync.sql", "003_rust_source.sql" })
            {
                using var stream = typeof(SqliteMessageRepository).Assembly.GetManifestResourceStream($"StudentReminder.Data.Migrations.{migration}")!;
                using var reader = new StreamReader(stream);
                command.CommandText = await reader.ReadToEndAsync();
                await command.ExecuteNonQueryAsync();
            }
            command.CommandText = "PRAGMA user_version=3;";
            await command.ExecuteNonQueryAsync();
        }
        var repository = new SqliteMessageRepository(database);
        await repository.SaveBatchAsync([ImportTests.Example()]);
        await repository.InitializeAsync();
        await repository.InitializeAsync();
        Assert.Equal(1, await repository.CountAsync());
        Assert.Equal(30, (await repository.ReadMoectProfileAsync()).IntervalMinutes);

        var folder = Path.Combine(directory, "exports");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "first.json");
        var second = Path.Combine(folder, "second.json");
        await WriteExportAsync(first, GroupId, GroupId, 1, 2);
        var profile = new MoectWatchProfile { Folder = folder, Group = await MoectInbox.ReadGroupAsync(first), IntervalMinutes = 45 };
        await repository.SaveMoectProfileAsync(profile);
        Assert.Equal(profile, await repository.ReadMoectProfileAsync());
        Assert.Equal(2, (await ScanAsync(repository, profile)).Added);

        await WriteExportAsync(second, GroupId, GroupId, 2, 3);
        await WriteExportAsync(Path.Combine(folder, "different.json"), "different@chatroom", "different@chatroom", 4);
        await WriteExportAsync(Path.Combine(folder, "mixed.json"), GroupId, "different@chatroom", 5);
        var next = await ScanAsync(repository, profile);
        Assert.Equal(1, next.Added);
        Assert.Equal(1, next.Duplicates);
        Assert.Equal(2, next.Skipped);

        var pending = await repository.ReadPendingMoectAsync(profile, 10);
        Assert.Equal(3, pending.Count);
        await repository.MarkMoectAttemptAsync(pending[0].MessageId);
        var retry = await repository.ReadPendingMoectAsync(profile, 10);
        Assert.Equal(pending[0].MessageId, retry[^1].MessageId);
        await repository.SaveMoectResultAsync(pending[0].MessageId, new("assignment", "提交报告", "明天"));
        await repository.SaveMoectResultAsync(pending[1].MessageId, new("none", "", ""));
        Assert.Equal("提交报告", Assert.Single(await repository.ReadMoectResultsAsync(profile)).Result.Title);

        var replay = new SqliteMessageRepository(database);
        Assert.Equal(0, (await ScanAsync(replay, profile)).Added);
        Assert.Single(await replay.ReadPendingMoectAsync(profile, 10));
        Assert.Equal(4, await replay.CountAsync());
    }

    private static Task<MoectScanResult> ScanAsync(SqliteMessageRepository repository, MoectWatchProfile profile) =>
        MoectInbox.ScanAsync(profile, (digest, messages, token) => repository.ImportMoectExportAsync(profile, digest, messages, token));

    private static Task WriteExportAsync(string file, string group, string messageGroup, params int[] ids) => File.WriteAllTextAsync(file,
        JsonSerializer.Serialize(new
        {
            metadata = new { application = "Moect", name = GroupName, session = group }, count = ids.Length,
            messages = ids.Select(id => new { session = messageGroup, kind = "text", server_id = id, sender_wxid = "synthetic",
                sender = "合成账号", create_time = 1794380000L + id, text = $"合成文字 {id}" })
        }));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
