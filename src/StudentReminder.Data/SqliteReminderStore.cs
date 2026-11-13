using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository : IReminderStore
{
    private readonly SemaphoreSlim digestWrites = new(1);
    private const string ReminderSelect = """
        SELECT r.id,r.event_id,e.title,r.reminder_rule,r.fire_at,r.status,r.last_error
        FROM reminders r JOIN events e ON e.id=r.event_id
        """;

    public async Task<ReminderPreferences> ReadReminderPreferencesAsync(CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        return await ReadReminderPreferencesAsync(connection, null, ct);
    }

    private static async Task<ReminderPreferences> ReadReminderPreferencesAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken ct)
    {
        using var read = Command(connection, transaction, "SELECT value_json FROM app_settings WHERE key='reminders';");
        return await read.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<ReminderPreferences>(json, ContractJson.Options) ?? new()
            : new();
    }

    public async Task SaveReminderPreferencesAsync(ReminderPreferences preferences, CancellationToken ct = default)
    {
        preferences.Validate();
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var previous = await ReadReminderPreferencesAsync(connection, transaction, ct);
        using var save = Command(connection, transaction, """
            INSERT INTO app_settings(key,value_json) VALUES('reminders',$json)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;
            """, ("$json", JsonSerializer.Serialize(preferences, ContractJson.Options)));
        await save.ExecuteNonQueryAsync(ct);
        if (!preferences.BeforeStartMinutes.SequenceEqual(previous.BeforeStartMinutes) ||
            !preferences.BeforeDeadlineMinutes.SequenceEqual(previous.BeforeDeadlineMinutes))
            await ReconcileAllRemindersAsync(connection, transaction, preferences, clock.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task ReconcileAllRemindersAsync(SqliteConnection connection, SqliteTransaction transaction,
        ReminderPreferences preferences, DateTimeOffset now, CancellationToken ct)
    {
        var items = new List<ReviewedEvent>();
        using (var command = Command(connection, transaction, EventRows + " WHERE e.status='active';"))
        using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(ReadEvent(reader));
        foreach (var item in items)
            await ReconcileRemindersAsync(connection, transaction, item.Id, item.Details, item.Status, preferences, now, ct);
    }

    private static async Task ReconcileRemindersAsync(SqliteConnection connection, SqliteTransaction transaction,
        string eventId, EventCandidate item, string status, ReminderPreferences preferences, DateTimeOffset now, CancellationToken ct)
    {
        var desired = ReminderRules.Plan(item, status, preferences).ToDictionary(
            row => SourceKeys.Hash(eventId, row.Rule, Format(row.FireAt)), StringComparer.Ordinal);
        var obsolete = new List<string>();
        using (var read = Command(connection, transaction,
            "SELECT id FROM reminders WHERE event_id=$event AND status IN ('pending','claimed');", ("$event", eventId)))
        using (var reader = await read.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!desired.Remove(id)) obsolete.Add(id);
            }
        foreach (var id in obsolete)
        {
            using var cancel = Command(connection, transaction,
                "UPDATE reminders SET status='canceled' WHERE id=$id;", ("$id", id));
            await cancel.ExecuteNonQueryAsync(ct);
        }
        foreach (var (id, row) in desired)
        {
            if (row.FireAt < now) continue;
            using var plan = Command(connection, transaction, """
                INSERT INTO reminders(id,event_id,reminder_rule,fire_at,status) VALUES($id,$event,$rule,$fire,'pending')
                ON CONFLICT(event_id,reminder_rule,fire_at) DO UPDATE SET status='pending'
                WHERE reminders.status='canceled';
                """, ("$id", id), ("$event", eventId), ("$rule", row.Rule), ("$fire", Format(row.FireAt)));
            await plan.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<ReminderBatch> ClaimRemindersAsync(DateTimeOffset now, DateTimeOffset missedBefore,
        bool recoverClaims, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        if (recoverClaims)
        {
            using var interrupted = Command(connection, transaction,
                "UPDATE reminders SET status='uncertain',last_error='interrupted' WHERE status='claimed';");
            await interrupted.ExecuteNonQueryAsync(ct);
        }
        using (var missed = Command(connection, transaction,
            "UPDATE reminders SET status='missed' WHERE status='pending' AND fire_at < $before;",
            ("$before", Format(missedBefore))))
            await missed.ExecuteNonQueryAsync(ct);

        using var report = Command(connection, transaction, """
            UPDATE reminders SET reported_at=$now WHERE status IN ('missed','uncertain') AND reported_at IS NULL;
            """, ("$now", Format(now)));
        var newlyMissed = await report.ExecuteNonQueryAsync(ct);
        var due = await ReadReminderRowsAsync(connection, transaction,
            " WHERE r.status='pending' AND r.fire_at <= $now ORDER BY r.fire_at,r.id;", ct, ("$now", Format(now)));
        using var claim = Command(connection, transaction,
            "UPDATE reminders SET status='claimed' WHERE status='pending' AND fire_at <= $now;", ("$now", Format(now)));
        await claim.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(due, newlyMissed);
    }

    public async Task FinishRemindersAsync(IReadOnlyList<ReminderRecord> reminders, bool submitted, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in reminders)
        {
            using var update = Command(connection, transaction, """
                UPDATE reminders SET status=$status,last_error=$error WHERE id=$id AND status='claimed';
                """, ("$id", row.Id), ("$status", submitted ? "submitted" : "failed"),
                ("$error", submitted ? null : "notification"));
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ReminderRecord>> ReadClaimedRemindersAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        return await ReadReminderRowsAsync(connection, null, " WHERE r.status='claimed' ORDER BY r.fire_at,r.id;", ct);
    }

    public async Task<IReadOnlyList<ReminderRecord>> ReadMissedRemindersAsync(CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        return await ReadReminderRowsAsync(connection, null,
            " WHERE r.status IN ('missed','uncertain','failed') ORDER BY r.fire_at DESC,r.id;", ct);
    }

    private static async Task<IReadOnlyList<ReminderRecord>> ReadReminderRowsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string filter, CancellationToken ct, params (string, object?)[] parameters)
    {
        using var read = Command(connection, transaction, ReminderSelect + filter, parameters);
        using var reader = await read.ExecuteReaderAsync(ct);
        var reminders = new List<ReminderRecord>();
        while (await reader.ReadAsync(ct))
            reminders.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseTime(reader.GetString(4))!.Value, reader.GetString(5), OptionalString(reader, 6)));
        return reminders;
    }

    public async Task<DailyDigest?> ReadDailyDigestAsync(DateOnly day, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, "SELECT content_json FROM digests WHERE digest_date=$day;",
            ("$day", FormatDay(day)));
        return await command.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<DailyDigest>(json, ContractJson.Options)
            : null;
    }

    public async Task<DailyDigest?> GenerateDailyDigestAsync(DateTimeOffset now, TimeZoneInfo zone, bool scheduled,
        CancellationToken ct = default)
    {
        await digestWrites.WaitAsync(ct);
        try { return await GenerateDailyDigestCoreAsync(now, zone, scheduled, ct); }
        finally { digestWrites.Release(); }
    }

    private async Task<DailyDigest?> GenerateDailyDigestCoreAsync(DateTimeOffset now, TimeZoneInfo zone, bool scheduled,
        CancellationToken ct)
    {
        var day = DigestCalendar.Day(now, zone);
        var date = FormatDay(day);
        if (scheduled)
        {
            using var connection = await OpenAsync(ct);
            using var read = Command(connection, null, "SELECT scheduled_at FROM digests WHERE digest_date=$day;", ("$day", date));
            if (await read.ExecuteScalarAsync(ct) is string) return null;
            var preferences = await ReadReminderPreferencesAsync(ct);
            if (now < DigestCalendar.At(day, preferences.DigestTime, zone)) return null;
        }

        var events = await ReadDigestEventsAsync(ct);
        var candidates = await ReadCandidatesAsync(includeReviewed: false, ct);
        var sources = await ReadSourcesAsync(ct);
        var digest = DigestCalendar.Build(now, zone, events, candidates, sources);
        using var writeConnection = await OpenAsync(ct);
        using var write = Command(writeConnection, null, """
            INSERT INTO digests(id,digest_date,content_json,generated_at,scheduled_at)
            VALUES($day,$day,$json,$now,$scheduled)
            ON CONFLICT(digest_date) DO UPDATE SET content_json=excluded.content_json,generated_at=excluded.generated_at,
                scheduled_at=COALESCE(digests.scheduled_at,excluded.scheduled_at)
            WHERE $scheduled IS NULL OR digests.scheduled_at IS NULL;
            """, ("$day", date), ("$json", JsonSerializer.Serialize(digest, ContractJson.Options)),
            ("$now", Format(now)), ("$scheduled", scheduled ? Format(now) : null));
        if (await write.ExecuteNonQueryAsync(ct) != 0) return digest;
        return scheduled ? null : await ReadDailyDigestAsync(day, ct);
    }

    private async Task<IReadOnlyList<DigestEvent>> ReadDigestEventsAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = Command(connection, null, """
            SELECT e.id,r.payload_json,e.status,e.version,e.updated_at,first.changed_at
            FROM events e JOIN event_versions r ON r.event_id=e.id AND r.version=e.version
            JOIN event_versions first ON first.event_id=e.id AND first.version=1 WHERE e.status<>'deleted';
            """);
        using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<DigestEvent>();
        while (await reader.ReadAsync(ct)) events.Add(new(ReadEvent(reader), ParseTime(reader.GetString(5))!.Value));
        return events;
    }

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
