using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository : IMessageRepository, ISyncStore
{
    private const int SchemaVersion = 8;
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly TimeProvider clock;

    public SqliteMessageRepository(string databasePath, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath, ForeignKeys = true, DefaultTimeout = 10, Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var versionCommand = Command(connection, transaction, "PRAGMA user_version;");
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException("数据库版本较新，请使用匹配的应用版本。");
        for (var nextVersion = version + 1; nextVersion <= SchemaVersion; nextVersion++)
        {
            var file = nextVersion switch
            {
                1 => "001_initial.sql", 2 => "002_source_sync.sql", 3 => "003_rust_source.sql",
                4 => "004_moect_analysis.sql", 5 => "005_event_review.sql", 6 => "006_qq_source.sql",
                7 => "007_reminders_and_digests.sql", 8 => "008_hybrid_analysis.sql",
                _ => throw new InvalidOperationException("缺少数据库迁移。")
            };
            using var stream = typeof(SqliteMessageRepository).Assembly.GetManifestResourceStream(
                $"StudentReminder.Data.Migrations.{file}")!;
            using var reader = new StreamReader(stream);
            using var migration = Command(connection, transaction, await reader.ReadToEndAsync(cancellationToken));
            await migration.ExecuteNonQueryAsync(cancellationToken);
            if (nextVersion == 7)
                await ReconcileAllRemindersAsync(connection, transaction, new ReminderPreferences(), clock.GetUtcNow(), cancellationToken);
            using var updateVersion = Command(connection, transaction, $"PRAGMA user_version = {nextVersion};");
            await updateVersion.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ImportResult> SaveBatchAsync(IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count > MessageValidation.MaxMessages) throw new ArgumentException("每批最多 10000 条消息。");
        var normalized = messages.Select(MessageValidation.Normalize).ToArray();
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var result = await SaveBatchCoreAsync(connection, transaction, normalized, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<ImportResult> SaveBatchCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<NormalizedMessage> normalized, CancellationToken cancellationToken,
        ICollection<string>? inserted = null, bool isLocalReaderSync = false)
    {
        int added = 0, duplicates = 0, updated = 0, conflicts = 0;
        foreach (var message in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = Hash(message.Source, message.AccountId);
            var conversationId = Hash(sourceId, message.ConversationId);
            var contentHash = Hash(message.SenderId, Format(message.SentAt), message.ContentType, NormalizeText(message.Text));
            var dedupKey = message.MessageId is { } externalId
                ? Hash(sourceId, conversationId, "external", externalId)
                : Hash(sourceId, conversationId, "fingerprint", contentHash);
            using var source = Command(connection, transaction,
                "INSERT INTO sources(id,type,account_alias) VALUES($id,$type,$account) ON CONFLICT DO NOTHING;",
                ("$id", sourceId), ("$type", message.Source), ("$account", message.AccountId));
            await source.ExecuteNonQueryAsync(cancellationToken);
            using var conversation = Command(connection, transaction, """
                INSERT INTO conversations(id,source_id,external_id,name,type,last_seen_at)
                VALUES($id,$source,$external,$name,$type,$time)
                ON CONFLICT(id) DO UPDATE SET last_seen_at = MAX(last_seen_at, excluded.last_seen_at);
                """, ("$id", conversationId), ("$source", sourceId), ("$external", message.ConversationId),
                ("$name", message.ConversationName), ("$type", message.ConversationType), ("$time", Format(message.ReceivedAt)));
            await conversation.ExecuteNonQueryAsync(cancellationToken);
            using var insert = Command(connection, transaction, """
                INSERT INTO messages(id,source_id,conversation_id,external_id,dedup_key,sent_at,received_at,text,content_hash,payload_json,status)
                VALUES($id,$source,$conversation,$external,$key,$sent,$received,$text,$hash,$payload,$status)
                ON CONFLICT(dedup_key) DO NOTHING;
                """, ("$id", dedupKey), ("$source", sourceId), ("$conversation", conversationId),
                ("$external", message.MessageId), ("$key", dedupKey), ("$sent", Format(message.SentAt)),
                ("$received", Format(message.ReceivedAt)), ("$text", message.Text), ("$hash", contentHash),
                ("$payload", JsonSerializer.Serialize(message, ContractJson.Options)),
                ("$status", message.IsRecalled ? "recalled" : "active"));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                added++;
                inserted?.Add(dedupKey);
                if (isLocalReaderSync && !message.IsRecalled)
                {
                    using var timestamp = Command(connection, transaction,
                        "UPDATE messages SET wxlocal_ingested_at=$time WHERE id=$id;",
                        ("$id", dedupKey), ("$time", Format(DateTimeOffset.UtcNow)));
                    await timestamp.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                switch (await HandleDuplicateAsync(connection, transaction, dedupKey, message, cancellationToken))
                {
                    case DuplicateKind.Updated: updated++; break;
                    case DuplicateKind.Conflict: conflicts++; break;
                    default: duplicates++; break;
                }
            }
        }
        return new ImportResult(added, duplicates, updated, conflicts);
    }

    private enum DuplicateKind { Duplicate, Updated, Conflict }

    private static async Task<DuplicateKind> HandleDuplicateAsync(SqliteConnection connection,
        SqliteTransaction transaction, string id, NormalizedMessage incoming, CancellationToken cancellationToken)
    {
        NormalizedMessage original;
        string oldStatus;
        using (var query = Command(connection, transaction, "SELECT payload_json,status FROM messages WHERE id=$id;", ("$id", id)))
        using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("重复消息记录缺失。");
            original = JsonSerializer.Deserialize<NormalizedMessage>(reader.GetString(0), ContractJson.Options)!;
            oldStatus = reader.GetString(1);
        }
        var sameEvidence = original.SenderId == incoming.SenderId && original.SentAt == incoming.SentAt
            && original.ContentType == incoming.ContentType && NormalizeText(original.Text) == NormalizeText(incoming.Text);
        var recalled = original.IsRecalled || incoming.IsRecalled;
        if (!sameEvidence)
        {
            var variantHash = Hash(incoming.SenderId, Format(incoming.SentAt), incoming.ContentType,
                NormalizeText(incoming.Text), incoming.IsRecalled.ToString());
            using var conflict = Command(connection, transaction, """
                INSERT INTO message_conflicts(message_id,payload_hash,payload_json,detected_at)
                VALUES($id,$hash,$payload,$time) ON CONFLICT DO NOTHING;
                """, ("$id", id), ("$hash", variantHash),
                ("$payload", JsonSerializer.Serialize(incoming, ContractJson.Options)), ("$time", Format(DateTimeOffset.UtcNow)));
            await conflict.ExecuteNonQueryAsync(cancellationToken);
        }
        if (sameEvidence && recalled == original.IsRecalled) return DuplicateKind.Duplicate;
        var preserved = original with
        {
            IsRecalled = recalled,
            OriginEvent = recalled ? "message.revoke" : original.OriginEvent
        };
        var status = !sameEvidence || oldStatus == "conflict" ? "conflict" : recalled ? "recalled" : "active";
        using var update = Command(connection, transaction,
            "UPDATE messages SET status=$status,payload_json=$payload WHERE id=$id;",
            ("$id", id), ("$status", status), ("$payload", JsonSerializer.Serialize(preserved, ContractJson.Options)));
        await update.ExecuteNonQueryAsync(cancellationToken);
        return sameEvidence ? DuplicateKind.Updated : DuplicateKind.Conflict;
    }

    public async Task<IReadOnlyList<StoredMessage>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null,
            "SELECT id,payload_json,status FROM messages ORDER BY sent_at DESC,id LIMIT $limit;", ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredMessage>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new StoredMessage(reader.GetString(0),
                JsonSerializer.Deserialize<NormalizedMessage>(reader.GetString(1), ContractJson.Options)!, reader.GetString(2)));
        return result;
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, "SELECT COUNT(*) FROM messages;");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static string Format(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").Normalize(NormalizationForm.FormC);
    private static string Hash(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
}
