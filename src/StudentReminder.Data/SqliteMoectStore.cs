using System.Globalization;
using System.Text.Json;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository
{
    public async Task<MoectWatchProfile> ReadMoectProfileAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, "SELECT value_json FROM app_settings WHERE key='moect-watch';");
        return await command.ExecuteScalarAsync(cancellationToken) is string value
            ? JsonSerializer.Deserialize<MoectWatchProfile>(value) ?? new() : new();
    }

    public async Task SaveMoectProfileAsync(MoectWatchProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, """
            INSERT INTO app_settings(key,value_json) VALUES('moect-watch',$json)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;
            """, ("$json", JsonSerializer.Serialize(profile)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ImportResult?> ImportMoectExportAsync(MoectWatchProfile profile, string digest,
        IReadOnlyList<NormalizedMessage> messages, CancellationToken cancellationToken = default)
    {
        var group = profile.Group ?? throw new ArgumentException("缺少授权群。");
        if (messages.Count > MessageValidation.MaxMessages || messages.Any(message =>
            message is not { Source: "wechat", Adapter: "moect-json", ConversationType: "group", ContentType: "text" } ||
            message.AccountId != profile.AccountAlias || message.ConversationId != group.Id))
            throw new ArgumentException("批次包含未授权会话或超过消息上限。");

        var target = SourceKeys.Hash(profile.AccountAlias, group.Id);
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var batch = Command(connection, transaction, """
            INSERT INTO moect_imported_exports(target_key,digest) VALUES($target,$digest) ON CONFLICT DO NOTHING;
            """, ("$target", target), ("$digest", digest));
        if (await batch.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        var inserted = new List<string>();
        var result = await SaveBatchCoreAsync(connection, transaction, messages.Select(MessageValidation.Normalize).ToArray(),
            cancellationToken, inserted);
        foreach (var id in inserted)
        {
            using var queue = Command(connection, transaction, "INSERT INTO moect_analysis(message_id) VALUES($id);", ("$id", id));
            await queue.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<MoectAnalysisInput>> ReadPendingMoectAsync(MoectWatchProfile profile, int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, """
            SELECT m.id,m.text,m.sent_at FROM moect_analysis a JOIN messages m ON a.message_id=m.id
            WHERE m.conversation_id=$group AND m.status='active' AND a.result_json IS NULL
            ORDER BY a.last_attempt_at,m.sent_at,m.id LIMIT $limit;
            """, ("$group", MoectConversationKey(profile)), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MoectAnalysisInput>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task MarkMoectAttemptAsync(string messageId, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null,
            "UPDATE moect_analysis SET last_attempt_at=$time WHERE message_id=$id AND result_json IS NULL;",
            ("$id", messageId), ("$time", Format(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveMoectResultAsync(string messageId, MoectAnalysisResult result, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, """
            UPDATE moect_analysis SET result_json=$json,analyzed_at=$time WHERE message_id=$id AND result_json IS NULL
            AND EXISTS (SELECT 1 FROM messages WHERE id=$id AND status='active');
            """, ("$id", messageId), ("$json", JsonSerializer.Serialize(result)), ("$time", Format(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MoectAnalysisEntry>> ReadMoectResultsAsync(MoectWatchProfile profile,
        CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, """
            SELECT m.sent_at,a.result_json FROM moect_analysis a JOIN messages m ON a.message_id=m.id
            WHERE m.conversation_id=$group AND m.status='active' AND json_extract(a.result_json,'$.Category') <> 'none'
            ORDER BY a.analyzed_at DESC,m.id LIMIT 100;
            """, ("$group", MoectConversationKey(profile)));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<MoectAnalysisEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var result = JsonSerializer.Deserialize<MoectAnalysisResult>(reader.GetString(1));
            if (result is not null)
                results.Add(new(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture).ToLocalTime(), result));
        }
        return results;
    }

    private static string MoectConversationKey(MoectWatchProfile profile) =>
        SourceKeys.Hash(SourceKeys.Hash("wechat", profile.AccountAlias), profile.Group?.Id ?? throw new ArgumentException("缺少授权群。"));
}
