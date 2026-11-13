using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class P3MigrationTests
{
    [Fact]
    public async Task VersionFourUpgradesAndPersistsPortlessWxLocalWithoutLosingImportedHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"p3-{Guid.NewGuid():N}.db");
        var oldSource = new SourceDefinition { Adapter = "weflow-cli", AccountAlias = "synthetic-old", Port = 18108 };
        var wxLocal = new SourceDefinition { Adapter = "wxlocal", AccountAlias = "synthetic-live", Port = 0, UsePush = true };
        var group = new MoectGroup("synthetic-old@chatroom", "旧版合成群");
        var moect = new MoectWatchProfile
        {
            Group = group,
            AccountAlias = oldSource.AccountAlias
        };
        var conversation = SourceKeys.Hash(oldSource.Id, group.Id);
        try
        {
            await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();
                foreach (var migration in new[] { "001_initial.sql", "002_source_sync.sql", "003_rust_source.sql", "004_moect_analysis.sql" })
                {
                    using var stream = typeof(SqliteMessageRepository).Assembly.GetManifestResourceStream(
                        $"StudentReminder.Data.Migrations.{migration}")!;
                    using var reader = new StreamReader(stream);
                    using var command = db.CreateCommand();
                    command.CommandText = await reader.ReadToEndAsync();
                    await command.ExecuteNonQueryAsync();
                }
                using var seed = db.CreateCommand();
                seed.CommandText = """
                    PRAGMA user_version=4;
                    INSERT INTO sources(id,type,account_alias) VALUES($id,'wechat','synthetic-old');
                    INSERT INTO source_configs(source_id,adapter,port,poll_seconds,lookback_days,provider_account)
                    VALUES($id,'weflow-cli',18108,60,7,$account);
                    INSERT INTO conversations(id,source_id,external_id,name,type)
                    VALUES($conversation,$id,$group,'旧版合成群','group');
                    INSERT INTO messages(id,source_id,conversation_id,external_id,dedup_key,sent_at,received_at,text,content_hash,payload_json,status)
                    VALUES('synthetic-v4-message',$id,$conversation,'synthetic-upstream','synthetic-v4-dedup',
                           '2026-11-11T01:00:00.0000000Z','2026-11-11T01:00:00.0000000Z','明天交作业','synthetic-v4-hash',$message,'active');
                    INSERT INTO moect_analysis(message_id,result_json) VALUES('synthetic-v4-message',$result);
                    INSERT INTO app_settings(key,value_json) VALUES('moect-watch',$profile);
                    """;
                seed.Parameters.AddWithValue("$id", oldSource.Id);
                seed.Parameters.AddWithValue("$account", SourceKeys.Hash("synthetic-bound"));
                seed.Parameters.AddWithValue("$conversation", conversation);
                seed.Parameters.AddWithValue("$group", group.Id);
                seed.Parameters.AddWithValue("$message", JsonSerializer.Serialize(ImportTests.Example() with
                {
                    Source = "wechat", Adapter = "moect-json", AccountId = moect.AccountAlias, ConversationId = group.Id,
                    MessageId = "synthetic-upstream", Text = "明天交作业"
                }, ContractJson.Options));
                seed.Parameters.AddWithValue("$result", JsonSerializer.Serialize(new MoectAnalysisResult("assignment", "交作业", "明天")));
                seed.Parameters.AddWithValue("$profile", JsonSerializer.Serialize(moect));
                await seed.ExecuteNonQueryAsync();
            }
            var repository = new SqliteMessageRepository(path);
            await repository.InitializeAsync();
            await repository.InitializeAsync();
            await repository.SaveSourceAsync(wxLocal);
            var sources = await repository.ReadSourcesAsync();
            Assert.DoesNotContain(sources, row => row.Id == oldSource.Id);
            Assert.Contains(sources, row => row.Id == wxLocal.Id && row.Port == 0 && row.UsePush && !row.Enabled);
            Assert.Equal("交作业", Assert.Single(await repository.ReadMoectResultsAsync(moect)).Result.Title);
            var history = Assert.Single(await repository.ReadAiScopesAsync());
            var page = await repository.ReadAnalysisHistoryAsync(history, DateTimeOffset.Parse("2026-11-10T00:00:00Z"),
                DateTimeOffset.Parse("2026-11-12T00:00:00Z"));
            Assert.Equal("synthetic-upstream", Assert.Single(page.Items).Evidence.MessageId);
        }
        finally { File.Delete(path); }
    }
}
