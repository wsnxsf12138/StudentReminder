using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.AI;
using StudentReminder.Core;
using StudentReminder.Data;
using StudentReminder.Notifications;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class P4ReminderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Initial = new(2026, 11, 12, 7, 0, 0, TimeSpan.Zero);
    private readonly string path = Path.Combine(Path.GetTempPath(), $"student-reminder-p4-{Guid.NewGuid():N}.db");
    private readonly Clock clock = new(Initial);
    private SqliteMessageRepository store = null!;
    private readonly Notification notification = new();
    private ReminderDispatcher Dispatcher => new(store, notification);
    private static CancellationToken Ct => CancellationToken.None;

    public async Task InitializeAsync()
    {
        store = new(path, clock);
        await store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfirmedEventsUseUtcKeysAndTitleEditsDoNotRepeatNotifications()
    {
        var start = Initial.AddHours(2).AddSeconds(20).ToOffset(TimeSpan.FromHours(8));
        var draft = Draft(start, start.AddHours(5));
        var id = await ConfirmAsync(draft);
        Assert.False(await Dispatcher.DispatchAsync(Initial, recovery: false, startup: true, Ct));
        clock.Now = Initial.AddSeconds(20);
        var sweeps = await Task.WhenAll(
            Task.Run(() => Dispatcher.DispatchAsync(clock.Now, recovery: false, startup: false, Ct)),
            Task.Run(() => Dispatcher.DispatchAsync(clock.Now, recovery: false, startup: false, Ct)));
        Assert.Single(sweeps, result => result);
        var row = Assert.Single(Assert.Single(notification.Batches).Due);
        Assert.Equal(("start:120", id, clock.Now), (row.Rule, row.EventId, row.FireAt));
        Assert.Equal(TimeSpan.Zero, row.FireAt.Offset);

        await store.UpdateEventAsync(id, 1, "edit", draft with { Title = "合成事项改名", StartsAt = start.ToUniversalTime() });
        Assert.False(await Dispatcher.DispatchAsync(clock.Now, recovery: false, startup: false, Ct));
        await store.UpdateEventAsync(id, 2, "complete");
        Assert.False(await Dispatcher.DispatchAsync(start.AddHours(5), recovery: true, startup: false, Ct));
    }

    [Fact]
    public async Task RuleChangesAndReschedulingReplacePendingPlansAndClearOnDelete()
    {
        var draft = Draft(Initial.AddHours(3), Initial.AddHours(4));
        var id = await ConfirmAsync(draft);
        await store.SaveReminderPreferencesAsync(new() { BeforeStartMinutes = [0], BeforeDeadlineMinutes = [] });
        Assert.False(await Dispatcher.DispatchAsync(Initial.AddHours(2), recovery: false, startup: false, Ct));
        clock.Now = Initial.AddHours(2);
        await store.UpdateEventAsync(id, 1, "edit", draft with { StartsAt = Initial.AddHours(4) });
        Assert.False(await Dispatcher.DispatchAsync(Initial.AddHours(3), recovery: false, startup: false, Ct));
        await store.UpdateEventAsync(id, 2, "delete");
        Assert.False(await Dispatcher.DispatchAsync(Initial.AddHours(5), recovery: true, startup: false, Ct));
        Assert.Empty(await store.ReadMissedRemindersAsync());
    }

    [Fact]
    public async Task RecoveryGroupsOverdueAndInterruptedClaimsAndKeepsOriginalTimes()
    {
        var start = Initial.AddHours(3);
        await ConfirmAsync(Draft(start, start.AddHours(1)));
        var first = Initial.AddHours(1);
        var interrupted = await store.ClaimRemindersAsync(first, Initial, false, Ct);
        Assert.Single(interrupted.Due);
        var second = Initial.AddHours(2);
        Assert.True(await Dispatcher.DispatchAsync(second.AddMinutes(1), recovery: true, startup: true, Ct));
        Assert.Equal(2, Assert.Single(notification.Batches).NewlyMissed);
        Assert.Empty(notification.Batches[0].Due);
        Assert.False(await Dispatcher.DispatchAsync(second.AddMinutes(2), recovery: true, startup: true, Ct));
        var rows = await store.ReadMissedRemindersAsync();
        Assert.Equal(new[] { ("missed", second), ("uncertain", first) }, rows.Select(row => (row.Status, row.FireAt)));
    }

    [Fact]
    public async Task FailedNotificationLeavesLocalRecordAndIsNotSubmittedAgain()
    {
        await store.SaveReminderPreferencesAsync(new() { BeforeStartMinutes = [0], BeforeDeadlineMinutes = [] });
        await ConfirmAsync(Draft(Initial.AddMinutes(5), null));
        notification.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Dispatcher.DispatchAsync(Initial.AddMinutes(5), false, false, Ct));
        Assert.Equal("failed", Assert.Single(await store.ReadMissedRemindersAsync()).Status);
        Assert.False(await Dispatcher.DispatchAsync(Initial.AddMinutes(5), false, true, Ct));
    }

    [Fact]
    public async Task CompletionInvalidatesAClaimThatHasNotBeenSubmitted()
    {
        var start = Initial.AddHours(3);
        var id = await ConfirmAsync(Draft(start, null));
        var fire = start.AddHours(-2);
        Assert.Single((await store.ClaimRemindersAsync(fire, Initial, false, Ct)).Due);
        await store.UpdateEventAsync(id, 1, "complete");
        Assert.Empty(await store.ReadClaimedRemindersAsync(Ct));
        Assert.False(await Dispatcher.DispatchAsync(fire.AddMinutes(1), recovery: true, startup: true, Ct));
    }

    [Fact]
    public async Task SchemaSixUpgradesAndOnlyBackfillsFutureTriggers()
    {
        var legacy = path + ".legacy";
        var reused = new SourceDefinition { Adapter = "qqlocal", AccountAlias = "合成旧来源" };
        try
        {
            using (var db = new SqliteConnection($"Data Source={legacy};Pooling=False;Foreign Keys=True"))
            {
                await db.OpenAsync();
                var assembly = typeof(SqliteMessageRepository).Assembly;
                foreach (var resource in assembly.GetManifestResourceNames()
                    .Where(name => name.Contains(".Migrations.00", StringComparison.Ordinal) &&
                        !name.Contains(".007_", StringComparison.Ordinal) && !name.Contains(".008_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                    using var migration = db.CreateCommand();
                    migration.CommandText = await reader.ReadToEndAsync();
                    await migration.ExecuteNonQueryAsync();
                }
                using var seed = db.CreateCommand();
                seed.CommandText = """
                    PRAGMA user_version=6;
                    INSERT INTO sources(id,type,account_alias,enabled,last_ok_at) VALUES($retired,'qq','合成旧来源',1,$now);
                    INSERT INTO source_configs(source_id,adapter,port,poll_seconds,lookback_days) VALUES($retired,'qqflow',5032,60,7);
                    INSERT INTO conversations(id,source_id,external_id,name,type,selected)
                    VALUES('old-chat',$retired,'old-chat','旧会话','group',1);
                    INSERT INTO sync_cursors(source_id,conversation_id,cursor_json,updated_at)
                    VALUES($retired,'old-chat','{}',$now);
                    INSERT INTO events(id,title,status,updated_at,starts_at) VALUES('old','合成历史事项','active',$now,$starts);
                    INSERT INTO event_versions(event_id,version,payload_json,status,changed_at) VALUES('old',1,$draft,'active',$now);
                    """;
                var start = Initial.AddHours(3);
                seed.Parameters.AddWithValue("$retired", reused.Id);
                seed.Parameters.AddWithValue("$now", Iso(Initial.AddDays(-2)));
                seed.Parameters.AddWithValue("$starts", Iso(start));
                seed.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(Draft(start, null), ContractJson.Options));
                await seed.ExecuteNonQueryAsync();
            }
            var upgraded = new SqliteMessageRepository(legacy, clock);
            await upgraded.InitializeAsync();
            await upgraded.InitializeAsync();
            Assert.Empty(await upgraded.ReadSourcesAsync());
            Assert.False(Assert.Single(await upgraded.ReadConversationsAsync(reused.Id)).Selected);
            Assert.Null(await upgraded.ReadCursorAsync(reused.Id, "old-chat"));
            await upgraded.SaveSourceAsync(reused);
            Assert.Null(Assert.Single(await upgraded.ReadSourcesAsync()).LastOkAt);
            Assert.Equal("old", Assert.Single(await upgraded.ReadEventsAsync()).Id);
            Assert.Empty((await upgraded.ClaimRemindersAsync(Initial, Initial, true, Ct)).Due);
            var next = Initial.AddHours(1);
            Assert.Equal("start:120", Assert.Single((await upgraded.ClaimRemindersAsync(next, Initial, false, Ct)).Due).Rule);
            Assert.Empty(await upgraded.ReadMissedRemindersAsync());
        }
        finally { File.Delete(legacy); }
    }

    [Fact]
    public async Task LocalDigestRunsOncePerDateAndCanRefreshAfterEventsChange()
    {
        var cutoff = Initial.AddMinutes(30);
        var id = await ConfirmAsync(Draft(Initial.AddHours(1), Initial.AddHours(2)));
        var pending = await SeedCandidateAsync(Draft(null, null));
        Assert.Null(await store.GenerateDailyDigestAsync(cutoff.AddSeconds(-1), TimeZoneInfo.Utc, scheduled: true));
        Assert.NotNull(await store.GenerateDailyDigestAsync(Initial, TimeZoneInfo.Utc, scheduled: false));
        var results = await Task.WhenAll(
            store.GenerateDailyDigestAsync(cutoff, TimeZoneInfo.Utc, scheduled: true),
            store.GenerateDailyDigestAsync(cutoff, TimeZoneInfo.Utc, scheduled: true));
        var digest = Assert.Single(results.OfType<DailyDigest>());
        Assert.Equal(id, Assert.Single(digest.Sections[0].Entries).Id);
        Assert.Equal(pending, Assert.Single(digest.Sections[3].Entries).Id);
        Assert.Equal(id, Assert.Single(digest.Sections[4].Entries).Id);
        Assert.Single(await store.ReadReviewEvidenceAsync(id, true));
        clock.Now = cutoff.AddHours(1);
        await store.UpdateEventAsync(id, 1, "complete");
        var refreshed = await store.GenerateDailyDigestAsync(clock.Now, TimeZoneInfo.Utc, scheduled: false);
        Assert.NotNull(refreshed);
        Assert.Empty(refreshed.Sections[2].Entries);
        Assert.Null(await store.GenerateDailyDigestAsync(clock.Now, TimeZoneInfo.Utc, scheduled: true));
        Assert.NotNull(await store.GenerateDailyDigestAsync(cutoff.AddDays(1), TimeZoneInfo.Utc, scheduled: true));
    }

    [Fact]
    public async Task ManualDigestRefreshWorksAfterTheClockMovesBackwards()
    {
        var scheduledAt = Initial.AddHours(2);
        await store.GenerateDailyDigestAsync(scheduledAt, TimeZoneInfo.Utc, scheduled: true);
        await ConfirmAsync(Draft(Initial.AddHours(3), null));
        var refreshed = await store.GenerateDailyDigestAsync(Initial, TimeZoneInfo.Utc, scheduled: false);
        Assert.NotNull(refreshed);
        Assert.Equal(Initial, refreshed.GeneratedAt);
        Assert.Single(refreshed.Sections[0].Entries);
        Assert.Null(await store.GenerateDailyDigestAsync(scheduledAt, TimeZoneInfo.Utc, scheduled: true));
    }

    [Fact]
    public void DigestDatesUseLocalMidnightAndHandleDstGapAndFold()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), DigestCalendar.At(new(2026, 3, 8), new(2, 30), zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), DigestCalendar.At(new(2026, 11, 1), new(1, 30), zone));
        var now = new DateTimeOffset(2026, 11, 1, 3, 59, 0, TimeSpan.Zero);
        var today = new ReviewedEvent("today", Draft(now.AddSeconds(10), now.AddHours(72)), "active", 1, now);
        var tomorrow = today with { Id = "tomorrow", Details = Draft(now.AddMinutes(1), now.AddHours(72).AddSeconds(-1)) };
        var digest = DigestCalendar.Build(now, zone, [new(today, now), new(tomorrow, now)], [], []);
        Assert.Equal(new DateOnly(2026, 10, 31), digest.Date);
        Assert.Equal("today", Assert.Single(digest.Sections[0].Entries).Id);
        Assert.Equal("tomorrow", Assert.Single(digest.Sections[1].Entries).Id);
        Assert.Equal(new DateOnly(2026, 11, 1), DigestCalendar.Day(now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task PolishUsesThePreviewPayloadAndSharedRequestBudget()
    {
        var profile = new AiProfile("deepseek", "synthetic-model");
        var reservation = await store.ReserveDigestRequestAsync(profile, Initial, TimeZoneInfo.Utc);
        Assert.NotNull(reservation.RequestId);
        Assert.Null((await store.ReserveDigestRequestAsync(profile, Initial, TimeZoneInfo.Utc)).RequestId);
        var input = new AiDigestItem(1, AnalysisText.Prepare("合成事项 13800000000"), "exam", Initial, null);
        var preview = ChatCompletionAiProvider.SerializeDigestInput([input]);
        using var provider = new ChatCompletionAiProvider(new Handler(preview,
            """{"choices":[{"id":1,"style":"time-first"}]}"""));
        var polish = await provider.GenerateDigestAsync(profile, "synthetic-key", [input]);
        Assert.Equal(new AiDigestChoice(1, "time-first"), Assert.Single(polish.Choices));
        await store.FinishDigestRequestAsync(reservation.RequestId.Value, polish.Usage, null);
        var stats = await store.ReadAiStatisticsAsync(Initial, TimeZoneInfo.Utc);
        Assert.Equal((1, 12L, 5L), (stats.Requests, stats.PromptTokens, stats.CompletionTokens));
        using var invalid = new ChatCompletionAiProvider(new Handler(preview,
            """{"choices":[{"id":9,"style":"time-first"}]}"""));
        Assert.Equal("format", (await Assert.ThrowsAsync<AiFailureException>(() =>
            invalid.GenerateDigestAsync(profile, "synthetic-key", [input]))).Code);
    }

    private async Task<string> ConfirmAsync(EventCandidate draft) =>
        await store.ConfirmCandidateAsync(await SeedCandidateAsync(draft), draft, null, null);

    private async Task<string> SeedCandidateAsync(EventCandidate draft)
    {
        var id = Guid.NewGuid().ToString("N");
        await store.SaveBatchAsync([ImportTests.Example() with { MessageId = id }]);
        var evidence = (await store.ReadRecentAsync()).Single(item => item.Message.MessageId == id).Id;
        using var db = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=True");
        await db.OpenAsync();
        using var seed = db.CreateCommand();
        seed.CommandText = """
            INSERT INTO event_candidates(id,payload_json,confidence,created_at) VALUES($id,$draft,'explicit',$now);
            INSERT INTO candidate_evidence(candidate_id,message_id) VALUES($id,$evidence);
            """;
        seed.Parameters.AddWithValue("$id", id);
        seed.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(draft, ContractJson.Options));
        seed.Parameters.AddWithValue("$now", Iso(clock.Now));
        seed.Parameters.AddWithValue("$evidence", evidence);
        await seed.ExecuteNonQueryAsync();
        return id;
    }

    private static EventCandidate Draft(DateTimeOffset? start, DateTimeOffset? due) => new()
    {
        Title = "合成考试", Category = "exam", Action = "参加", StartsAt = start, DeadlineAt = due, Evidence = []
    };

    private static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed class Clock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Notification : IReminderNotification
    {
        public List<ReminderBatch> Batches { get; } = [];
        public bool Fail { get; set; }
        public Task SubmitAsync(ReminderBatch batch, CancellationToken ct)
        {
            if (Fail) throw new IOException("Synthetic notification failure.");
            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(string preview, string choices) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var input = content.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.Equal(preview, input);
            Assert.DoesNotContain("13800000000", input);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = choices } } },
                usage = new { prompt_tokens = 12, completion_tokens = 5 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
