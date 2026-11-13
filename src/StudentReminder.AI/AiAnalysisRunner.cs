using StudentReminder.Core;

namespace StudentReminder.AI;

public sealed record AiPreview(AiWorkItem Work, string OutboundText)
{
    public static AiPreview Create(AiWorkItem item) => new(item, AnalysisText.Prepare(item.Message.Text));
}

public sealed record AiJointPreview(AiPreview Anchor, IReadOnlyList<AiWorkItem> Related, AiInput Input)
{
    public static AiJointPreview Create(IReadOnlyList<AiPreview> previews, TimeZoneInfo zone)
    {
        if (previews.Count is < 2 or > AnalysisContext.MaxMessages)
            throw new ArgumentException("请选择同一会话的 2～4 条消息。");
        var ordered = previews.OrderByDescending(row => row.Work.Message.Message.SentAt)
            .ThenByDescending(row => row.Work.Message.Id, StringComparer.Ordinal).ToArray();
        var anchor = ordered.FirstOrDefault(row => row.Work.Status is not ("empty" or "extracted")) ??
            throw new ArgumentException("至少选择一条还没有提取结果的消息。");
        var related = ordered.Where(row => !ReferenceEquals(row, anchor)).Select(row => row.Work).ToArray();
        return new(anchor, related, AnalysisContext.Create(anchor.Work, related, zone));
    }
}

