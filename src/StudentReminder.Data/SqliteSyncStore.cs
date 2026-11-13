using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudentReminder.Core;

namespace StudentReminder.Data;

public sealed partial class SqliteMessageRepository
{
    public async Task SaveSourceAsync(SourceDefinition source, CancellationToken cancellationToken = default)
    {
        source.Validate();
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var sourceCommand = Command(connection, transaction, """
            INSERT INTO sources(id,type,account_alias) VALUES($id,$type,$alias)
            ON CONFLICT(id) DO UPDATE SET enabled=0,health='disabled';
            """, ("$id", source.Id), ("$type", source.Platform), ("$alias", source.AccountAlias));
        await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
        using var query = Command(connection, transaction, "SELECT adapter,port FROM source_configs WHERE source_id=$id;", ("$id", source.Id));
        var reset = false;
        using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) reset = reader.GetString(0) != source.Adapter || reader.GetInt32(1) != source.Port;
        using var config = Command(connection, transaction, """
            INSERT INTO source_configs(source_id,adapter,port,poll_seconds,lookback_days,use_push)
            VALUES($id,$adapter,$port,$poll,$days,$push)
            ON CONFLICT(source_id) DO UPDATE SET adapter=$adapter,port=$port,poll_seconds=$poll,
                lookback_days=$days,use_push=$push,revision=revision+1;
            """, ("$id", source.Id), ("$adapter", source.Adapter), ("$port", source.Port),
            ("$poll", source.PollSeconds), ("$days", source.LookbackDays), ("$push", source.UsePush ? 1 : 0));
        await config.ExecuteNonQueryAsync(cancellationToken);
        if (reset)
        {
            using var resetCommand = Command(connection, transaction, """
                DELETE FROM sync_cursors WHERE source_id=$id;
                UPDATE conversations SET selected=0 WHERE source_id=$id;
                UPDATE sources SET last_ok_at=NULL WHERE id=$id;
                """, ("$id", source.Id));
            await resetCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SourceDefinition>> ReadSourcesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var query = Command(connection, null, """
            SELECT c.adapter,s.account_alias,c.port,c.poll_seconds,c.lookback_days,s.enabled,c.revision,s.health,s.last_ok_at,c.use_push,c.provider_account
            FROM source_configs c JOIN sources s ON s.id=c.source_id ORDER BY s.type,s.account_alias;
            """);
        using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var result = new List<SourceDefinition>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new SourceDefinition
        {
            Adapter = reader.GetString(0), AccountAlias = reader.GetString(1), Port = reader.GetInt32(2),
            PollSeconds = reader.GetInt32(3), LookbackDays = reader.GetInt32(4), Enabled = reader.GetInt32(5) == 1,
            Revision = reader.GetInt64(6), Health = reader.GetString(7),
            UsePush = reader.GetInt32(9) == 1,
            ProviderAccount = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastOkAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)
        });
        return result;
    }

    public async Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var query = Command(connection, transaction, """
            UPDATE sources SET enabled=$enabled,health=$status WHERE id=$id AND id IN (SELECT source_id FROM source_configs);
            UPDATE source_configs SET revision=revision+1 WHERE source_id=$id;
            """, ("$id", sourceId), ("$enabled", enabled ? 1 : 0), ("$status", enabled ? "pending" : "disabled"));
        await query.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MergeConversationsAsync(string sourceId, IReadOnlyList<SourceConversation> conversations,
        CancellationToken cancellationToken = default, string? providerAccount = null)
    {
        if (conversations.Count > 10000) throw new ArgumentException("会话数量超过上限。");
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        if (providerAccount is not null)
        {
            if (providerAccount.Length != 64 || providerAccount.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("无效来源账号标识。");
            using var bind = Command(connection, transaction, """
                UPDATE source_configs SET provider_account=$account
                WHERE source_id=$source AND adapter IN ('wxlocal','qqlocal')
                    AND (provider_account IS NULL OR provider_account=$account);
                """, ("$account", providerAccount), ("$source", sourceId));
            if (await bind.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("来源配置已变化，请重新检查连接。");
        }
        foreach (var conversation in conversations)
        {
            if (string.IsNullOrWhiteSpace(conversation.Id) || conversation.Id.Length > 512 || conversation.Type is not ("group" or "private"))
                throw new ArgumentException("无效会话。");
            using var command = Command(connection, transaction, """
                INSERT INTO conversations(id,source_id,external_id,name,type) VALUES($id,$source,$external,$name,$type)
                ON CONFLICT(id) DO UPDATE SET name=$name,type=$type;
                """, ("$id", SourceKeys.Hash(sourceId, conversation.Id)), ("$source", sourceId),
                ("$external", conversation.Id), ("$name", conversation.Name), ("$type", conversation.Type));
            await command.ExecuteNonQueryAsync(cancellationToken);
            using var backfill = Command(connection, transaction, """
                UPDATE messages SET payload_json=json_set(payload_json,'$.conversation_name',$name)
                WHERE conversation_id=$id AND json_extract(payload_json,'$.conversation_name')=$external
                    AND EXISTS (SELECT 1 FROM source_configs c
                        WHERE c.source_id=messages.source_id AND c.adapter='qqlocal');
                """, ("$id", SourceKeys.Hash(sourceId, conversation.Id)),
                ("$name", conversation.Name), ("$external", conversation.Id));
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SelectedConversation>> ReadConversationsAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null,
            "SELECT external_id,name,type,selected FROM conversations WHERE source_id=$id ORDER BY type,name;", ("$id", sourceId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SelectedConversation>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        return result;
    }

    public async Task SelectConversationsAsync(string sourceId, IReadOnlyList<string> conversationIds, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var reset = Command(connection, transaction, "UPDATE conversations SET selected=0 WHERE source_id=$id;", ("$id", sourceId));
        await reset.ExecuteNonQueryAsync(cancellationToken);
        foreach (var id in conversationIds.Distinct(StringComparer.Ordinal))
        {
            using var select = Command(connection, transaction,
                "UPDATE conversations SET selected=1 WHERE source_id=$source AND external_id=$external;", ("$source", sourceId), ("$external", id));
            if (await select.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ArgumentException("选择的会话不存在。");
        }
        using var revision = Command(connection, transaction, "UPDATE source_configs SET revision=revision+1 WHERE source_id=$id;", ("$id", sourceId));
        await revision.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string?> ReadCursorAsync(string sourceId, string conversationId, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, "SELECT cursor_json FROM sync_cursors WHERE source_id=$source AND conversation_id=$conversation;",
            ("$source", sourceId), ("$conversation", SourceKeys.Hash(sourceId, conversationId)));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<ImportResult> CommitPageAsync(SourceDefinition source, string conversationId, MessagePage page, CancellationToken cancellationToken = default)
    {
        if (page.Messages.Count > MessageValidation.MaxMessages || (page.HasMore && page.NextCursor is null)) throw new ArgumentException("无效同步分页。");
        var normalized = page.Messages.Select(MessageValidation.Normalize).ToArray();
        if (normalized.Any(message => message.Source != source.Platform || message.AccountId != source.AccountAlias
            || message.Adapter != source.Adapter || message.ConversationId != conversationId))
            throw new ArgumentException("消息与授权来源或会话不一致。");
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureAuthorizedAsync(connection, transaction, source, conversationId, cancellationToken);
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index].MessageId is not { } messageId) continue;
            using var recalled = Command(connection, transaction, """
                SELECT 1 FROM recall_markers WHERE source_id=$source AND conversation_external_id=$conversation AND message_external_id=$message;
                """, ("$source", source.Id), ("$conversation", conversationId), ("$message", messageId));
            if (await recalled.ExecuteScalarAsync(cancellationToken) is not null)
                normalized[index] = normalized[index] with { IsRecalled = true, OriginEvent = "message.revoke" };
        }
        var result = await SaveBatchCoreAsync(connection, transaction, normalized, cancellationToken, isLocalReaderSync: source.Adapter is "wxlocal" or "qqlocal");
        if (page.NextCursor is not null)
        {
            using var cursor = Command(connection, transaction, """
                INSERT INTO sync_cursors(source_id,conversation_id,cursor_json,updated_at) VALUES($source,$conversation,$cursor,$time)
                ON CONFLICT(source_id,conversation_id) DO UPDATE SET cursor_json=$cursor,updated_at=$time;
                """, ("$source", source.Id), ("$conversation", SourceKeys.Hash(source.Id, conversationId)),
                ("$cursor", page.NextCursor), ("$time", Format(DateTimeOffset.UtcNow)));
            await cursor.ExecuteNonQueryAsync(cancellationToken);
        }
        using var health = Command(connection, transaction, "UPDATE sources SET health='ok',last_ok_at=$time WHERE id=$id;",
            ("$id", source.Id), ("$time", Format(DateTimeOffset.UtcNow)));
        await health.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task RecallAsync(SourceDefinition source, string conversationId, string messageId, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureAuthorizedAsync(connection, transaction, source, conversationId, cancellationToken);
        using var marker = Command(connection, transaction, """
            INSERT INTO recall_markers(source_id,conversation_external_id,message_external_id,recalled_at)
            VALUES($source,$conversation,$message,$time) ON CONFLICT DO NOTHING;
            """, ("$source", source.Id), ("$conversation", conversationId), ("$message", messageId), ("$time", Format(DateTimeOffset.UtcNow)));
        await marker.ExecuteNonQueryAsync(cancellationToken);
        var id = SourceKeys.Hash(source.Id, SourceKeys.Hash(source.Id, conversationId), "external", messageId);
        using var query = Command(connection, transaction, "SELECT payload_json FROM messages WHERE id=$id;", ("$id", id));
        if (await query.ExecuteScalarAsync(cancellationToken) is string json)
        {
            var message = JsonSerializer.Deserialize<NormalizedMessage>(json, ContractJson.Options)!;
            using var update = Command(connection, transaction,
                "UPDATE messages SET payload_json=$payload,status=CASE WHEN status='conflict' THEN status ELSE 'recalled' END WHERE id=$id;",
                ("$id", id), ("$payload", JsonSerializer.Serialize(message with { IsRecalled = true, OriginEvent = "message.revoke" }, ContractJson.Options)));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetHealthAsync(string sourceId, string code, bool successful, CancellationToken cancellationToken = default)
    {
        if (!SourceStatus.Codes.Contains(code)) throw new ArgumentException("无效健康状态。");
        using var connection = await OpenAsync(cancellationToken);
        using var command = Command(connection, null, """
            UPDATE sources SET health=CASE WHEN enabled=0 THEN 'disabled' ELSE $code END,
                last_ok_at=CASE WHEN $ok=1 THEN $time ELSE last_ok_at END WHERE id=$id;
            """, ("$id", sourceId), ("$code", code), ("$ok", successful ? 1 : 0), ("$time", Format(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAuthorizedAsync(SqliteConnection connection, SqliteTransaction transaction,
        SourceDefinition source, string conversationId, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            SELECT COUNT(*) FROM sources s JOIN source_configs c ON c.source_id=s.id
            JOIN conversations v ON v.source_id=s.id WHERE s.id=$id AND s.enabled=1 AND c.revision=$revision
                AND v.external_id=$conversation AND v.selected=1;
            """, ("$id", source.Id), ("$revision", source.Revision), ("$conversation", conversationId));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new OperationCanceledException("来源或会话已关闭，或配置已改变。", cancellationToken);
    }
}
