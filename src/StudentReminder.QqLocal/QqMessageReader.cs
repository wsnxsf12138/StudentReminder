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
/// 40093 sender nickname snapshot, 40800 protobuf body). Text is extracted from field 45101;
/// media becomes a placeholder.
/// </summary>
public sealed class QqMessageReader : IDisposable
{
    private const string C2cTable = "c2c_msg_table";
    private const string GroupTable = "group_msg_table";
    /// <summary>Message column holding the sender's nickname as a snapshot taken at send time.</summary>
    private const string SenderNameColumn = "40093";

    private readonly string _root;
    private readonly SqliteConnection _msg;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal); // uid -> name
    private readonly Dictionary<string, string> _groupNames = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Group, string Uid), string> _groupMembers = new(); // (group id, uid) -> display name
    private readonly Dictionary<string, bool> _hasSenderName = new(StringComparer.Ordinal); // table -> has 40093

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
        _groupNames.Clear();
        LoadNameMap("profile_info.db", _names);
        LoadQqNtBuddyNames();
        LoadQqNtGroupNames();
        LoadQqNtGroupMembers();
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
                ReadNameMap(connection, table, uidCol, nameCol, target);
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// QQ NT stores friend uid/nick/remark in profile_info_v6 numeric columns:
    /// 1000 = uid, 20002 = nickname, 20009 = remark. Prefer the remark (as the QQ
    /// client does) and fall back to the nickname when no remark is set.
    /// </summary>
    private void LoadQqNtBuddyNames()
    {
        var path = Path.Combine(_root, "profile_info.db");
        if (!File.Exists(path)) return;
        try
        {
            using var connection = Open(path);
            var tables = ListTables(connection).ToHashSet(StringComparer.Ordinal);
            if (!tables.Contains("profile_info_v6")) return;
            var columns = TableColumns(connection, "profile_info_v6");
            if (!columns.Contains("1000")) return;

            var nameCols = new List<string>(2);
            if (columns.Contains("20009")) nameCols.Add("20009");
            if (columns.Contains("20002")) nameCols.Add("20002");
            if (nameCols.Count == 0) return;

            var select = string.Join(", ", nameCols.Select(c => $"\"{c}\""));
            using var command = connection.CreateCommand();
            command.CommandText = $"select \"1000\", {select} from \"profile_info_v6\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                if (id.Length == 0) continue;
                for (var i = 0; i < nameCols.Count; i++)
                {
                    var name = reader.IsDBNull(i + 1) ? "" : Convert.ToString(reader.GetValue(i + 1), CultureInfo.InvariantCulture) ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) { _names.TryAdd(id, name); break; }
                }
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
    }

    /// <summary>QQ NT stores group uin/name in numeric columns 60001/60007.</summary>
    private void LoadQqNtGroupNames()
    {
        var path = Path.Combine(_root, "group_info.db");
        if (!File.Exists(path)) return;
        try
        {
            using var connection = Open(path);
            var tables = ListTables(connection).ToHashSet(StringComparer.Ordinal);
            foreach (var table in new[] { "group_list", "group_detail_info_ver1" })
            {
                if (!tables.Contains(table)) continue;
                var columns = TableColumns(connection, table);
                if (columns.Contains("60001") && columns.Contains("60007"))
                    ReadNameMap(connection, table, "60001", "60007", _groupNames);
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// QQ NT stores per-group member display names in group_member3 numeric columns:
    /// 60001 = group id, 1000 = member uid, 64003 = group card, 20002 = nickname,
    /// 64029 = remark. Prefer the group card, then the nickname, then the remark.
    /// </summary>
    private void LoadQqNtGroupMembers()
    {
        var path = Path.Combine(_root, "group_info.db");
        if (!File.Exists(path)) return;
        try
        {
            using var connection = Open(path);
            var tables = ListTables(connection).ToHashSet(StringComparer.Ordinal);
            if (!tables.Contains("group_member3")) return;
            var columns = TableColumns(connection, "group_member3");
            if (!columns.Contains("60001") || !columns.Contains("1000")) return;

            var nameCols = new List<string>(3);
            if (columns.Contains("64003")) nameCols.Add("64003");
            if (columns.Contains("20002")) nameCols.Add("20002");
            if (columns.Contains("64029")) nameCols.Add("64029");
            if (nameCols.Count == 0) return;

            var select = string.Join(", ", nameCols.Select(c => $"\"{c}\""));
            using var command = connection.CreateCommand();
            command.CommandText = $"select \"60001\", \"1000\", {select} from \"group_member3\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupId = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                var uid = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                if (groupId.Length == 0 || uid.Length == 0) continue;
                for (var i = 0; i < nameCols.Count; i++)
                {
                    var name = reader.IsDBNull(i + 2) ? "" : Convert.ToString(reader.GetValue(i + 2), CultureInfo.InvariantCulture) ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) { _groupMembers.TryAdd((groupId, uid), name); break; }
                }
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
    }

    private static void ReadNameMap(SqliteConnection connection, string table,
        string uidCol, string nameCol, Dictionary<string, string> target)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"select \"{uidCol}\", \"{nameCol}\" from \"{table}\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                var name = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                if (id.Length > 0 && !string.IsNullOrWhiteSpace(name)) target.TryAdd(id, name);
            }
        }
        catch (SqliteException) { }
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

    /// <summary>
    /// Whether a message table exposes <see cref="SenderNameColumn"/>. Older QQ NT builds may
    /// omit the column, so it is only selected when present; otherwise messages are read as before.
    /// </summary>
    private bool HasSenderNameColumn(string table)
    {
        if (_hasSenderName.TryGetValue(table, out var known)) return known;
        var present = false;
        try
        {
            present = TableColumns(_msg, table).Contains(SenderNameColumn);
        }
        catch (SqliteException) { }
        _hasSenderName[table] = present;
        return present;
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
        return _groupNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id;
    }

    public string GetConversationName(string id, bool group) => group ? GroupName(id) : DisplayName(id);

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
                // QQ NT leaves placeholder rows with peer "0"; drop them so they don't
                // surface as a bogus "0" conversation.
                if (id.Length == 0 || id == "0") continue;
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
        var withSenderName = HasSenderNameColumn(table);
        try
        {
            using var command = _msg.CreateCommand();
            command.CommandText = $"select rowid, \"40001\", \"40050\", \"40020\", \"40033\", \"40013\", \"40800\"" +
                (withSenderName ? ", \"40093\"" : "") +
                $" from \"{table}\" where \"40021\" = $id order by rowid asc";
            command.Parameters.AddWithValue("$id", conversationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var createTime = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (startUnix.HasValue && createTime < startUnix.Value) continue;
                if (endUnix.HasValue && createTime > endUnix.Value) continue;
                messages.Add(BuildMessage(reader, conversationId, group, withSenderName));
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
        var withSenderName = HasSenderNameColumn(table);
        try
        {
            using var command = _msg.CreateCommand();
            command.CommandText = $"select rowid, \"40001\", \"40050\", \"40020\", \"40033\", \"40013\", \"40800\"" +
                (withSenderName ? ", \"40093\"" : "") +
                $" from \"{table}\" where \"40021\" = $id and rowid > $rowid order by rowid asc";
            command.Parameters.AddWithValue("$id", conversationId);
            command.Parameters.AddWithValue("$rowid", lastRowid);
            using var reader = command.ExecuteReader();
            while (reader.Read()) messages.Add(BuildMessage(reader, conversationId, group, withSenderName));
        }
        catch (SqliteException) { }
        return messages;
    }

    private QqMessage BuildMessage(SqliteDataReader reader, string conversationId, bool group, bool withSenderName)
    {
        var rowId = reader.GetInt64(0);
        var msgId = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        var createTime = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        var senderUid = TextOf(reader, 3);
        var senderQq = TextOf(reader, 4);
        var direction = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
        // QQ NT direction: 0 = received, 3 = system notice, 1/2/4/5 = sent by me.
        var sentByMe = direction != 0 && direction != 3;
        var blob = ReadBlob(reader, 6);
        var text = blob is null ? "" : ProtoReader.ExtractText(blob);
        var kind = text.Length > 0 ? "text" : "other";
        if (kind == "other") text = "[非文本消息]";
        // A send-time nickname snapshot; QQ NT fills it for roughly 41% of received group
        // messages. It is only a last resort, after the member/friend maps.
        var senderNameSnapshot = withSenderName ? TextOf(reader, 7).Trim() : "";
        var sender = ResolveSender(senderUid, senderQq, sentByMe, group, conversationId, senderNameSnapshot);
        return new QqMessage(conversationId, rowId, msgId, createTime, senderUid, senderQq,
            sender, sentByMe, kind, text);
    }

    /// <summary>
    /// Display name for a message sender, in order: me → system notice → group member card /
    /// nickname / remark → friend remark / nickname → nickname snapshot carried by the message
    /// itself (40093) → the raw uid or QQ number.
    /// </summary>
    private string ResolveSender(string senderUid, string senderQq, bool sentByMe, bool group,
        string groupId, string senderNameSnapshot)
    {
        if (sentByMe) return "我";
        // QQ NT fills 40020 with the group number (and 40033 with 0) for group system rows such
        // as member or announcement notices. A real sender always carries a "u_" uid, so a pure
        // numeric uid equal to the group id means the row has no individual sender.
        if (group && senderUid.Length > 0 && senderUid == groupId && IsAllDigits(senderUid)) return "系统";
        if (senderUid.Length > 0)
        {
            if (group && _groupMembers.TryGetValue((groupId, senderUid), out var member) && member.Length > 0)
                return member;
            if (_names.TryGetValue(senderUid, out var name) && name.Length > 0) return name;
            if (!string.IsNullOrWhiteSpace(senderNameSnapshot)) return senderNameSnapshot;
            return senderUid;
        }
        if (senderQq.Length > 0 && senderQq != "0") return senderQq;
        return "系统";
    }

    private static bool IsAllDigits(string value)
    {
        foreach (var ch in value)
            if (ch is < '0' or > '9') return false;
        return true;
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