public sealed class AiAnalysisRunner(IAiAnalysisStore store, IAiProvider provider, TimeProvider? clock = null,
    ILocalTriage? localTriage = null)
{
    private const int QueueCapacity = 60;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int running;
    private (string Key, LocalTriageDecision Decision)? lastDecision;

    public Task RunAutomaticAsync(AiProfile profile, string key, IReadOnlyList<string> scopes, DateTimeOffset since,
        TimeZoneInfo zone, IProgress<string>? progress, CancellationToken ct, string? localModelPath = null,
        CloudBatchSettings? batch = null) =>
        RunExclusiveAsync(profile, key, async () =>
        {
            if (scopes.Count == 0) throw new ArgumentException("请至少选择一个要分析的本地直读会话。");
            var scopeIds = scopes.Distinct(StringComparer.Ordinal).ToArray();
            if (batch is not null)
            {
                batch.Validate();
                if (localModelPath is null || provider is not IAiBatchProvider batchProvider)
                    throw new ArgumentException("分批提交需要启用本地初筛并配置兼容的云服务。");
                await RunBatchedAsync(batchProvider, profile, key, scopeIds, since, zone, localModelPath, batch, progress, ct)
                    .ConfigureAwait(false);
                return;
            }
            progress?.Report("自动分析已开启，等待授权会话的新消息。");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var work = await store.ReadAutomaticAsync(scopeIds, since, time.GetUtcNow(), ct).ConfigureAwait(false);
                DateTimeOffset? next = null;
                foreach (var item in work)
                {
                    var outcome = await ProcessAsync(AiPreview.Create(item), profile, key, since, zone, progress, ct,
                        localModelPath).ConfigureAwait(false);
                    if (outcome.DailyLimit || outcome.Throttled)
                    {
                        next = outcome.AvailableAt;
                        break;
                    }
                }
                var poll = time.GetUtcNow().AddSeconds(next is null ? 3 : 60);
                await WaitUntilAsync(next is { } wake && wake < poll ? wake : poll, ct).ConfigureAwait(false);
            }
        });

    private async Task RunBatchedAsync(IAiBatchProvider batchProvider, AiProfile profile, string key,
        IReadOnlyList<string> scopeIds, DateTimeOffset since, TimeZoneInfo zone, string modelPath, CloudBatchSettings settings,
        IProgress<string>? progress, CancellationToken ct)
    {
        var queued = new List<QueuedJob>();
        DateTimeOffset? serviceAvailableAt = null;
        progress?.Report($"自动分析已开启。单个会话满 {settings.Count} 条或等待 {settings.WaitSeconds} 秒提交一批。");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            if (serviceAvailableAt is null || serviceAvailableAt <= now)
            {
                var ready = ReadyBatch(queued, settings, now);
                if (ready.Length != 0)
                {
                    serviceAvailableAt = await SendBatchAsync(batchProvider, ready.Select(row => row.Job).ToArray(),
                        profile, key, since, zone, progress, ct).ConfigureAwait(false);
                    if (serviceAvailableAt is null)
                        foreach (var row in ready) queued.Remove(row);
                    continue;
                }
            }

            if (queued.Count < QueueCapacity)
            {
                var work = await store.ReadAutomaticAsync(scopeIds, since, now, ct,
                    queued.Select(row => row.Job.Anchor.Message.Id).ToArray()).ConfigureAwait(false);
                foreach (var item in work.Take(QueueCapacity - queued.Count))
                {
                    var current = await store.ReadAuthorizedAsync(item.Scope, item.Message.Id, since, ct).ConfigureAwait(false);
                    if (current is null || current.Status is "empty" or "extracted" or "filtered" or "failed") continue;
                    var job = await PrepareHybridAsync(current, AnalysisText.Prepare(current.Message.Text), since, zone,
                        modelPath, progress, ct).ConfigureAwait(false);
                    if (job is not null) queued.Add(new(job, time.GetUtcNow()));
                    now = time.GetUtcNow();
                    if ((serviceAvailableAt is null || serviceAvailableAt <= now) && ReadyBatch(queued, settings, now).Length != 0)
                        break;
                }
            }

            now = time.GetUtcNow();
            var canSend = serviceAvailableAt is null || serviceAvailableAt <= now;
            if (canSend && ReadyBatch(queued, settings, now).Length != 0) continue;
            var poll = now.AddSeconds(3);
            if (queued.Count != 0)
            {
                var due = queued[0].QueuedAt.AddSeconds(settings.WaitSeconds);
                var wake = canSend ? due : serviceAvailableAt!.Value;
                if (wake > now && wake < poll) poll = wake;
                progress?.Report(canSend ? $"本地排队 {queued.Count} 条，最早一批预计 {TimeZoneInfo.ConvertTime(due, zone):HH:mm:ss} 提交。"
                    : $"本地排队 {queued.Count} 条，云端暂缓至 {TimeZoneInfo.ConvertTime(wake, zone):MM-dd HH:mm:ss}。");
            }
            await WaitUntilAsync(poll, ct).ConfigureAwait(false);
        }
    }

    private static QueuedJob[] ReadyBatch(List<QueuedJob> queued, CloudBatchSettings settings, DateTimeOffset now) =>
        queued.GroupBy(row => row.Job.Anchor.Scope.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() >= settings.Count || group.First().QueuedAt.AddSeconds(settings.WaitSeconds) <= now)
            ?.Take(settings.Count).ToArray() ?? [];

    private async Task<DateTimeOffset?> SendBatchAsync(IAiBatchProvider batchProvider, IReadOnlyList<AiBatchJob> jobs,
        AiProfile profile, string key, DateTimeOffset since, TimeZoneInfo zone, IProgress<string>? progress, CancellationToken ct)
    {
        var reservation = await store.ReserveBatchAsync(jobs, profile, since, time.GetUtcNow(), zone, ct).ConfigureAwait(false);
        if (reservation.DailyLimit) progress?.Report("已达到今天的 100 次提取请求上限。");
        if (reservation.RequestId is not { } requestId) return reservation.AvailableAt;
        ct.ThrowIfCancellationRequested();
        progress?.Report($"正在云端提取同一会话的 {reservation.Attempts.Count} 条消息…");
        try
        {
            var result = await batchProvider.ExtractBatchAsync(profile, key, reservation.Attempts.Select(row => row.Job).ToArray(),
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await store.CompleteBatchAsync(reservation, result, since, ct).ConfigureAwait(false);
            progress?.Report("本批云端提取完成，可到「事项」查看待审核候选。");
        }
        catch (AiFailureException error)
        {
            ct.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            var latest = reservation.Attempts.Max(row => NextAttempt(now, row.Attempt, error));
            var failures = reservation.Attempts.Select(row => new AiBatchFailure(row,
                error.Retryable && row.Attempt < AiLimits.MaxAttempts ? NextAttempt(now, row.Attempt, error) : null)).ToArray();
            var cooldown = error.Code == "rate" || error.RetryAfter.HasValue ? latest : (DateTimeOffset?)null;
            await store.RecordBatchFailureAsync(requestId, failures, error, cooldown, ct).ConfigureAwait(false);
            if (!error.Retryable) throw;
            progress?.Report(AiFailureException.Describe(error.Code) + " 本批未完成，可重试的消息会重新排队。");
        }
        return null;
    }

    public Task RunCombinedAsync(AiProfile profile, string key, AiJointPreview preview, IProgress<string>? progress,
        CancellationToken ct) => RunExclusiveAsync(profile, key, async () =>
    {
        while (true)
        {
            var outcome = await ProcessAsync(preview.Anchor, profile, key, null, preview.Input.TimeZone,
                progress, ct, joint: preview).ConfigureAwait(false);
            if (outcome.DailyLimit || outcome.AvailableAt is not { } next) return;
            await WaitUntilAsync(next, ct).ConfigureAwait(false);
        }
    });

    public Task RunManualAsync(AiProfile profile, string key, IReadOnlyList<AiPreview> previews, TimeZoneInfo zone,
        IProgress<string>? progress, CancellationToken ct) =>
        RunExclusiveAsync(profile, key, async () =>
        {
            foreach (var preview in previews.DistinctBy(value => value.Work.Message.Id))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var outcome = await ProcessAsync(preview, profile, key, null, zone, progress, ct).ConfigureAwait(false);
                    if (outcome.DailyLimit) return;
                    if (outcome.AvailableAt is not { } next) break;
                    await WaitUntilAsync(next, ct).ConfigureAwait(false);
                }
            }
            progress?.Report("本批手动分析结束。可查看事项或刷新分析状态。");
        });

    private async Task RunExclusiveAsync(AiProfile profile, string key, Func<Task> run)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("已有分析任务在运行。");
        try
        {
            provider.ValidateConfig(profile, key);
            await run().ConfigureAwait(false);
        }
        finally
        {
            lastDecision = null;
            Volatile.Write(ref running, 0);
        }
    }

    private async Task<ProcessOutcome> ProcessAsync(AiPreview preview, AiProfile profile, string key,
        DateTimeOffset? since, TimeZoneInfo zone, IProgress<string>? progress, CancellationToken ct,
        string? localModelPath = null, AiJointPreview? joint = null)
    {
        var item = await store.ReadAuthorizedAsync(preview.Work.Scope, preview.Work.Message.Id, since, ct).ConfigureAwait(false);
        if (item is null || item.Status is "empty" or "extracted") return default;
        var prepared = AnalysisText.Prepare(item.Message.Text);
        if (!string.Equals(prepared, preview.OutboundText, StringComparison.Ordinal)) return default;
        AiInput input = new(prepared, item.Message.Message.SentAt, zone);
        IReadOnlyList<AiWorkItem>? context = null;
        if (joint is not null)
        {
            context = joint.Related;
            input = AnalysisContext.Create(item, context, zone);
            if (ChatCompletionAiProvider.PreviewInput(input) != ChatCompletionAiProvider.PreviewInput(joint.Input))
            {
                progress?.Report("消息已变化，请重新预览联合分析。");
                return default;
            }
        }
        else if (since is { } activated && localModelPath is not null)
        {
            var job = await PrepareHybridAsync(item, prepared, activated, zone, localModelPath, progress, ct).ConfigureAwait(false);
            if (job is null) return default;
            context = job.Context;
            input = job.Input;
        }
        else if (since.HasValue && !AnalysisText.IsRelevant(prepared))
        {
            await store.MarkFilteredAsync(item.Message.Id, ct).ConfigureAwait(false);
            return default;
        }
        var permit = await store.ReserveAsync(item, profile, since, time.GetUtcNow(), zone, ct, context).ConfigureAwait(false);
        if (permit.DailyLimit) progress?.Report("已达到今天的 100 次提取上限。");
        if (permit.RequestId is not { } requestId)
        {
            if (!permit.DailyLimit && permit.AvailableAt is { } available && available - time.GetUtcNow() > AiLimits.RequestInterval)
                progress?.Report($"服务暂缓发送，{TimeZoneInfo.ConvertTime(available, zone):MM-dd HH:mm:ss} 后可继续。");
            if (joint is not null && permit.AvailableAt is null)
                progress?.Report("消息授权或状态已变化，请刷新并重新预览。");
            return new(permit.AvailableAt, permit.DailyLimit, permit.AvailableAt.HasValue);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report("正在分析已授权的消息…");
        try
        {
            var result = await provider.ExtractCandidatesAsync(profile, key, input, item.Evidence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await store.CompleteAsync(item, requestId, result, since, ct, context).ConfigureAwait(false);
            progress?.Report(result.Events.Count == 0 ? "分析完成，未识别到事项。" : "分析完成，请到「事项」审核候选。");
            return default;
        }
        catch (AiFailureException error)
        {
            ct.ThrowIfCancellationRequested();
            var nextAttempt = NextAttempt(time.GetUtcNow(), permit.Attempt, error);
            DateTimeOffset? retryAt = error.Retryable && permit.Attempt < AiLimits.MaxAttempts ? nextAttempt : null;
            DateTimeOffset? providerAvailableAt = error.Code == "rate" || error.RetryAfter.HasValue ? nextAttempt : null;
            await store.RecordFailureAsync(item.Message.Id, requestId, error, retryAt, providerAvailableAt, ct).ConfigureAwait(false);
            if (!error.Retryable) throw;
            var retryLabel = retryAt is { } next
                ? $" 下次可重试：{TimeZoneInfo.ConvertTime(next, zone):MM-dd HH:mm:ss}。" : " 已用完本轮重试。";
            progress?.Report(AiFailureException.Describe(error.Code) + retryLabel);
            return new(retryAt, false, false);
        }
    }

    private async Task<AiBatchJob?> PrepareHybridAsync(AiWorkItem item, string prepared, DateTimeOffset since,
        TimeZoneInfo zone, string modelPath, IProgress<string>? progress, CancellationToken ct)
    {
        var nearby = await store.ReadNearbyAsync(item, since, ct).ConfigureAwait(false);
        var decision = await ReadLocalDecisionAsync(modelPath, item, nearby, progress, ct).ConfigureAwait(false);
        if (!decision.Relevant && !AnalysisText.HasProtectedSignal(prepared))
        {
            await store.MarkFilteredAsync(item.Message.Id, ct, "local_filtered").ConfigureAwait(false);
            progress?.Report("本地初筛已跳过一条消息，可在历史消息中查看或手动分析。");
            return null;
        }
        var related = decision.Related.Select(index => nearby[index - 1]).ToList();
        if (related.Count == 0 && AnalysisText.IsContextFollowUp(prepared))
        {
            var sender = item.Message.Message.SenderId;
            var fallback = nearby.FirstOrDefault(row => sender != "unknown" && row.Message.Message.SenderId == sender &&
                AnalysisText.HasProtectedSignal(row.Message.Text));
            if (fallback is not null) related.Add(fallback);
        }
        return new(item, AnalysisContext.Create(item, related, zone), related);
    }

    private static DateTimeOffset NextAttempt(DateTimeOffset now, int attempt, AiFailureException error)
    {
        var delay = attempt == 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);
        return now + (error.RetryAfter > delay ? error.RetryAfter.Value : delay);
    }

    private async Task<LocalTriageDecision> ReadLocalDecisionAsync(string path, AiWorkItem item,
        IReadOnlyList<AiWorkItem> nearby, IProgress<string>? progress, CancellationToken ct)
    {
        if (localTriage is null) throw new AiFailureException("local", false);
        var identity = SourceKeys.Hash(nearby.Prepend(item).SelectMany(row => new[] { row.Message.Id, row.Message.Text }).ToArray());
        if (lastDecision is { } cached && cached.Key == identity) return cached.Decision;
        progress?.Report("正在本机筛选消息并查找关联前文…");
        var result = await localTriage.EvaluateAsync(path, item, nearby, ct).ConfigureAwait(false);
        if (result.Related.Count >= AnalysisContext.MaxMessages || result.Related.Any(index => index < 1 || index > nearby.Count) ||
            result.Related.Distinct().Count() != result.Related.Count) throw new AiFailureException("local_format", false);
        lastDecision = (identity, result);
        return result;
    }

    private async Task WaitUntilAsync(DateTimeOffset next, CancellationToken ct)
    {
        // Wait in bounded increments so long Retry-After values stay cancellable.
        while (next - time.GetUtcNow() is var wait && wait > TimeSpan.Zero)
            await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), time, ct).ConfigureAwait(false);
    }

    private readonly record struct ProcessOutcome(DateTimeOffset? AvailableAt, bool DailyLimit, bool Throttled);
    private sealed record QueuedJob(AiBatchJob Job, DateTimeOffset QueuedAt);
}
