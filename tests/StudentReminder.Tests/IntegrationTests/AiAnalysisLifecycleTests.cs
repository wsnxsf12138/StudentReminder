using StudentReminder.AI;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.Tests.IntegrationTests;

public sealed class AiAnalysisLifecycleTests : IAsyncLifetime
{
    private static readonly AiProfile Profile = new("deepseek", "synthetic-model");
    private const string FirstGroup = "synthetic-one@chatroom";
    private const string SecondGroup = "synthetic-two@chatroom";
    private const string Unselected = "synthetic-hidden@chatroom";
    private readonly string path = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.db");
    private readonly SourceDefinition wxLocal = new() { Adapter = "wxlocal", AccountAlias = "synthetic-wx", Port = 0 };
    private SqliteMessageRepository store = null!;

    public async Task InitializeAsync()
    {
        store = new SqliteMessageRepository(path);
        await store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AutomaticAnalysisUsesOnlySelectedNewWxLocalTextAndCanReviewFilteredMessages()
    {
        var source = await SetupWxLocalAsync();
        await CommitAsync(source, FirstGroup, "preexisting", "已有考试通知", DateTimeOffset.UtcNow.AddMinutes(-2));
        var since = DateTimeOffset.UtcNow;
        await CommitAsync(source, FirstGroup, "backfill", "回溯考试通知", since.AddMinutes(-1));
        await CommitAsync(source, FirstGroup, "smalltalk", "你好呀", DateTimeOffset.UtcNow);
        await CommitAsync(source, FirstGroup, "new", "报名通知。联系方式 13800000000，见 https://example.invalid/path", DateTimeOffset.UtcNow);
        await CommitAsync(source, SecondGroup, "not-ai-authorized", "另一个群的报名通知", DateTimeOffset.UtcNow);
        await store.SaveBatchAsync([Message(source, FirstGroup, "json-spoof", "JSON 导入模拟考试通知", DateTimeOffset.UtcNow)]);
        await store.SaveBatchAsync([Message(source, Unselected, "unselected", "未选中的会话报名通知", DateTimeOffset.UtcNow)]);

        var scopes = await store.ReadAiScopesAsync();
        Assert.DoesNotContain(scopes, row => row.Name == Unselected);
        var scope = scopes.Single(row => row.Name == FirstGroup);
        var eligible = await store.ReadAutomaticAsync([scope.Id], since, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(new[] { "smalltalk", "new" }, eligible.Select(row => row.Message.Message.MessageId));
        var received = new TaskCompletionSource<AiInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProvider((input, _, _) =>
        {
            received.TrySetResult(input);
            return Task.FromResult(Extract("报名事项", "new"));
        });
        var runner = new AiAnalysisRunner(store, provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var run = runner.RunAutomaticAsync(Profile, "synthetic-key", [scope.Id], since, TimeZoneInfo.Utc, null, cancellation.Token);
        try
        {
            var outbound = await received.Task.WaitAsync(cancellation.Token);
            Assert.Contains("[已隐藏]", outbound.Text);
            Assert.DoesNotContain("13800000000", outbound.Text);
            Assert.DoesNotContain("example.invalid", outbound.Text);
            while ((await store.ReadCandidatesAsync(false, cancellation.Token)).Count == 0)
                await Task.Delay(20, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        var history = (await store.ReadAnalysisHistoryAsync(scope, since.AddDays(-1), since.AddDays(1))).Items;
        Assert.Equal("filtered", history.Single(row => row.Message.Message.MessageId == "smalltalk").Status);
        Assert.Equal("pending", history.Single(row => row.Message.Message.MessageId == "backfill").Status);
        Assert.Equal("extracted", history.Single(row => row.Message.Message.MessageId == "new").Status);
        var stats = await store.ReadAiStatisticsAsync(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal(1, stats.Requests);
        Assert.Equal(1, stats.Filtered);
        Assert.Equal(25, stats.PromptTokens);
    }

    [Fact]
    public async Task PausingWaitsForAnInFlightRequestAndRevokedMessagesCannotBeResubmitted()
    {
        var source = await SetupWxLocalAsync();
        var since = DateTimeOffset.UtcNow;
        await CommitAsync(source, FirstGroup, "first", "考试通知", DateTimeOffset.UtcNow);
        await CommitAsync(source, FirstGroup, "second", "报名通知", DateTimeOffset.UtcNow);
        var scope = (await store.ReadAiScopesAsync()).Single(row => row.Name == FirstGroup);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var sends = 0;
        var provider = new StubProvider(async (_, _, ct) =>
        {
            Interlocked.Increment(ref sends);
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { finished = true; }
            return Extract("不可写入", "new");
        });
        var runner = new AiAnalysisRunner(store, provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var run = runner.RunAutomaticAsync(Profile, "synthetic-key", [scope.Id], since, TimeZoneInfo.Utc, null, cancellation.Token);
        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(finished);
        var previews = (await store.ReadAnalysisHistoryAsync(scope, since.AddDays(-1), since.AddDays(1))).Items.Select(AiPreview.Create).ToArray();
        await store.SelectConversationsAsync(source.Id, []);
        await runner.RunManualAsync(Profile, "synthetic-key", previews, TimeZoneInfo.Utc, null, CancellationToken.None);
        Assert.Equal(1, sends);
        Assert.Empty(await store.ReadCandidatesAsync(true));
        Assert.Empty((await store.ReadAnalysisHistoryAsync(scope, since.AddDays(-1), since.AddDays(1))).Items);
    }

    [Fact]
    public async Task ForwardedEvidenceMergesAndExplicitChangesCreateVersionsAndKeepOriginalEvidence()
    {
        var source = await SetupWxLocalAsync();
        var now = DateTimeOffset.UtcNow;
        await CommitAsync(source, FirstGroup, "original", "周五18点前交报告", now);
        await CommitAsync(source, SecondGroup, "forward", "周五18点前交报告", now);
        var clock = new TestClock(now);
        var calls = 0;
        var provider = new StubProvider((input, _, _) =>
        {
            calls++;
            clock.Advance(TimeSpan.FromSeconds(11));
            return Task.FromResult(Extract("实验报告", input.Text.Contains("改期") ? "update" : input.Text.Contains("取消") ? "cancel" : "new"));
        });
        var runner = new AiAnalysisRunner(store, provider, clock);
        await AnalyzeHistoryAsync(runner, now);
        var original = Assert.Single(await store.ReadCandidatesAsync(false));
        Assert.Equal(2, (await store.ReadReviewEvidenceAsync(original.Id, false)).Count);
        Assert.Empty(await store.ReadEventsAsync());
        var eventId = await store.ConfirmCandidateAsync(original.Id, original.Draft with { Location = "教学楼" }, null, null);
        var versionOne = Assert.Single(await store.ReadEventsAsync());
        Assert.Equal(1, versionOne.Version);
        Assert.Equal("教学楼", versionOne.Details.Location);
        Assert.False(versionOne.Details.NeedsConfirmation);

        await AnalyzeHistoryAsync(runner, now);
        Assert.Equal(2, calls);
        await CommitAsync(source, FirstGroup, "update", "改期到周六，实验报告继续提交", now);
        await AnalyzeHistoryAsync(runner, now);
        var change = Assert.Single(await store.ReadCandidatesAsync(false));
        Assert.Equal("update", change.ChangeKind);
        Assert.Equal("教学楼", Assert.Single(await store.ReadEventsAsync()).Details.Location);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConfirmCandidateAsync(change.Id, change.Draft, eventId, 99));
        var edited = change.Draft with { DeadlineAt = now.AddDays(2), Location = "教学楼" };
        Assert.Equal(eventId, await store.ConfirmCandidateAsync(change.Id, edited, eventId, versionOne.Version));
        Assert.Equal(2, Assert.Single(await store.ReadEventsAsync()).Version);
        Assert.Equal(3, (await store.ReadReviewEvidenceAsync(eventId, true)).Count);
        Assert.Equal(new[] { 2, 1 }, (await store.ReadEventVersionsAsync(eventId)).Select(row => row.Version));
        await store.RecallAsync(source, FirstGroup, "update");
        Assert.Contains(await store.ReadReviewEvidenceAsync(eventId, true), row => row.Status == "recalled");
        Assert.Equal(edited.DeadlineAt, Assert.Single(await store.ReadEventsAsync()).Details.DeadlineAt);

        await store.UpdateEventAsync(eventId, 2, "complete");
        Assert.Equal("completed", Assert.Single(await store.ReadEventsAsync()).Status);
        await CommitAsync(source, FirstGroup, "cancel", "通知：取消实验报告", now);
        await AnalyzeHistoryAsync(runner, now);
        var cancellation = Assert.Single(await store.ReadCandidatesAsync(false));
        Assert.Equal("cancel", cancellation.ChangeKind);
        await store.ConfirmCandidateAsync(cancellation.Id, cancellation.Draft, eventId, 3);
        Assert.Equal("canceled", Assert.Single(await store.ReadEventsAsync()).Status);
        await store.UpdateEventAsync(eventId, 4, "delete");
        Assert.Empty(await store.ReadEventsAsync());
        Assert.Equal("deleted", (await store.ReadEventVersionsAsync(eventId))[0].Status);
        await CommitAsync(source, FirstGroup, "repeat", "周五18点前交报告", now);
        await AnalyzeHistoryAsync(runner, now);
        Assert.Empty(await store.ReadCandidatesAsync(false));
        Assert.Empty(await store.ReadEventsAsync());
    }

    [Fact]
    public async Task CandidateWithOnlyRecalledEvidenceCanBeIgnoredOrDeletedButCannotBeConfirmed()
    {
        var source = await SetupWxLocalAsync();
        var now = DateTimeOffset.UtcNow;
        await CommitAsync(source, FirstGroup, "exam", "考试合成通知", now);
        var runner = new AiAnalysisRunner(store, new StubProvider((_, _, _) => Task.FromResult(Extract("考试", "new"))));
        await AnalyzeHistoryAsync(runner, now);
        var candidate = Assert.Single(await store.ReadCandidatesAsync(false));
        await store.RecallAsync(source, FirstGroup, "exam");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmCandidateAsync(candidate.Id, candidate.Draft, null, null));
        await store.SetCandidateDispositionAsync(candidate.Id, delete: false);
        Assert.Empty(await store.ReadCandidatesAsync(false));
        Assert.Equal("ignored", Assert.Single(await store.ReadCandidatesAsync(true)).Status);
        await store.SetCandidateDispositionAsync(candidate.Id, delete: true);
        Assert.Empty(await store.ReadCandidatesAsync(true));
    }

    [Fact]
    public async Task RetriesUseRetryAfterAndStopAfterTheThirdAttempt()
    {
        var source = await SetupWxLocalAsync();
        var now = DateTimeOffset.UtcNow;
        var clock = new TestClock(now);
        await CommitAsync(source, FirstGroup, "retry", "截止通知", now);
        var attempts = new List<DateTimeOffset>();
        var runner = new AiAnalysisRunner(store, new StubProvider((_, _, _) =>
        {
            attempts.Add(clock.GetUtcNow());
            throw new AiFailureException("rate", true, TimeSpan.FromMinutes(8));
        }), clock);
        var progress = new InlineProgress(text =>
        {
            if (text.Contains("下次可重试")) clock.Advance(TimeSpan.FromMinutes(8));
        });
        await AnalyzeHistoryAsync(runner, now, progress);
        Assert.Equal(new[] { now, now.AddMinutes(8), now.AddMinutes(16) }, attempts);
        var stats = await store.ReadAiStatisticsAsync(clock.GetUtcNow(), TimeZoneInfo.Utc);
        Assert.Equal(3, stats.Requests);
        Assert.Equal(3, stats.MissingUsage);
        Assert.Equal(1, stats.Failed);
    }

    [Fact]
    public async Task ExtractionBudgetPersistsAcrossProvidersAndResetsAtLocalMidnight()
    {
        var now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
        await store.SaveBatchAsync(Enumerable.Range(0, 101).Select(index => ManualMessage(index, now)).ToArray());
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var rows = (await store.ReadAnalysisHistoryAsync(scope, now.AddDays(-1), now.AddDays(1))).Items;
        var ct = CancellationToken.None;
        var zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-8", TimeSpan.FromHours(8), "synthetic-8", "synthetic-8");
        var first = await store.ReserveAsync(rows[0], Profile, null, now, zone, ct);
        Assert.NotNull(first.RequestId);
        var anotherProvider = new AiProfile("doubao", "synthetic-model");
        var throttled = await store.ReserveAsync(rows[1], anotherProvider, null, now.AddSeconds(9), zone, ct);
        Assert.Null(throttled.RequestId);
        Assert.Equal(now.AddSeconds(10), throttled.AvailableAt);
        for (var index = 1; index < 100; index++)
        {
            var permit = await store.ReserveAsync(rows[index], anotherProvider, null, now.AddSeconds(11 * index), zone, ct);
            Assert.NotNull(permit.RequestId);
        }
        var reopened = new SqliteMessageRepository(path);
        var quota = await reopened.ReserveAsync(rows[100], Profile, null, now.AddMinutes(20), zone, ct);
        Assert.True(quota.DailyLimit);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T16:00:00Z"), quota.AvailableAt);
        var nextDay = await reopened.ReserveAsync(rows[100], Profile, null, quota.AvailableAt!.Value, zone, ct);
        Assert.NotNull(nextDay.RequestId);
    }

    [Fact]
    public async Task RateLimitPreventsOtherMessagesFromCallingTheSameProviderBeforeRetryAfter()
    {
        var now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
        await store.SaveBatchAsync([ManualMessage(1, now), ManualMessage(2, now)]);
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var page = await store.ReadAnalysisHistoryAsync(scope, now.AddDays(-1), now.AddDays(1));
        var ct = CancellationToken.None;
        var permit = await store.ReserveAsync(page.Items[0], Profile, null, now, TimeZoneInfo.Utc, ct);
        var available = now.AddMinutes(8);
        await store.RecordFailureAsync(page.Items[0].Message.Id, permit.RequestId!.Value,
            new AiFailureException("rate", true, TimeSpan.FromMinutes(8)), available, available, ct);

        var reopened = new SqliteMessageRepository(path);
        var blocked = await reopened.ReserveAsync(page.Items[1], Profile, null, now.AddSeconds(30), TimeZoneInfo.Utc, ct);
        Assert.Null(blocked.RequestId);
        Assert.Equal(available, blocked.AvailableAt);
        var allowed = await reopened.ReserveAsync(page.Items[1], Profile, null, available, TimeZoneInfo.Utc, ct);
        Assert.NotNull(allowed.RequestId);
    }

    [Fact]
    public async Task HistoryPagesReachAllMessagesWithIdenticalTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        await store.SaveBatchAsync(Enumerable.Range(0, 204).Select(index => ManualMessage(index, now)).ToArray());
        var scope = Assert.Single(await store.ReadAiScopesAsync());
        var first = await store.ReadAnalysisHistoryAsync(scope, now.AddDays(-1), now.AddDays(1));
        Assert.Equal(200, first.Items.Count);
        Assert.NotNull(first.Next);
        var second = await store.ReadAnalysisHistoryAsync(scope, now.AddDays(-1), now.AddDays(1), before: first.Next);
        Assert.Equal(4, second.Items.Count);
        Assert.Null(second.Next);
        Assert.Equal(204, first.Items.Concat(second.Items).Select(row => row.Message.Id).Distinct().Count());
    }

    private async Task AnalyzeHistoryAsync(AiAnalysisRunner runner, DateTimeOffset around, IProgress<string>? progress = null)
    {
        var previews = new List<AiPreview>();
        foreach (var scope in await store.ReadAiScopesAsync())
            previews.AddRange((await store.ReadAnalysisHistoryAsync(scope, around.AddDays(-1), around.AddDays(1))).Items
                .Where(row => row.Status is not ("empty" or "extracted")).Select(AiPreview.Create));
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await runner.RunManualAsync(Profile, "synthetic-key", previews, TimeZoneInfo.Utc, progress, timeout.Token);
    }

    private async Task<SourceDefinition> SetupWxLocalAsync()
    {
        await store.SaveSourceAsync(wxLocal);
        await store.MergeConversationsAsync(wxLocal.Id, new[] { FirstGroup, SecondGroup, Unselected }
            .Select(group => new SourceConversation(group, group, "group")).ToArray());
        await store.SelectConversationsAsync(wxLocal.Id, [FirstGroup, SecondGroup]);
        await store.SetSourceEnabledAsync(wxLocal.Id, true);
        return Assert.Single(await store.ReadSourcesAsync());
    }

    private Task<ImportResult> CommitAsync(SourceDefinition source, string group, string id, string text, DateTimeOffset sent) =>
        store.CommitPageAsync(source, group, new([Message(source, group, id, text, sent)], id, false));

    private static NormalizedMessage Message(SourceDefinition source, string group, string id, string text, DateTimeOffset sent) => new()
    {
        Source = "wechat", Adapter = source.Adapter, AccountId = source.AccountAlias, ConversationId = group,
        ConversationName = group, MessageId = id, SenderId = "synthetic-sender", Text = text, SentAt = sent, ReceivedAt = DateTimeOffset.UtcNow
    };

    private static NormalizedMessage ManualMessage(int index, DateTimeOffset now) => new()
    {
        Source = "manual", Adapter = "manual", AccountId = "synthetic", ConversationId = "quota", ConversationName = "合成预算测试",
        MessageId = $"msg-{index}", SenderId = "synthetic", SentAt = now, ReceivedAt = now, Text = "合成通知"
    };

    private static AiExtraction Extract(string title, string kind) =>
        new([new(new EventCandidate { Title = title, Category = "assignment", Action = "提交报告", Evidence = [] }, kind)], new(25, 10));

    private sealed class StubProvider(Func<AiInput, MessageEvidence, CancellationToken, Task<AiExtraction>> extract) : IAiProvider
    {
        public void ValidateConfig(AiProfile profile, string key) => profile.Validate();
        public Task TestConnectionAsync(AiProfile profile, string key, CancellationToken ct) => Task.CompletedTask;
        public Task<AiExtraction> ExtractCandidatesAsync(AiProfile profile, string key, AiInput input, MessageEvidence evidence,
            CancellationToken ct) => extract(input, evidence, ct);
        public Task<AiDigestPolish> GenerateDigestAsync(AiProfile profile, string key, IReadOnlyList<AiDigestItem> items,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
