using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace StudentReminder.QqLocal;

public sealed record QqConversation(string Id, string Name, string Type, long LastTime, long Count);

public sealed record QqMessage(
    string Conversation, long RowId, long MsgId, long CreateTime,
    string SenderUid, string SenderQq, string Sender, bool SentByMe,
    string Kind, string Text);

/// <summary>
/// Reads decrypted QQ NT databases (nt_msg.db, profile_info.db, group_info.db) into a
/// conversation + message model. Numeric column names mirror QQ NT's schema (40001 msgid,
/// 40050 time, 40020 sender uid, 40033 sender qq, 40021 peer, 40030 peer qq, 40013 direction,
/// 40800 protobuf body). Text is extracted from field 45101; media becomes a placeholder.
/// </summary>
public sealed class QqMessageReader : IDisposable
{
    private const string C2cTable = "c2c_msg_table";
    private const string GroupTable = "group_msg_table";

    private readonly string _root;
    private readonly SqliteConnection _msg;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal); // uid -> name
    private readonly Dictionary<string, string> _groupNames = new(StringComparer.Ordinal);

    public QqMessageReader(string decryptedDir)
    {
        _root = decryptedDir;
        _msg = Open(Path.Combine(decryptedDir, "nt_msg.db"));
        LoadNames();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }

    private void LoadNames()
    {
        _names.Clear();
        LoadNameMap("profile_info.db", _names);
        LoadNameMap("group_info.db", _groupNames);
    }

    /// <summary>Best-effort uid/name mapping from a decrypted sibling database.</summary>
    private void LoadNameMap(string dbName, Dictionary<string, string> target)
    {
        var path = Path.Combine(_root, dbName);
        if (!File.Exists(path)) return;
        try
        {
            using var connection = Open(path);
            foreach (var table in ListTables(connection))
            {
                var columns = TableColumns(connection, table);
                var uidCol = FirstOf(columns, "uid", "uin", "group_id", "group_code", "id");
                var nameCol = FirstOf(columns, "nickname", "nick", "name", "remark", "group_name");
                if (uidCol is null || nameCol is null) continue;
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"select \"{uidCol}\", \"{nameCol}\" from \"{table}\"";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                        var name = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                        if (id.Length > 0 && name.Length > 0) target.TryAdd(id, name);
                    }
                }
                catch (SqliteException) { }
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
    }

    private static string? FirstOf(HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.Contains(name)) return name;
        return null;
    }

    private static IEnumerable<string> ListTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type='table'";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private static HashSet<string> TableColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private HashSet<string> Tables()
    {
        using var command = _msg.CreateCommand();
        command.CommandText = "select name from sqlite_master where type='table'";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private string DisplayName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return _names.TryGetValue(id, out var name) && name.Length > 0 ? name : id;
    }

    private string GroupName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return _groupNames.TryGetValue(id, out var name) && name.Length > 0 ? name : id;
    }

    /// <summary>Distinct conversations from the c2c and group tables, ordered by last activity.</summary>
    public IReadOnlyList<QqConversation> ListConversations()
    {
        var tables = Tables();
        var conversations = new List<QqConversation>();
        if (tables.Contains(GroupTable))
        {
            foreach (var (id, count, last) in Summarize(GroupTable, "group"))
                conversations.Add(new QqConversation(id, GroupName(id), "group", last, count));
        }
        if (tables.Contains(C2cTable))
        {
            foreach (var (id, count, last) in Summarize(C2cTable, "private"))
                conversations.Add(new QqConversation(id, DisplayName(id), "private", last, count));
        }
        return conversations.OrderByDescending(c => c.LastTime).ToList();
    }

    private List<(string Id, long Count, long Last)> Summarize(string table, string _)
    {
        var result = new List<(string, long, long)>();
        try
        {
            using var command = _msg.CreateCommand();
            command.CommandText =
                $"select \"40021\", count(*), max(\"40050\") from \"{table}\" group by \"40021\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                if (id.Length == 0) continue;
                var count = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                var last = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                result.Add((id, count, last));
            }
        }
        catch (SqliteException) { }
        return result;
    }

    /// <summary>Detect whether a conversation id lives in the group table (true), c2c (false), or neither (null).</summary>
    public bool? IsGroup(string conversationId)
    {
        try
        {
            using (var command = _msg.CreateCommand())
            {
                command.CommandText = $"select count(*) from \"{GroupTable}\" where \"40021\" = $id limit 1";
                command.Parameters.AddWithValue("$id", conversationId);
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return true;
            }
            using (var command = _msg.CreateCommand())
            {
                command.CommandText = $"select count(*) from \"{C2cTable}\" where \"40021\" = $id limit 1";
                command.Parameters.AddWithValue("$id", conversationId);
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return false;
            }
        }
        catch (SqliteException) { }
        return null;
    }

    /// <summary>Read messages of one conversation (group or private), ordered by rowid.</summary>
    public IReadOnlyList<QqMessage> ReadMessages(string conversationId, bool group,
        long? startUnix = null, long? endUnix = null)
    {
        var table = group ? GroupTable : C2cTable;
        var messages = new List<QqMessage>();
        try
        {
            using var command = _msg.CreateCommand();
            command.CommandText = $"select rowid, \"40001\", \"40050\", \"40020\", \"40033\", \"40013\", \"40800\"" +
                $" from \"{table}\" where \"40021\" = $id order by rowid asc";
            command.Parameters.AddWithValue("$id", conversationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var createTime = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (startUnix.HasValue && createTime < startUnix.Value) continue;
                if (endUnix.HasValue && createTime > endUnix.Value) continue;
                messages.Add(BuildMessage(reader, conversationId, group));
            }
        }
        catch (SqliteException) { }
        return messages;
    }

    /// <summary>Read messages strictly newer than <paramref name="lastRowid"/> (rowid is monotonic).</summary>
    public IReadOnlyList<QqMessage> ReadMessagesAfter(string conversationId, bool group, long lastRowid)
    {
        var table = group ? GroupTable : C2cTable;
        var messages = new List<QqMessage>();
        try
        {
            using var command = _msg.CreateCommand();
            command.CommandText = $"select rowid, \"40001\", \"40050\", \"40020\", \"40033\", \"40013\", \"40800\"" +
                $" from \"{table}\" where \"40021\" = $id and rowid > $rowid order by rowid asc";
            command.Parameters.AddWithValue("$id", conversationId);
            command.Parameters.AddWithValue("$rowid", lastRowid);
            using var reader = command.ExecuteReader();
            while (reader.Read()) messages.Add(BuildMessage(reader, conversationId, group));
        }
        catch (SqliteException) { }
        return messages;
    }

    private QqMessage BuildMessage(SqliteDataReader reader, string conversationId, bool group)
    {
        var rowId = reader.GetInt64(0);
        var msgId = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        var createTime = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        var senderUid = TextOf(reader, 3);
        var senderQq = TextOf(reader, 4);
        var direction = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
        var sentByMe = direction == 1;
        var blob = ReadBlob(reader, 6);
        var text = blob is null ? "" : ProtoReader.ExtractText(blob);
        var kind = text.Length > 0 ? "text" : "other";
        var display = kind == "text"
            ? (senderQq.Length > 0 ? DisplayName(senderQq) : senderUid)
            : (senderQq.Length > 0 ? senderQq : senderUid);
        if (kind == "other") text = "[非文本消息]";
        return new QqMessage(conversationId, rowId, msgId, createTime, senderUid, senderQq,
            display.Length > 0 ? display : (sentByMe ? "我" : "未知发送者"),
            sentByMe, kind, text);
    }

    private static string TextOf(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return "";
        var value = reader.GetValue(index);
        return value switch
        {
            string text => text,
            long number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

    private static byte[]? ReadBlob(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        return reader.GetValue(index) switch
        {
            byte[] blob => blob,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null,
        };
    }

    public void Dispose()
    {
        try { _msg.Dispose(); } catch (SqliteException) { }
    }
}
