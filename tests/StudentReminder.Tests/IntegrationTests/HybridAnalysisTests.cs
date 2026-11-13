using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.AI;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class HybridAnalysisTests : IAsyncLifetime
{
    private static readonly AiProfile Profile = new("deepseek", "synthetic-model");
    private static readonly DateTimeOffset Prior = DateTimeOffset.Parse("2026-11-11T23:58:00+08:00");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-cn", TimeSpan.FromHours(8), "test", "test");
    private const string JointReply = """
        {"candidates":[{"title":"实验报告","category":"assignment","action":"交报告","location":null,
        "start_refs":[],"deadline_refs":[{"evidence_id":"E2","quote":"明天"},{"evidence_id":"E1","quote":"18点前"}],
        "evidence_ids":["E1","E2"],"certainty":"explicit","ambiguities":[],"change_kind":"new"}]}
        """;
    private readonly string path = Path.Combine(Path.GetTempPath(), $"hybrid-ai-{Guid.NewGuid():N}.db");
    private SqliteMessageRepository store = null!;

    private const string NoticeReply = """
        {"jobs":[
        {"job_id":"J2","candidates":[{"title":"提交合成登记表","category":"notice",
        "action":"打印并交给合成班委，办理路径为合成平台／登记表","location":null,"start_refs":[],
        "deadline_refs":[{"evidence_id":"E1","quote":"明天中午前"}],"evidence_ids":["E1"],"certainty":"explicit",
        "ambiguities":[],"change_kind":"new"}]},
        {"job_id":"J1","candidates":[{"title":"填写合成问卷","category":"notice",
        "action":"填写合成问卷","location":null,"start_refs":[],"deadline_refs":[],"evidence_ids":["E1"],
        "certainty":"explicit","ambiguities":[],"change_kind":"new"}]}]}
        """;

    public async Task InitializeAsync()
    {
        store = new(path);
        await store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CombinedAnalysisUsesEachEvidenceTimestampAndPersistsBothReferences()
    {
        var preview = await SeedManualJointAsync();
        using var provider = new ChatCompletionAiProvider(new Handler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var outbound = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Equal(ChatCompletionAiProvider.PreviewInput(preview.Input), outbound);
            Assert.DoesNotContain("合成老师", outbound);
            Assert.DoesNotContain("synthetic-account", outbound);
            Assert.DoesNotContain("synthetic-sender", outbound);
            Assert.DoesNotContain("13800000000", outbound);
            using var input = JsonDocument.Parse(outbound);
            var messages = input.RootElement.GetProperty("messages");
            Assert.Equal(messages[0].GetProperty("speaker").GetString(), messages[1].GetProperty("speaker").GetString());
            Assert.NotEqual(messages[0].GetProperty("sent_at").GetDateTimeOffset().Date, messages[1].GetProperty("sent_at").GetDateTimeOffset().Date);
            return Reply(JointReply);
        }));
        await new AiAnalysisRunner(store, provider).RunCombinedAsync(Profile, "synthetic-key", preview, null, CancellationToken.None);
        var candidate = Assert.Single(await store.ReadCandidatesAsync(false));
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T10:00:00Z"), candidate.Draft.DeadlineAt);
        Assert.Equal(2, candidate.Draft.Evidence.Count);
        Assert.Equal(2, (await store.ReadReviewEvidenceAsync(candidate.Id, false)).Count);
        var confirmed = await store.ConfirmCandidateAsync(candidate.Id, candidate.Draft, null, null);
        Assert.Equal(2, (await store.ReadReviewEvidenceAsync(confirmed, true)).Count);
    }

    [Fact]
    public async Task RevokedContextIsRejectedBeforeCloudReservation()
    {
        var preview = await SeedManualJointAsync();
        await store.SaveBatchAsync([preview.Related[0].Message.Message with { IsRecalled = true, OriginEvent = "message.revoke" }]);
        using var provider = new ChatCompletionAiProvider(new Handler((_, _) =>
            throw new InvalidOperationException("Revoked context must never reach the cloud.")));
        await new AiAnalysisRunner(store, provider).RunCombinedAsync(Profile, "synthetic-key", preview, null, CancellationToken.None);
        Assert.Equal(0, (await store.ReadAiStatisticsAsync(DateTimeOffset.UtcNow, Zone)).Requests);
        Assert.Empty(await store.ReadCandidatesAsync(false));
    }

    [Fact]
    public async Task InventedClockRemainsUnresolvedEvenWhenTheEvidenceIdsAreValid()
    {
        var preview = await SeedManualJointAsync();
        using var provider = new ChatCompletionAiProvider(new Handler((_, _) =>
            Task.FromResult(Reply(JointReply.Replace("18点前", "20点前", StringComparison.Ordinal)))));
        var result = await provider.ExtractCandidatesAsync(Profile, "synthetic-key", preview.Input, preview.Anchor.Work.Evidence);
        var draft = Assert.Single(result.Events).Candidate;
        Assert.Null(draft.DeadlineAt);
        Assert.Contains(draft.Ambiguities, value => value.Contains("证据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DateWithoutAClockIsStoredAndShownInTodayWithoutSchedulingAReminder()
    {
        await store.SaveBatchAsync([Manual("date", Prior, "明天交报告")]);
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var row = Assert.Single((await store.ReadAnalysisHistoryAsync(scope, Prior.AddDays(-1), Prior.AddDays(1))).Items);
        using var provider = new ChatCompletionAiProvider(new Handler((_, _) => Task.FromResult(Reply("""
            {"candidates":[{"title":"实验报告","category":"assignment","action":"交报告","location":null,
            "start_text":null,"deadline_text":"明天","evidence_id":"E1","certainty":"explicit","ambiguities":[],"change_kind":"new"}]}
            """))));
        await new AiAnalysisRunner(store, provider).RunManualAsync(Profile, "synthetic-key", [AiPreview.Create(row)], Zone,
            null, CancellationToken.None);
        var candidate = Assert.Single(await store.ReadCandidatesAsync(false));
        Assert.Null(candidate.Draft.DeadlineAt);
        Assert.Equal(new DateOnly(2026, 11, 12), candidate.Draft.DeadlineOn);
        var id = await store.ConfirmCandidateAsync(candidate.Id, candidate.Draft, null, null);
        var confirmed = Assert.Single(await store.ReadEventsAsync());
        Assert.Equal(candidate.Draft.DeadlineOn, confirmed.Details.DeadlineOn);
        Assert.Empty(ReminderRules.Plan(confirmed.Details, confirmed.Status, new()));
        var digest = await store.GenerateDailyDigestAsync(Prior.AddDays(1), Zone, scheduled: false);
        Assert.Equal(id, Assert.Single(digest!.Sections[0].Entries).Id);
    }

    [Fact]
    public async Task AutomaticHybridUsesRulesToProtectADeadlineWhenLocalModelSkipsIt()
    {
        const string group = "synthetic-course";
        var source = new SourceDefinition { Adapter = "wxlocal", AccountAlias = "synthetic-account" };
        await store.SaveSourceAsync(source);
        await store.MergeConversationsAsync(source.Id, [new(group, "合成课程", "group")]);
        await store.SelectConversationsAsync(source.Id, [group]);
        await store.SetSourceEnabledAsync(source.Id, true);
        source = Assert.Single(await store.ReadSourcesAsync());
        var since = DateTimeOffset.UtcNow.AddMinutes(-6);
        var messages = new[]
        {
            Manual("before", since.AddMinutes(-1), "未授权历史"),
            Manual("announcement", since.AddMinutes(1), "明天交实验报告"),
            Manual("noise", since.AddMinutes(2), "收到谢谢"),
            Manual("deadline", since.AddMinutes(3), "18点前")
        }.Select(item => item with { Source = "wechat", Adapter = source.Adapter, AccountId = source.AccountAlias,
            ConversationId = group }).ToArray();
        await store.CommitPageAsync(source, group, new(messages, "synthetic-cursor", false));
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var original = (await store.ReadRecentAsync()).Single(row => row.Message.MessageId == "announcement");
        await store.MarkFilteredAsync(original.Id, CancellationToken.None);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ChatCompletionAiProvider(new Handler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var outbound = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Contains("18点前", outbound);
            Assert.Contains("明天交实验报告", outbound);
            Assert.DoesNotContain("未授权历史", outbound);
            Assert.DoesNotContain("收到谢谢", outbound);
            reached.SetResult();
            return Reply(JointReply);
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new AiAnalysisRunner(store, provider, localTriage: new SkipAll());
        var run = runner.RunAutomaticAsync(Profile, "synthetic-key", [scope.Id], since, Zone, null, timeout.Token, "synthetic");
        try
        {
            await reached.Task.WaitAsync(timeout.Token);
            while ((await store.ReadCandidatesAsync(false, timeout.Token)).Count == 0)
                await Task.Delay(20, timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        var stats = await store.ReadAiStatisticsAsync(DateTimeOffset.UtcNow, Zone);
        Assert.Equal(1, stats.Requests);
        Assert.Equal(2, stats.Filtered);
    }

    [Fact]
    public async Task AutomaticBatchUsesOneRequestForReadyNoticesAndKeepsEvidencePerMessage()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-6);
        var scopes = await SeedAutomaticAsync(since,
            ("group-a", "survey", "请填写合成问卷 https://example.invalid/synthetic-form"),
            ("group-b", "separate", "合成公告：新开放自习室"),
            ("group-a", "form", "明天中午前提交合成登记表，打印后交给合成班委，路径为合成平台／登记表"));
        var reached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ChatCompletionAiProvider(new Handler(async (request, ct) =>
        {
            reached.SetResult(await ReadOutboundAsync(request, ct));
            return Reply(NoticeReply);
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new AiAnalysisRunner(store, provider, localTriage: new SkipAll()).RunAutomaticAsync(Profile, "synthetic-key",
            scopes, since, Zone, null, timeout.Token, "synthetic", new(2, 120));
        try
        {
            var outbound = await reached.Task.WaitAsync(timeout.Token);
            Assert.Contains("合成问卷", outbound);
            Assert.Contains("合成登记表", outbound);
            Assert.DoesNotContain("新开放自习室", outbound);
            Assert.DoesNotContain("synthetic-sender", outbound);
            Assert.DoesNotContain("合成老师", outbound);
            Assert.DoesNotContain("example.invalid", outbound);
            using var json = JsonDocument.Parse(outbound);
            Assert.Equal(2, json.RootElement.GetProperty("jobs").GetArrayLength());
            var candidates = await WaitForCandidatesAsync(2, timeout.Token);
            var form = Assert.Single(candidates, row => row.Draft.Title == "提交合成登记表").Draft;
            Assert.Equal("high", form.Priority);
            Assert.Contains("合成平台／登记表", form.Action);
            Assert.Equal("明天中午前", form.DeadlineText);
            Assert.NotNull(form.DeadlineOn);
            Assert.Null(form.DeadlineAt);
            var survey = Assert.Single(candidates, row => row.Draft.Title == "填写合成问卷").Draft;
            Assert.Equal("notice", survey.Category);
            Assert.Null(survey.DeadlineOn);
            Assert.NotEqual(Assert.Single(form.Evidence).MessageId, Assert.Single(survey.Evidence).MessageId);
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        Assert.Equal(1, (await store.ReadAiStatisticsAsync(DateTimeOffset.UtcNow, Zone)).Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteBatchWaitsForItsDeadlineAndPausePreventsSending(bool pause)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddMinutes(-6);
        var scopes = await SeedAutomaticAsync(since, ("group", "notice", "合成公告：图书室调整开放安排"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new BatchClock(now);
        var calls = 0;
        var queued = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(status =>
        {
            if (status.StartsWith("本批云端提取完成", StringComparison.Ordinal)) completed.SetResult();
            if (!status.StartsWith("本地排队", StringComparison.Ordinal)) return;
            Assert.Equal(0, calls);
            queued++;
            if (pause) timeout.Cancel();
            clock.Now = now.AddMinutes(2);
        });
        using var provider = new ChatCompletionAiProvider(new Handler((_, _) =>
        {
            Assert.Equal(1, queued);
            Assert.Equal(now.AddMinutes(2), clock.GetUtcNow());
            calls++;
            return Task.FromResult(Reply("""{"jobs":[{"job_id":"J1","candidates":[]}]}"""));
        }));
        var run = new AiAnalysisRunner(store, provider, clock, new SkipAll()).RunAutomaticAsync(Profile, "synthetic-key",
            scopes, since, Zone, progress, timeout.Token, "synthetic", new(4, 120));
        if (!pause)
        {
            await completed.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, queued);
        Assert.Equal(pause ? 0 : 1, calls);
    }

    [Fact]
    public async Task RevokedConversationCannotSendItsQueuedBatchOrOldPreview()
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddMinutes(-6);
        var scopes = await SeedAutomaticAsync(since,
            ("revoked", "old", "合成公告：已撤销会话的作业通知"),
            ("allowed", "valid", "合成公告：仍获授权的作业通知"));
        var source = Assert.Single(await store.ReadSourcesAsync());
        var scope = (await store.ReadAiScopesAsync()).Single(item => item.Name == "合成会话 revoked");
        var history = (await store.ReadAnalysisHistoryAsync(scope, since.AddDays(-1), now.AddDays(1))).Items;
        var oldPreview = AiPreview.Create(Assert.Single(history));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new BatchClock(now);
        var revoked = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(status =>
        {
            if (!revoked && status.StartsWith("本地排队 2 条", StringComparison.Ordinal))
            {
                store.SelectConversationsAsync(source.Id, ["allowed"], timeout.Token).GetAwaiter().GetResult();
                revoked = true;
                clock.Now = now.AddMinutes(2);
            }
            if (status.StartsWith("本批云端提取完成", StringComparison.Ordinal)) completed.TrySetResult();
        });
        var outbound = new List<string>();
        using var provider = new ChatCompletionAiProvider(new Handler(async (request, ct) =>
        {
            outbound.Add(await ReadOutboundAsync(request, ct));
            return Reply("""{"jobs":[{"job_id":"J1","candidates":[]}]}""");
        }));
        var runner = new AiAnalysisRunner(store, provider, clock, new SkipAll());
        var run = runner.RunAutomaticAsync(Profile, "synthetic-key", scopes, since, Zone, progress, timeout.Token,
            "synthetic", new(4, 120));
        try { await completed.Task.WaitAsync(timeout.Token); }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        await runner.RunManualAsync(Profile, "synthetic-key", [oldPreview], Zone, null, CancellationToken.None);
        Assert.Contains("仍获授权的作业通知", Assert.Single(outbound));
        Assert.DoesNotContain("已撤销会话的作业通知", outbound[0]);
        Assert.Equal(1, (await store.ReadAiStatisticsAsync(clock.GetUtcNow(), Zone)).Requests);
    }

    [Fact]
    public async Task LectureRegistrationAndLectureKeepSeparateTimesWithDottedDates()
    {
        var sent = DateTimeOffset.Parse("2026-11-03T09:00:00+08:00");
        await store.SaveBatchAsync([Manual("lecture", sent,
            "合成讲座预约开放11.4（周三）09:00，预约截止11.4（周三）中午前。讲座11.6（周五）14:30开始。")]);
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var row = Assert.Single((await store.ReadAnalysisHistoryAsync(scope, sent.AddDays(-1), sent.AddDays(1))).Items);
        using var provider = new ChatCompletionAiProvider(new Handler((_, _) => Task.FromResult(Reply("""
            {"candidates":[
            {"title":"预约合成讲座","category":"registration","action":"预约","location":null,
            "start_text":"11.4（周三）09:00","deadline_text":"11.4（周三）中午前","evidence_id":"E1","certainty":"explicit",
            "ambiguities":[],"change_kind":"new"},
            {"title":"参加合成讲座","category":"activity","action":"参加","location":null,
            "start_text":"11.6（周五）14:30","deadline_text":null,"evidence_id":"E1","certainty":"explicit",
            "ambiguities":[],"change_kind":"new"}]}
            """))));
        await new AiAnalysisRunner(store, provider).RunManualAsync(Profile, "synthetic-key", [AiPreview.Create(row)], Zone,
            null, CancellationToken.None);
        var candidates = await store.ReadCandidatesAsync(false);
        var registration = Assert.Single(candidates, item => item.Draft.Category == "registration").Draft;
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T01:00:00Z"), registration.StartsAt);
        Assert.Equal(new DateOnly(2026, 11, 4), registration.DeadlineOn);
        Assert.Equal("11.4（周三）中午前", registration.DeadlineText);
        Assert.Null(registration.DeadlineAt);
        var lecture = Assert.Single(candidates, item => item.Draft.Category == "activity").Draft;
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T06:30:00Z"), lecture.StartsAt);
        Assert.Null(EventTimeParser.ParseDate("11.4（周五）中午前", sent, Zone));
    }

    [Fact]
    public async Task SchemaSevenUpgradesWithoutChangingExistingMessages()
    {
        var oldPath = path + ".v7";
        try
        {
            using (var db = new SqliteConnection($"Data Source={oldPath};Pooling=False"))
            {
                await db.OpenAsync();
                var assembly = typeof(SqliteMessageRepository).Assembly;
                foreach (var resource in assembly.GetManifestResourceNames()
                    .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && !name.Contains(".008_", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal))
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                    using var migration = db.CreateCommand();
                    migration.CommandText = await reader.ReadToEndAsync();
                    await migration.ExecuteNonQueryAsync();
                }
                using var version = db.CreateCommand();
                version.CommandText = "PRAGMA user_version=7;";
                await version.ExecuteNonQueryAsync();
            }
            var upgraded = new SqliteMessageRepository(oldPath);
            await upgraded.SaveBatchAsync([Manual("old", Prior, "合成历史消息")]);
            await upgraded.InitializeAsync();
            await upgraded.InitializeAsync();
            Assert.Equal("合成历史消息", Assert.Single(await upgraded.ReadRecentAsync()).Text);
            using var verified = new SqliteConnection($"Data Source={oldPath};Pooling=False");
            await verified.OpenAsync();
            using var check = verified.CreateCommand();
            check.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, await check.ExecuteScalarAsync());
        }
        finally { File.Delete(oldPath); }
    }

    private async Task<AiJointPreview> SeedManualJointAsync()
    {
        await store.SaveBatchAsync([Manual("original", Prior, "合成老师通知：明天交实验报告。联系方式 13800000000"),
            Manual("supplement", Prior.AddMinutes(4), "补充：18点前交")]);
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var rows = (await store.ReadAnalysisHistoryAsync(scope, Prior.AddDays(-1), Prior.AddDays(1))).Items;
        return AiJointPreview.Create(rows.Select(AiPreview.Create).ToArray(), Zone);
    }

    private async Task<string[]> SeedAutomaticAsync(DateTimeOffset since, params (string Group, string Id, string Text)[] items)
    {
        var source = new SourceDefinition { Adapter = "wxlocal", AccountAlias = "synthetic-account" };
        var groups = items.Select(item => item.Group).Distinct().ToArray();
        await store.SaveSourceAsync(source);
        await store.MergeConversationsAsync(source.Id, groups.Select(group => new SourceConversation(group, $"合成会话 {group}", "group")).ToArray());
        await store.SelectConversationsAsync(source.Id, groups);
        await store.SetSourceEnabledAsync(source.Id, true);
        source = Assert.Single(await store.ReadSourcesAsync());
        var messages = items.Select((item, index) => Manual(item.Id, since.AddMinutes(index + 1), item.Text) with
        {
            Source = "wechat", Adapter = source.Adapter, AccountId = source.AccountAlias, ConversationId = item.Group
        });
        foreach (var group in messages.GroupBy(item => item.ConversationId))
            await store.CommitPageAsync(source, group.Key, new(group.ToArray(), "synthetic-cursor", false));
        return (await store.ReadAiScopesAsync()).Select(scope => scope.Id).ToArray();
    }

    private async Task<IReadOnlyList<ReviewCandidate>> WaitForCandidatesAsync(int count, CancellationToken ct)
    {
        while (true)
        {
            var candidates = await store.ReadCandidatesAsync(false, ct);
            if (candidates.Count >= count) return candidates;
            await Task.Delay(20, ct);
        }
    }

    private static async Task<string> ReadOutboundAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        return payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    }

    private static NormalizedMessage Manual(string id, DateTimeOffset sent, string text) => ImportTests.Example() with
    {
        Source = "manual", Adapter = "manual", AccountId = "synthetic-account", ConversationId = "synthetic-course",
        SenderId = "synthetic-sender", SenderName = "合成老师", MessageId = id, SentAt = sent, ReceivedAt = sent, Text = text
    };

    private static HttpResponseMessage Reply(string response) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = response } } }
        }))
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class SkipAll : ILocalTriage
    {
        public Task<LocalTriageDecision> EvaluateAsync(string modelPath, AiWorkItem anchor, IReadOnlyList<AiWorkItem> nearby,
            CancellationToken ct) => Task.FromResult(new LocalTriageDecision(false, []));
    }

    private sealed class BatchClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
