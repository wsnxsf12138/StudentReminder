using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository
{
    private const string EventRows = """
        SELECT e.id,r.payload_json,e.status,e.version,e.updated_at
        FROM events e JOIN event_versions r ON r.event_id=e.id AND r.version=e.version
        """;

    private static async Task AddCandidateAsync(SqliteConnection connection, SqliteTransaction transaction, AiWorkItem item,
        ExtractedEvent result, CancellationToken ct, IReadOnlyList<AiWorkItem> context)
    {
        var linked = context.Where(row => result.Candidate.Evidence.Contains(row.Evidence)).Prepend(item).ToArray();
        var draft = ValidateDraft(result.Candidate) with { Evidence = linked.Select(row => row.Evidence).ToArray(), NeedsConfirmation = true };
        if (result.ChangeKind is not ("new" or "update" or "cancel")) throw new ArgumentException("无效的事项变更类型。");
        string[] fingerprintParts = [result.ChangeKind, draft.Category, NormalizeText(draft.Title),
            NormalizeText(draft.Action), NormalizeText(draft.Location ?? ""), draft.StartsAt is { } start ? Format(start) : "",
            draft.DeadlineAt is { } deadline ? Format(deadline) : "",
            draft.StartsAt is null ? draft.StartText ?? "" : "", draft.DeadlineAt is null ? draft.DeadlineText ?? "" : ""];
        if (draft.StartsOn.HasValue || draft.DeadlineOn.HasValue)
            fingerprintParts = [..fingerprintParts, FormattableString.Invariant($"{draft.StartsOn:yyyy-MM-dd}"),
                FormattableString.Invariant($"{draft.DeadlineOn:yyyy-MM-dd}")];
        var fingerprint = SourceKeys.Hash(fingerprintParts);
        string? candidateId = null, eventId = null;
        using (var find = Command(connection, transaction, """
            SELECT c.id,c.event_id FROM event_candidates c
            JOIN candidate_evidence ce ON ce.candidate_id=c.id JOIN messages previous ON previous.id=ce.message_id
            JOIN messages incoming ON incoming.id=$message
            WHERE c.fingerprint=$key AND (previous.conversation_id=incoming.conversation_id OR previous.text=incoming.text)
            ORDER BY CASE c.review_status WHEN 'pending' THEN 1 ELSE 0 END,c.created_at,c.id LIMIT 1;
            """, ("$key", fingerprint), ("$message", item.Message.Id)))
        using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                candidateId = reader.GetString(0);
                eventId = OptionalString(reader, 1);
            }
        }
        if (candidateId is null)
        {
            candidateId = Guid.NewGuid().ToString("N");
            using var insert = Command(connection, transaction, """
                INSERT INTO event_candidates(id,payload_json,confidence,created_at,fingerprint,change_kind)
                VALUES($id,$payload,$confidence,$created,$key,$change);
                """, ("$id", candidateId), ("$payload", WriteDraft(draft)), ("$confidence", draft.Certainty),
                ("$created", Format(DateTimeOffset.UtcNow)), ("$key", fingerprint), ("$change", result.ChangeKind));
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach (var row in linked)
        {
            using var link = Command(connection, transaction, """
                INSERT INTO candidate_evidence(candidate_id,message_id) VALUES($candidate,$message) ON CONFLICT DO NOTHING;
                """, ("$candidate", candidateId), ("$message", row.Message.Id));
            await link.ExecuteNonQueryAsync(ct);
        }
        if (eventId is not null) await CopyCandidateEvidenceAsync(connection, transaction, candidateId, eventId, result.ChangeKind, ct);
    }

    public async Task<IReadOnlyList<ReviewCandidate>> ReadCandidatesAsync(bool includeReviewed, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            SELECT id,payload_json,review_status,change_kind,event_id,created_at FROM event_candidates
            WHERE review_status <> 'deleted' AND ($all=1 OR review_status='pending')
            ORDER BY created_at DESC,id;
            """, ("$all", includeReviewed ? 1 : 0));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ReviewCandidate>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadCandidate(reader));
        return rows;
    }

    public async Task<IReadOnlyList<ReviewedEvent>> ReadEventsAsync(CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, EventRows + """

            WHERE e.status <> 'deleted' ORDER BY e.updated_at DESC,e.id;
            """);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ReviewedEvent>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadEvent(reader));
        return rows;
    }

    public async Task<IReadOnlyList<EventRevision>> ReadEventVersionsAsync(string eventId, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            SELECT version,payload_json,status,changed_at FROM event_versions WHERE event_id=$id ORDER BY version DESC;
            """, ("$id", eventId));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<EventRevision>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetInt32(0), ReadDraft(reader.GetString(1)), reader.GetString(2), ParseTime(reader.GetString(3))!.Value));
        return rows;
    }

    public async Task<IReadOnlyList<StoredMessage>> ReadReviewEvidenceAsync(string id, bool forEvent, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        return await ReadEvidenceCoreAsync(connection, null, id, forEvent, ct);
    }

    private static async Task<IReadOnlyList<StoredMessage>> ReadEvidenceCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string id, bool forEvent, CancellationToken ct)
    {
        var relation = forEvent ? "event_evidence e ON e.message_id=m.id WHERE e.event_id=$id"
            : "candidate_evidence e ON e.message_id=m.id WHERE e.candidate_id=$id";
        using var command = Command(connection, transaction,
            "SELECT m.id,m.payload_json,m.status FROM messages m JOIN " + relation + " ORDER BY m.sent_at,m.id;", ("$id", id));
        using var reader = await command.ExecuteReaderAsync(ct);
        var evidence = new List<StoredMessage>();
        while (await reader.ReadAsync(ct)) evidence.Add(new(reader.GetString(0), ReadMessagePayload(reader.GetString(1)), reader.GetString(2)));
        return evidence;
    }

    public async Task<string> ConfirmCandidateAsync(string candidateId, EventCandidate edits, string? targetEventId,
        int? expectedVersion, CancellationToken ct = default)
    {
        var draft = ValidateDraft(edits);
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var candidate = await ReadPendingCandidateAsync(connection, transaction, candidateId, ct);
        var evidence = await ReadEvidenceCoreAsync(connection, transaction, candidateId, forEvent: false, ct);
        if (!evidence.Any(message => message.Status == "active"))
            throw new InvalidOperationException("证据均已撤回或存在冲突，无法确认此候选。");
        var target = targetEventId is null ? null : await ReadCurrentEventAsync(connection, transaction, targetEventId, expectedVersion, ct);
        var eventId = target?.Id ?? Guid.NewGuid().ToString("N");
        var status = candidate.ChangeKind == "cancel" ? "canceled" : target?.Status ?? "active";
        if (target is not null && (candidate.ChangeKind == "cancel" || candidate.ChangeKind == "new"))
            draft = target.Details;
        if (target is null) await CreateEventAsync(connection, transaction, eventId, draft, status, ct);
        await CopyCandidateEvidenceAsync(connection, transaction, candidateId, eventId, candidate.ChangeKind, ct);
        await WriteRevisionAsync(connection, transaction, eventId, (target?.Version ?? 0) + 1, draft, status, candidateId, ct);
        using var confirm = Command(connection, transaction, """
            UPDATE event_candidates SET review_status='confirmed',event_id=$event WHERE id=$id;
            """, ("$id", candidateId), ("$event", eventId));
        await confirm.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return eventId;
    }

    public async Task SetCandidateDispositionAsync(string id, bool delete, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            UPDATE event_candidates SET review_status=$status
            WHERE id=$id AND (review_status='pending' OR ($status='deleted' AND review_status='ignored'));
            """, ("$status", delete ? "deleted" : "ignored"), ("$id", id));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("候选状态已变化，请刷新后重试。");
    }

    public async Task UpdateEventAsync(string id, int expectedVersion, string operation, EventCandidate? edits = null,
        CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var current = await ReadCurrentEventAsync(connection, transaction, id, expectedVersion, ct);
        var status = operation switch
        {
            "edit" when current.Status is "active" or "completed" => current.Status,
            "complete" when current.Status == "active" => "completed",
            "delete" => "deleted",
            _ => throw new ArgumentException("当前事项状态不支持此操作。")
        };
        var draft = operation == "edit" ? ValidateDraft(edits ?? throw new ArgumentNullException(nameof(edits))) : current.Details;
        await WriteRevisionAsync(connection, transaction, id, current.Version + 1, draft, status, null, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<ReviewCandidate> ReadPendingCandidateAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, CancellationToken ct)
    {
        using var command = Command(connection, transaction, """
            SELECT id,payload_json,review_status,change_kind,event_id,created_at FROM event_candidates
            WHERE id=$id AND review_status='pending';
            """, ("$id", id));
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("候选状态已变化，请刷新后重试。");
        return ReadCandidate(reader);
    }

    private static async Task<ReviewedEvent> ReadCurrentEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, int? expectedVersion, CancellationToken ct)
    {
        using var command = Command(connection, transaction, EventRows + " WHERE e.id=$id AND e.status<>'deleted';", ("$id", id));
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(3) != expectedVersion)
            throw new InvalidOperationException("关联事项已变化，请刷新并重新核对版本。");
        return ReadEvent(reader);
    }

    private static async Task CreateEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, EventCandidate draft, string status, CancellationToken ct)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO events(id,title,status,updated_at) VALUES($id,$title,$status,$now);
            """, ("$id", id), ("$title", draft.Title), ("$status", status), ("$now", Format(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopyCandidateEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string candidateId, string eventId, string relation, CancellationToken ct)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO event_evidence(event_id,message_id,relation)
            SELECT $event,message_id,$relation FROM candidate_evidence WHERE candidate_id=$candidate
            ON CONFLICT DO NOTHING;
            """, ("$event", eventId), ("$candidate", candidateId), ("$relation", relation));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, string id,
        int version, EventCandidate draft, string status, string? candidateId, CancellationToken ct)
    {
        var sources = await ReadEvidenceCoreAsync(connection, transaction, id, forEvent: true, ct);
        var snapshot = draft with
        {
            NeedsConfirmation = false,
            Evidence = sources.Select(source => new MessageEvidence(source.Source, source.Message.ConversationId,
                source.Message.MessageId ?? source.Id) { AccountId = source.Account }).ToArray()
        };
        var instant = clock.GetUtcNow();
        var now = Format(instant);
        using var revision = Command(connection, transaction, """
            INSERT INTO event_versions(event_id,version,payload_json,status,candidate_id,changed_at)
            VALUES($id,$version,$payload,$status,$candidate,$now);
            UPDATE events SET title=$title,starts_at=$starts,deadline_at=$deadline,status=$status,version=$version,
                updated_at=$now WHERE id=$id;
            """, ("$id", id), ("$version", version), ("$payload", WriteDraft(snapshot)), ("$status", status),
            ("$candidate", candidateId), ("$now", now), ("$title", draft.Title),
            ("$starts", draft.StartsAt is { } start ? Format(start) : null),
            ("$deadline", draft.DeadlineAt is { } deadline ? Format(deadline) : null));
        await revision.ExecuteNonQueryAsync(ct);
        var preferences = await ReadReminderPreferencesAsync(connection, transaction, ct);
        await ReconcileRemindersAsync(connection, transaction, id, snapshot, status, preferences, instant, ct);
    }

    private static EventCandidate ValidateDraft(EventCandidate draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 120 || !ReviewLabels.IsCategory(draft.Category) ||
            draft.Action.Length > 240 || draft.Location?.Length > 160 || draft.StartsAt > draft.DeadlineAt ||
            draft.StartsOn > draft.DeadlineOn || (draft.StartsAt.HasValue && draft.StartsOn.HasValue) ||
            (draft.DeadlineAt.HasValue && draft.DeadlineOn.HasValue))
            throw new ArgumentException("请检查事项标题、类别、文本长度和开始／截止时间。");
        return draft with
        {
            Title = draft.Title.Trim(), Action = draft.Action.Trim(), Location = draft.Location?.Trim(),
            StartsAt = draft.StartsAt?.ToUniversalTime(), DeadlineAt = draft.DeadlineAt?.ToUniversalTime()
        };
    }

    private static string WriteDraft(EventCandidate draft) => JsonSerializer.Serialize(draft, ContractJson.Options);
    private static EventCandidate ReadDraft(string json) => JsonSerializer.Deserialize<EventCandidate>(json, ContractJson.Options)!;
    private static ReviewCandidate ReadCandidate(SqliteDataReader reader) => new(reader.GetString(0), ReadDraft(reader.GetString(1)),
        reader.GetString(2), reader.GetString(3), OptionalString(reader, 4), ParseTime(reader.GetString(5))!.Value);
    private static ReviewedEvent ReadEvent(SqliteDataReader reader) => new(reader.GetString(0), ReadDraft(reader.GetString(1)),
        reader.GetString(2), reader.GetInt32(3), ParseTime(reader.GetString(4))!.Value);
}
