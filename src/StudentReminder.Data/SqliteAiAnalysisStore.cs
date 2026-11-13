using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository : IAiAnalysisStore
{
    private const string AnalysisRows = """
        SELECT v.id,s.type,$adapter,s.account_alias,v.name,v.type,m.id,m.payload_json,m.status,
               COALESCE(a.status,'pending'),a.retry_at,a.error_code
        FROM messages m JOIN conversations v ON v.id=m.conversation_id JOIN sources s ON s.id=v.source_id
        LEFT JOIN source_configs c ON c.source_id=s.id LEFT JOIN ai_analysis a ON a.message_id=m.id
        WHERE m.status='active' AND json_extract(m.payload_json,'$.content_type')='text'
        """;
    private const string AutomaticRows = """
        SELECT v.id,s.type,c.adapter,s.account_alias,v.name,v.type,m.id,m.payload_json,m.status,
               COALESCE(a.status,'pending'),a.retry_at,a.error_code
        FROM messages m JOIN conversations v ON v.id=m.conversation_id JOIN sources s ON s.id=v.source_id
        LEFT JOIN source_configs c ON c.source_id=s.id LEFT JOIN ai_analysis a ON a.message_id=m.id
        WHERE m.status='active' AND json_extract(m.payload_json,'$.content_type')='text'
        """;
    private const string LocalReaderAuthorization = " AND c.adapter IN ('wxlocal','qqlocal') AND v.selected=1";
    private const string LiveMessages = " AND m.wxlocal_ingested_at >= $since AND m.sent_at >= $since";
    private const string LocalHistory = " AND json_extract(m.payload_json,'$.adapter')=$adapter" + LocalReaderAuthorization;
    private const string MoectHistory = " AND s.type='wechat' AND v.id=$moect AND json_extract(m.payload_json,'$.adapter')='moect-json'";

    public async Task<IReadOnlyList<AiScope>> ReadAiScopesAsync(CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        var moect = await ReadMoectScopeAsync(connection, null, ct);
        using var command = Command(connection, null, """
            SELECT v.id,s.type,c.adapter,s.account_alias,v.name,v.type
            FROM conversations v JOIN sources s ON s.id=v.source_id JOIN source_configs c ON c.source_id=s.id
            WHERE c.adapter IN ('wxlocal','qqlocal') AND v.selected=1
            UNION ALL
            SELECT v.id,s.type,'moect-json',s.account_alias,v.name,v.type
            FROM conversations v JOIN sources s ON s.id=v.source_id WHERE v.id=$moect
            UNION ALL
            SELECT v.id,s.type,'manual',s.account_alias,v.name,v.type
            FROM conversations v JOIN sources s ON s.id=v.source_id WHERE s.type='manual'
            ORDER BY 3,5,1;
            """, ("$moect", moect));
        using var reader = await command.ExecuteReaderAsync(ct);
        var scopes = new List<AiScope>();
        while (await reader.ReadAsync(ct)) scopes.Add(ReadScope(reader));
        return scopes;
    }

    public async Task<AiProfile?> ReadAiProfileAsync(string provider, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, "SELECT value_json FROM app_settings WHERE key=$key;", ("$key", "ai-" + provider));
        return await command.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<AiProfile>(value) : null;
    }

    public async Task SaveAiProfileAsync(AiProfile profile, CancellationToken ct = default)
    {
        profile.Validate();
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            INSERT INTO app_settings(key,value_json) VALUES($key,$json)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;
            """, ("$key", "ai-" + profile.Provider), ("$json", JsonSerializer.Serialize(profile)));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AiWorkItem>> ReadAutomaticAsync(IReadOnlyList<string> scopeIds, DateTimeOffset since,
        DateTimeOffset now, CancellationToken ct, IReadOnlyList<string>? queued = null)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, AutomaticRows + LocalReaderAuthorization + LiveMessages + """

            AND v.id IN (SELECT value FROM json_each($scopes))
            AND json_extract(m.payload_json,'$.adapter')=c.adapter
            AND m.id NOT IN (SELECT value FROM json_each($queued))
            AND (a.status IS NULL OR (a.status='retry' AND a.retry_at <= $now))
            ORDER BY m.wxlocal_ingested_at,m.sent_at,m.id LIMIT 30;
            """, ("$since", Format(since)), ("$scopes", JsonSerializer.Serialize(scopeIds)), ("$now", Format(now)),
            ("$queued", JsonSerializer.Serialize(queued ?? [])));
        return await ReadWorkAsync(command, ct);
    }

    public async Task<HybridAnalysisSettings> ReadHybridSettingsAsync(CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, "SELECT value_json FROM app_settings WHERE key='ai-hybrid';");
        return await command.ExecuteScalarAsync(ct) is string value
            ? JsonSerializer.Deserialize<HybridAnalysisSettings>(value) ?? new() : new();
    }

    public async Task SaveHybridSettingsAsync(HybridAnalysisSettings settings, CancellationToken ct = default)
    {
        settings.Batch?.Validate();
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            INSERT INTO app_settings(key,value_json) VALUES('ai-hybrid',$json)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;
            """, ("$json", JsonSerializer.Serialize(settings)));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AiHistoryPage> ReadAnalysisHistoryAsync(AiScope scope, DateTimeOffset from,
        DateTimeOffset until, CancellationToken ct = default, AiHistoryCursor? before = null)
    {
        if (from >= until) throw new ArgumentException("请选择有效的日期范围。");
        using var connection = await OpenAsync(ct);
        var moect = scope.Adapter == "moect-json" ? await ReadMoectScopeAsync(connection, null, ct) : null;
        using var command = Command(connection, null, AnalysisRows + HistoryCondition(scope.Adapter) + """

            AND v.id=$scope AND m.sent_at >= $from AND m.sent_at < $until
            AND ($before IS NULL OR m.sent_at < $before OR (m.sent_at=$before AND m.id > $lastId))
            ORDER BY m.sent_at DESC,m.id LIMIT 201;
            """, ("$adapter", scope.Adapter), ("$moect", moect), ("$scope", scope.Id), ("$from", Format(from)),
            ("$until", Format(until)), ("$before", before is null ? null : Format(before.SentAt)), ("$lastId", before?.MessageId));
        var rows = await ReadWorkAsync(command, ct);
        return rows.Count <= 200 ? new(rows, null)
            : new(rows.Take(200).ToArray(), new(rows[199].Message.Message.SentAt, rows[199].Message.Id));
    }

    public async Task<AiWorkItem?> ReadAuthorizedAsync(AiScope scope, string messageId, DateTimeOffset? since, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        return await ReadAuthorizedCoreAsync(connection, null, scope, messageId, since, ct);
    }

    public async Task<IReadOnlyList<AiWorkItem>> ReadNearbyAsync(AiWorkItem anchor, DateTimeOffset since, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        var sent = anchor.Message.Message.SentAt;
        using var command = Command(connection, null, AnalysisRows + LocalHistory + LiveMessages + """

            AND v.id=$scope AND c.adapter=$adapter
            AND m.sent_at >= $from AND (m.sent_at < $sent OR (m.sent_at=$sent AND m.id < $anchor))
            ORDER BY m.sent_at DESC,m.id DESC LIMIT $limit;
            """, ("$adapter", anchor.Scope.Adapter), ("$scope", anchor.Scope.Id), ("$since", Format(since)),
            ("$from", Format(sent.AddMinutes(-20))), ("$sent", Format(sent)), ("$anchor", anchor.Message.Id),
            ("$limit", AiLimits.HybridNearbyCount));
        return await ReadWorkAsync(command, ct);
    }

    private static async Task<AiWorkItem?> ReadAuthorizedCoreAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AiScope scope, string messageId, DateTimeOffset? since, CancellationToken ct)
    {
        if (since.HasValue && scope.Adapter is not ("wxlocal" or "qqlocal")) return null;
        var moect = scope.Adapter == "moect-json" ? await ReadMoectScopeAsync(connection, transaction, ct) : null;
        var condition = since.HasValue ? LocalHistory + LiveMessages + " AND c.adapter=$adapter" : HistoryCondition(scope.Adapter);
        using var command = Command(connection, transaction, AnalysisRows + condition + " AND v.id=$scope AND m.id=$id;",
            ("$adapter", scope.Adapter), ("$moect", moect), ("$scope", scope.Id), ("$id", messageId),
            ("$since", since is { } activation ? Format(activation) : null));
        return (await ReadWorkAsync(command, ct)).SingleOrDefault();
    }

    public async Task MarkFilteredAsync(string messageId, CancellationToken ct, string? reason = null)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            INSERT INTO ai_analysis(message_id,status,error_code) VALUES($id,'filtered',$reason) ON CONFLICT DO NOTHING;
            """, ("$id", messageId), ("$reason", reason));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AiReservation> ReserveAsync(AiWorkItem item, AiProfile profile, DateTimeOffset? since,
        DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct, IReadOnlyList<AiWorkItem>? context = null)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var current = await ReadAuthorizedCoreAsync(connection, transaction, item.Scope, item.Message.Id, since, ct);
        if (current is null || current.Status is "empty" or "extracted" ||
            (since.HasValue && current.Status is ("filtered" or "failed"))) return new(null);
        if (context is not null && (!SameInput(item, current) ||
            !await ContextMatchesAsync(connection, transaction, item.Scope, context, since, ct))) return new(null);
        if (current.RetryAt > now) return new(null, AvailableAt: current.RetryAt);
        var reservation = await ReserveAiRequestAsync(connection, transaction, profile, now, zone, ct);
        if (reservation.RequestId is not { } requestId) return reservation;
        var attempt = await StartAttemptAsync(connection, transaction, item.Message.Id, requestId, now, ct);
        await transaction.CommitAsync(ct);
        return new(requestId, attempt);
    }

    public async Task<AiBatchReservation> ReserveBatchAsync(IReadOnlyList<AiBatchJob> jobs, AiProfile profile,
        DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        if (jobs.Count is < 1 or > AiLimits.MaxBatchJobs || jobs.Any(job => job.Anchor.Scope != jobs[0].Anchor.Scope) ||
            jobs.Select(job => job.Anchor.Message.Id).Distinct(StringComparer.Ordinal).Count() != jobs.Count)
            throw new ArgumentException("批次必须由同一会话的不同消息组成。");
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var eligible = new List<AiBatchJob>();
        foreach (var job in jobs)
        {
            var item = job.Anchor;
            var current = await ReadAuthorizedCoreAsync(connection, transaction, item.Scope, item.Message.Id, since, ct);
            if (current is null || current.Status is "empty" or "extracted" or "filtered" or "failed" ||
                current.RetryAt > now || !SameInput(item, current) ||
                !await ContextMatchesAsync(connection, transaction, item.Scope, job.Context, since, ct)) continue;
            eligible.Add(job);
        }
        if (eligible.Count == 0) return new(null, []);
        var reservation = await ReserveAiRequestAsync(connection, transaction, profile, now, zone, ct);
        if (reservation.RequestId is not { } requestId)
            return new(null, [], reservation.AvailableAt, reservation.DailyLimit);
        var attempts = new List<AiBatchAttempt>(eligible.Count);
        foreach (var job in eligible)
        {
            var attempt = await StartAttemptAsync(connection, transaction, job.Anchor.Message.Id, requestId, now, ct);
            attempts.Add(new(job, attempt));
        }
        await transaction.CommitAsync(ct);
        return new(requestId, attempts);
    }

    private static async Task<int> StartAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        string messageId, long requestId, DateTimeOffset now, CancellationToken ct)
    {
        using var state = Command(connection, transaction, """
            INSERT INTO ai_analysis(message_id,status,attempts,retry_at,error_code,last_request_id)
            VALUES($id,'retry',1,$retry,'interrupted',$request)
            ON CONFLICT(message_id) DO UPDATE SET
                attempts=CASE WHEN ai_analysis.status='failed' THEN 1 ELSE ai_analysis.attempts+1 END,
                status=CASE WHEN ai_analysis.status<>'failed' AND ai_analysis.attempts+1 >= $limit THEN 'failed' ELSE 'retry' END,
                retry_at=CASE WHEN ai_analysis.status<>'failed' AND ai_analysis.attempts+1 >= $limit THEN NULL ELSE excluded.retry_at END,
                error_code='interrupted', last_request_id=$request
            RETURNING attempts;
            """, ("$id", messageId), ("$retry", Format(now.AddMinutes(2))), ("$limit", AiLimits.MaxAttempts),
            ("$request", requestId));
        return Convert.ToInt32(await state.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<AiReservation> ReserveDigestRequestAsync(AiProfile profile, DateTimeOffset now, TimeZoneInfo zone,
        CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var reservation = await ReserveAiRequestAsync(connection, transaction, profile, now, zone, ct);
        await transaction.CommitAsync(ct);
        return reservation;
    }

    public async Task FinishDigestRequestAsync(long requestId, AiUsage? usage, DateTimeOffset? cooldownUntil,
        CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await SaveUsageAsync(connection, transaction, requestId, usage, ct);
        if (cooldownUntil is { } until)
            await SaveAiCooldownAsync(connection, transaction, requestId, until, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<AiReservation> ReserveAiRequestAsync(SqliteConnection connection, SqliteTransaction transaction,
        AiProfile profile, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        using var cooldown = Command(connection, transaction, "SELECT available_at FROM ai_provider_cooldowns WHERE provider=$provider;",
            ("$provider", profile.Provider));
        var available = ParseTime(await cooldown.ExecuteScalarAsync(ct) as string);
        if (available > now) return new(null, AvailableAt: available);
        var day = LocalDay(now, zone);
        using var count = Command(connection, transaction, "SELECT COUNT(*) FROM ai_requests WHERE local_day=$day;", ("$day", day));
        if ((long)(await count.ExecuteScalarAsync(ct))! >= AiLimits.DailyRequests)
            return new(null, AvailableAt: NextLocalDay(now, zone), DailyLimit: true);

        using var recent = Command(connection, transaction, "SELECT sent_at FROM ai_requests ORDER BY id DESC LIMIT 1;");
        var last = ParseTime(await recent.ExecuteScalarAsync(ct) as string);
        if (last + AiLimits.RequestInterval > now) return new(null, AvailableAt: last + AiLimits.RequestInterval);

        using var request = Command(connection, transaction, """
            INSERT INTO ai_requests(provider,model,sent_at,local_day) VALUES($provider,$model,$now,$day) RETURNING id;
            """, ("$provider", profile.Provider), ("$model", profile.Model), ("$now", Format(now)), ("$day", day));
        return new((long)(await request.ExecuteScalarAsync(ct))!);
    }

    public async Task RecordFailureAsync(string messageId, long requestId, AiFailureException failure,
        DateTimeOffset? retryAt, DateTimeOffset? providerAvailableAt, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await SaveUsageAsync(connection, transaction, requestId, failure.Usage, ct);
        if (providerAvailableAt is { } available)
            await SaveAiCooldownAsync(connection, transaction, requestId, available, ct);
        await SaveFailureAsync(connection, transaction, messageId, requestId, failure.Code, retryAt, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecordBatchFailureAsync(long requestId, IReadOnlyList<AiBatchFailure> failures,
        AiFailureException error, DateTimeOffset? providerAvailableAt, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await SaveUsageAsync(connection, transaction, requestId, error.Usage, ct);
        if (providerAvailableAt is { } available)
            await SaveAiCooldownAsync(connection, transaction, requestId, available, ct);
        foreach (var failed in failures)
            await SaveFailureAsync(connection, transaction, failed.Attempt.Job.Anchor.Message.Id, requestId, error.Code, failed.RetryAt, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task SaveFailureAsync(SqliteConnection connection, SqliteTransaction transaction, string messageId,
        long requestId, string code, DateTimeOffset? retryAt, CancellationToken ct)
    {
        using var command = Command(connection, transaction, """
            UPDATE ai_analysis SET status=$status,retry_at=$retry,error_code=$code
            WHERE message_id=$id AND last_request_id=$request AND status IN ('retry','failed');
            """, ("$status", retryAt.HasValue ? "retry" : "failed"), ("$retry", retryAt is { } time ? Format(time) : null),
            ("$code", code), ("$id", messageId), ("$request", requestId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(AiWorkItem item, long requestId, AiExtraction extraction, DateTimeOffset? since,
        CancellationToken ct, IReadOnlyList<AiWorkItem>? context = null)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await SaveUsageAsync(connection, transaction, requestId, extraction.Usage, ct);
        await CompleteJobAsync(connection, transaction, item, requestId, extraction.Events, since, ct, context);
        await transaction.CommitAsync(ct);
    }

    public async Task CompleteBatchAsync(AiBatchReservation reservation, AiBatchExtraction extraction,
        DateTimeOffset since, CancellationToken ct)
    {
        if (reservation.RequestId is not { } requestId || reservation.Attempts.Count != extraction.Results.Count)
            throw new ArgumentException("批次提取结果与请求数量不一致。");
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await SaveUsageAsync(connection, transaction, requestId, extraction.Usage, ct);
        for (var index = 0; index < reservation.Attempts.Count; index++)
        {
            var job = reservation.Attempts[index].Job;
            await CompleteJobAsync(connection, transaction, job.Anchor, requestId, extraction.Results[index], since, ct, job.Context);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task CompleteJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        AiWorkItem item, long requestId, IReadOnlyList<ExtractedEvent> events, DateTimeOffset? since, CancellationToken ct,
        IReadOnlyList<AiWorkItem>? context)
    {
        var current = await ReadAuthorizedCoreAsync(connection, transaction, item.Scope, item.Message.Id, since, ct);
        if (current is not null && current.Status is not ("empty" or "extracted") &&
            (context is null || (SameInput(item, current) &&
                await ContextMatchesAsync(connection, transaction, item.Scope, context, since, ct))))
        {
            using var command = Command(connection, transaction, """
                UPDATE ai_analysis SET status=$status,retry_at=NULL,error_code=NULL
                WHERE message_id=$id AND last_request_id=$request AND status IN ('retry','failed');
                """, ("$status", events.Count == 0 ? "empty" : "extracted"), ("$id", item.Message.Id), ("$request", requestId));
            if (await command.ExecuteNonQueryAsync(ct) == 1)
                foreach (var result in events)
                    await AddCandidateAsync(connection, transaction, current, result, ct, context ?? []);
        }
    }

    private static async Task<bool> ContextMatchesAsync(SqliteConnection connection, SqliteTransaction transaction,
        AiScope scope, IReadOnlyList<AiWorkItem> context, DateTimeOffset? since, CancellationToken ct)
    {
        foreach (var snapshot in context)
        {
            if (snapshot.Scope != scope ||
                await ReadAuthorizedCoreAsync(connection, transaction, scope, snapshot.Message.Id, since, ct) is not { } current ||
                !SameInput(snapshot, current)) return false;
        }
        return true;
    }

    private static bool SameInput(AiWorkItem snapshot, AiWorkItem current)
    {
        var old = snapshot.Message.Message;
        var live = current.Message.Message;
        return old.Text == live.Text && old.SentAt == live.SentAt && old.SenderName == live.SenderName && old.SenderId == live.SenderId;
    }

    public async Task<AiStatistics> ReadAiStatisticsAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            SELECT COUNT(*),COALESCE(SUM(prompt_tokens),0),COALESCE(SUM(completion_tokens),0),
                   COUNT(*)-COUNT(prompt_tokens),
                   (SELECT COUNT(*) FROM ai_analysis WHERE status='filtered'),
                   (SELECT COUNT(*) FROM ai_analysis WHERE status='retry'),
                   (SELECT COUNT(*) FROM ai_analysis WHERE status='failed')
            FROM ai_requests WHERE local_day=$day;
            """, ("$day", LocalDay(now, zone)));
        using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
    }

    private static async Task SaveUsageAsync(SqliteConnection connection, SqliteTransaction transaction,
        long requestId, AiUsage? usage, CancellationToken ct)
    {
        if (usage is null) return;
        using var command = Command(connection, transaction,
            "UPDATE ai_requests SET prompt_tokens=$prompt,completion_tokens=$completion WHERE id=$id;",
            ("$prompt", usage.PromptTokens), ("$completion", usage.CompletionTokens), ("$id", requestId));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SaveAiCooldownAsync(SqliteConnection connection, SqliteTransaction transaction,
        long requestId, DateTimeOffset available, CancellationToken ct)
    {
        using var cooldown = Command(connection, transaction, """
            INSERT INTO ai_provider_cooldowns(provider,available_at)
            SELECT provider,$available FROM ai_requests WHERE id=$request
            ON CONFLICT(provider) DO UPDATE SET available_at=MAX(available_at,excluded.available_at);
            """, ("$available", Format(available)), ("$request", requestId));
        await cooldown.ExecuteNonQueryAsync(ct);
    }

    private static string HistoryCondition(string adapter) => adapter switch
    {
        "wxlocal" or "qqlocal" => LocalHistory, "moect-json" => MoectHistory, "manual" => " AND s.type='manual'", _ => " AND 0=1"
    };

    private static async Task<string?> ReadMoectScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        using var command = Command(connection, transaction, "SELECT value_json FROM app_settings WHERE key='moect-watch';");
        var profile = await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<MoectWatchProfile>(json) : null;
        return profile?.Group is not null ? MoectConversationKey(profile) : null;
    }

    private static async Task<IReadOnlyList<AiWorkItem>> ReadWorkAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiWorkItem>();
        while (await reader.ReadAsync(ct))
        {
            var message = new StoredMessage(reader.GetString(6), ReadMessagePayload(reader.GetString(7)), reader.GetString(8));
            result.Add(new(ReadScope(reader), message, reader.GetString(9), ParseTime(OptionalString(reader, 10)), OptionalString(reader, 11)));
        }
        return result;
    }

    private static AiScope ReadScope(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    private static NormalizedMessage ReadMessagePayload(string json) => JsonSerializer.Deserialize<NormalizedMessage>(json, ContractJson.Options)!;
    private static string? OptionalString(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    private static DateTimeOffset? ParseTime(string? text) => text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static string LocalDay(DateTimeOffset now, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset NextLocalDay(DateTimeOffset now, TimeZoneInfo zone)
    {
        var midnight = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}
