using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace StudentReminder.WxLocal;

public sealed record WxSessionInfo(
    string Username, string Display, string Category, bool IsGroup,
    long LastTime, string Summary, long Unread);

public sealed record WxMessage(
    string Session, long LocalId, long SortSeq, string ServerId, int Shard, int Type, string TypeName,
    long CreateTime, string SenderWxid, string Sender, bool SentByMe,
    string Content, string Kind, string Text);

/// <summary>
/// Reads decrypted WeChat 4.x databases into a session + message model:
/// session.db (SessionTable), contact.db (contact/stranger/chat_room/chatroom_member),
/// message_N.db / biz_message_N.db (one Msg_&lt;md5(session)&gt; table per conversation,
/// sharded) and media_N.db. Message content is ZSTD-compressed or XML depending on type.
/// </summary>
public sealed partial class WxMessageReader : IDisposable
{
    [GeneratedRegex(@"^message(_\d+)?\.db$")]
    private static partial Regex MessageDbPattern();

    [GeneratedRegex(@"^biz_message(_\d+)?\.db$")]
    private static partial Regex BizMessageDbPattern();

    [GeneratedRegex(@"^media(_\d+)?\.db$")]
    private static partial Regex MediaDbPattern();

    [GeneratedRegex(@"^Msg_[0-9a-f]{32}$")]
    private static partial Regex MsgTablePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@-]{1,190}$")]
    private static partial Regex SenderIdPattern();

    private static readonly byte[] ZstdMagic = [0x28, 0xb5, 0x2f, 0xfd];
    private const int MaxMessagesPerSession = 100_000;

    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [1] = "文本", [3] = "图片", [34] = "语音", [37] = "好友请求", [42] = "名片", [43] = "视频",
        [47] = "动画表情", [48] = "位置", [49] = "应用消息", [50] = "音视频通话", [51] = "状态",
        [62] = "小视频", [66] = "连线", [10000] = "系统消息", [10002] = "系统消息",
        [11000] = "动画表情", [11001] = "引用消息", [11002] = "文件",
    };

    private readonly string _root;
    private readonly string _wxid;
    private readonly List<SqliteConnection> _msgDbs = [];
    private readonly List<SqliteConnection> _mediaDbs = [];
    private readonly Dictionary<string, SqliteConnection> _conns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SqliteConnection, HashSet<string>> _tables = [];
    private readonly Dictionary<string, Dictionary<string, object?>> _contacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _roomMembers = new(StringComparer.Ordinal);

    public WxMessageReader(string decryptedDir, string wxid = "")
    {
        _root = decryptedDir;
        _wxid = NormalizeWxid(wxid);
        ScanDbs();
        LoadContacts();
        LoadRooms();
    }

    private static string NormalizeWxid(string wxid) =>
        Regex.Replace(wxid, @"_[0-9a-fA-F]{4}$", "");

    private void ScanDbs()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*.db", SearchOption.AllDirectories))
        {
            var name = NormalizeDbName(Path.GetFileName(path));
            List<SqliteConnection>? bucket = null;
            if (MessageDbPattern().IsMatch(name) || BizMessageDbPattern().IsMatch(name)) bucket = _msgDbs;
            else if (MediaDbPattern().IsMatch(name)) bucket = _mediaDbs;
            RegisterDb(path, bucket);
        }
        _msgDbs.Sort((left, right) => string.Compare(ConnectionString(left), ConnectionString(right), StringComparison.Ordinal));
        _mediaDbs.Sort((left, right) => string.Compare(ConnectionString(left), ConnectionString(right), StringComparison.Ordinal));
    }

    private static string NormalizeDbName(string name) =>
        Regex.Replace(name, @"\s*\([^)]*\)\.db$", ".db");

    private void RegisterDb(string path, List<SqliteConnection>? bucket)
    {
        try
        {
            var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            _conns[NormalizeDbName(Path.GetFileName(path))] = connection;
            _tables[connection] = [.. ListTables(connection)];
            bucket?.Add(connection);
        }
        catch (SqliteException) { }
    }

    private static string ConnectionString(SqliteConnection connection) => connection.DataSource;

    private static IEnumerable<string> ListTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type='table'";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private HashSet<string> TablesOf(SqliteConnection connection) =>
        _tables.TryGetValue(connection, out var tables) ? tables : [];

    private SqliteConnection? ConnectionOf(string dbBaseName)
    {
        return _conns.TryGetValue(dbBaseName + ".db", out var connection) ? connection : null;
    }

    private void LoadContacts()
    {
        var connection = ConnectionOf("contact");
        if (connection is null) return;
        foreach (var table in new[] { "contact", "stranger" })
        {
            if (!TablesOf(connection).Contains(table)) continue;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"select * from \"{table}\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++) map[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    var username = StringOf(map, "username") ?? StringOf(map, "user_name") ?? "";
                    var id = StringOf(map, "id");
                    var record = map;
                    if (id is not null) _contacts.TryAdd("#id:" + id, record);
                    if (username.Length > 0)
                    {
                        record["username"] = username;
                        _contacts.TryAdd(username, record);
                    }
                }
            }
            catch (SqliteException) { }
        }
    }

    private static string? StringOf(Dictionary<string, object?> map, params string[] names)
    {
        foreach (var name in names)
            if (map.TryGetValue(name, out var value) && value is string text && text.Length > 0) return text;
        return null;
    }

    private string DisplayName(string username)
    {
        if (_contacts.TryGetValue(username, out var record))
        {
            foreach (var field in new[] { "remark", "nick_name", "alias", "username" })
                if (record.TryGetValue(field, out var value) && value is string text && text.Length > 0) return text;
        }
        return username == _wxid ? "我" : username;
    }

    private void LoadRooms()
    {
        var connection = ConnectionOf("contact");
        if (connection is null) return;
        try
        {
            var roomIds = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select id,username from chat_room";
                using var reader = command.ExecuteReader();
                while (reader.Read()) roomIds[reader.GetString(0)] = reader.GetString(1);
            }
            using var memberCommand = connection.CreateCommand();
            memberCommand.CommandText = "select room_id,member_id from chatroom_member";
            using var memberReader = memberCommand.ExecuteReader();
            while (memberReader.Read())
            {
                if (!roomIds.TryGetValue(memberReader.GetString(0), out var room)) continue;
                if (_contacts.TryGetValue("#id:" + memberReader.GetString(1), out var member)
                    && member.TryGetValue("username", out var username) && username is string memberName)
                {
                    if (!_roomMembers.TryGetValue(room, out var list)) _roomMembers[room] = list = [];
                    list.Add(memberName);
                }
            }
        }
        catch (SqliteException) { }
    }

    /// <summary>Session list from session.db, newest first.</summary>
    public IReadOnlyList<WxSessionInfo> ListSessions()
    {
        var connection = ConnectionOf("session");
        var sessions = new List<WxSessionInfo>();
        if (connection is null || !TablesOf(connection).Contains("SessionTable")) return sessions;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select * from SessionTable";
            using var reader = command.ExecuteReader();
            var ordinal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++) ordinal[reader.GetName(i)] = i;
            while (reader.Read())
            {
                var username = ColumnText(reader, ordinal, "username");
                if (username.Length == 0) continue;
                var lastTime = ColumnLong(reader, ordinal, "last_timestamp");
                var sortTime = ColumnLong(reader, ordinal, "sort_timestamp");
                if (sortTime == 0) sortTime = lastTime;
                var summary = ColumnText(reader, ordinal, "summary");
                sessions.Add(new WxSessionInfo(username, SessionDisplay(username), CategoryOf(username),
                    username.EndsWith("@chatroom", StringComparison.Ordinal), sortTime,
                    summary.Length <= 120 ? summary : summary[..120],
                    ColumnLong(reader, ordinal, "unread_count")));
            }
        }
        catch (SqliteException) { }
        return sessions.OrderByDescending(session => session.LastTime).ToList();
    }

    private static string ColumnText(SqliteDataReader reader, Dictionary<string, int> ordinal, string name)
    {
        if (!ordinal.TryGetValue(name, out var index) || reader.IsDBNull(index)) return "";
        var value = reader.GetValue(index);
        return value switch
        {
            byte[] blob => DecodeContent(blob, null),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

    private static long ColumnLong(SqliteDataReader reader, Dictionary<string, int> ordinal, string name)
    {
        if (!ordinal.TryGetValue(name, out var index) || reader.IsDBNull(index)) return 0;
        try { return Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }

    private string SessionDisplay(string username)
    {
        var name = DisplayName(username);
        if (name != username || !username.EndsWith("@chatroom", StringComparison.Ordinal)) return name;
        if (_roomMembers.TryGetValue(username, out var members) && members.Count > 0)
        {
            var names = members.Take(3).Select(DisplayName).ToList();
            return $"{string.Join("、", names)}({members.Count}人)";
        }
        return username.Replace("@chatroom", "");
    }

    private string CategoryOf(string username)
    {
        if (username.EndsWith("@chatroom", StringComparison.Ordinal)) return "group";
        _contacts.TryGetValue(username, out var contact);
        var verified = 0L;
        if (contact is not null)
        {
            var flag = StringOf(contact, "verify_flag") ?? StringOf(contact, "verifyflag");
            long.TryParse(flag, out verified);
        }
        if (username.StartsWith("gh_") || (verified & 8) != 0) return "official";
        if (username.StartsWith("local-msg:") || SpecialUsernames.Contains(username)) return "other";
        return "friend";
    }

    private static readonly HashSet<string> SpecialUsernames = new(StringComparer.Ordinal)
    {
        "filehelper", "fmessage", "medianote", "newsapp", "notification_messages", "weixin", "qqmail", "tmessage",
    };

    /// <summary>Table name of one conversation inside the message databases.</summary>
    public static string MsgTableFor(string username) =>
        "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(username))).ToLowerInvariant();

    /// <summary>Read messages of one conversation across shards, ordered by time.</summary>
    public IReadOnlyList<WxMessage> ReadMessages(string username, long? startUnix = null, long? endUnix = null)
    {
        var table = MsgTableFor(username);
        var messages = new List<WxMessage>();
        var shard = 0;
        foreach (var connection in _msgDbs)
        {
            if (!TablesOf(connection).Contains(table)) continue;
            var id2Name = LoadName2Id(connection);
            var columns = TableColumns(connection, table);
            if (!columns.Contains("create_time")) continue;
            var select = MessageSelect(columns);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"select {select} from \"{table}\" order by sort_seq asc, local_id asc";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var createTime = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5));
                    if (startUnix.HasValue && createTime < startUnix.Value) continue;
                    if (endUnix.HasValue && createTime > endUnix.Value) continue;
                    messages.Add(BuildMessage(reader, id2Name, username, shard));
                    if (messages.Count > MaxMessagesPerSession) break;
                }
            }
            catch (SqliteException) { }
            shard++;
        }
        messages.Sort((left, right) => (left.CreateTime, left.Shard, left.LocalId).CompareTo((right.CreateTime, right.Shard, right.LocalId)));
        return messages;
    }

    /// <summary>Shared column projection used by the message readers.</summary>
    private static string MessageSelect(HashSet<string> columns)
    {
        var select = new List<string>();
        foreach (var (columnName, fallback) in new (string Column, string Fallback)[]
                 {
                     ("local_id", "NULL"), ("server_id", "NULL"), ("local_type", "NULL"),
                     ("sort_seq", "NULL"), ("real_sender_id", "NULL"), ("create_time", "NULL"),
                     ("message_content", "NULL"), ("compress_content", "NULL"),
                 })
            select.Add(columns.Contains(columnName) ? $"\"{columnName}\"" : fallback + " as \"" + columnName + "\"");
        return string.Join(", ", select);
    }

    /// <summary>
    /// Read messages strictly newer than (lastSortSeq, lastLocalId) across shards,
    /// ordered ascending. Used for incremental (real-time) sync where the cursor is the
    /// last observed (sort_seq, local_id) pair.
    /// </summary>
    public IReadOnlyList<WxMessage> ReadMessagesAfter(string username, long lastSortSeq, long lastLocalId)
    {
        var table = MsgTableFor(username);
        var messages = new List<WxMessage>();
        var shard = 0;
        foreach (var connection in _msgDbs)
        {
            if (!TablesOf(connection).Contains(table)) continue;
            var columns = TableColumns(connection, table);
            // The incremental cursor depends on a monotonic sort_seq + local_id pair.
            if (!columns.Contains("sort_seq") || !columns.Contains("local_id") || !columns.Contains("create_time")) continue;
            var id2Name = LoadName2Id(connection);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"select {MessageSelect(columns)} from \"{table}\"" +
                    " where sort_seq > $seq or (sort_seq = $seq and local_id > $id)" +
                    " order by sort_seq asc, local_id asc";
                command.Parameters.AddWithValue("$seq", lastSortSeq);
                command.Parameters.AddWithValue("$id", lastLocalId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    messages.Add(BuildMessage(reader, id2Name, username, shard));
                    if (messages.Count > MaxMessagesPerSession) break;
                }
            }
            catch (SqliteException) { }
            shard++;
        }
        messages.Sort((left, right) => (left.SortSeq, left.LocalId, left.Shard).CompareTo((right.SortSeq, right.LocalId, right.Shard)));
        return messages;
    }

    private static Dictionary<long, string> LoadName2Id(SqliteConnection connection)
    {
        var map = new Dictionary<long, string>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select rowid,user_name from Name2Id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                map[reader.GetInt64(0)] = reader.GetString(1);
        }
        catch (SqliteException) { }
        return map;
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

    private WxMessage BuildMessage(SqliteDataReader reader, Dictionary<long, string> id2Name,
        string session, int shard)
    {
        var rawType = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));
        var type = NormalizeLocalType(rawType);
        var messageContent = ReadBlob(reader, 6);
        var compressContent = ReadBlob(reader, 7);
        var content = DecodeContent(messageContent, compressContent);
        var sender = reader.IsDBNull(4) ? "" : id2Name.GetValueOrDefault(Convert.ToInt64(reader.GetValue(4)), "");
        if (session.EndsWith("@chatroom", StringComparison.Ordinal))
            (sender, content) = StripGroupSenderPrefix(content, sender, session, id2Name);
        var createTime = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5));
        var serverId = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1)) ?? "";
        var localId = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0));
        var sortSeq = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3));
        var (kind, text) = Describe(type, content);
        return new WxMessage(session, localId, sortSeq, serverId, shard, type,
            TypeNames.GetValueOrDefault(type, $"其他({type})"), createTime,
            sender, sender.Length > 0 ? DisplayName(sender) : (type is 10000 or 10002 ? "系统" : "未知发送者"),
            sender.Length > 0 && sender == _wxid, content, kind, text);
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

    /// <summary>local_type packs (subtype &lt;&lt; 32) | base_type in newer 4.x builds.</summary>
    public static int NormalizeLocalType(long rawType)
    {
        if (rawType is <= 0xFFFF and >= 0) return (int)rawType;
        var low = rawType & 0xFFFFFFFF;
        if (low is >= 1 and <= 99 or 10000 or 10002) return (int)low;
        return (int)(low & 0xFF);
    }

    private (string Sender, string Content) StripGroupSenderPrefix(string content, string sender,
        string session, Dictionary<long, string> id2Name)
    {
        var head = content.Length <= 256 ? content : content[..256];
        var newline = head.IndexOf('\n');
        if (newline <= 0) return (sender, content);
        var prefix = head[..newline].TrimEnd('\r');
        if (!prefix.EndsWith(':')) return (sender, content);
        var maybe = prefix[..^1];
        if (!SenderIdPattern().IsMatch(maybe)) return (sender, content);
        var known = maybe == sender || _contacts.ContainsKey(maybe) || id2Name.ContainsValue(maybe)
            || (_roomMembers.TryGetValue(session, out var members) && members.Contains(maybe));
        var alias = sender.Length > 0 && _contacts.TryGetValue(sender, out var contact)
            ? StringOf(contact, "alias") : null;
        var recognizable = sender.Length == 0 && (maybe.StartsWith("wxid_") || maybe.EndsWith("@openim"));
        if (known || maybe == alias || recognizable)
            return (sender.Length == 0 ? maybe : sender, content[(newline + 1)..]);
        return (sender, content);
    }

    /// <summary>Decode the message body: ZSTD blob, plain UTF-8 or container-wrapped text.</summary>
    public static string DecodeContent(byte[]? messageContent, byte[]? compressContent)
    {
        foreach (var blob in new[] { messageContent, compressContent })
        {
            if (blob is null) continue;
            var text = blob.AsSpan().StartsWith(ZstdMagic) ? DecompressZstd(blob) : null;
            if (text is { Length: > 0 }) return text;
        }
        foreach (var blob in new[] { compressContent, messageContent })
        {
            var text = ToText(blob);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return "";
    }

    internal static string? DecompressZstd(byte[] blob)
    {
        try
        {
            using var decompressor = new Decompressor();
            return Encoding.UTF8.GetString(decompressor.Unwrap(blob, 4 * 1024 * 1024));
        }
        catch (ZstdException) { return null; }
        catch (OutOfMemoryException) { return null; }
    }

    /// <summary>UTF-8 decode with the “short container header + plaintext + \x01\x00 tail” cleanup.</summary>
    internal static string ToText(byte[]? blob)
    {
        if (blob is null) return "";
        var text = Encoding.UTF8.GetString(blob);
        var start = text.IndexOf('<');
        if (start is > 0 and <= 24 && start < text.Length) text = text[start..];
        var end = text.IndexOf("\u0001\u0000", StringComparison.Ordinal);
        if (end > 0) text = text[..end];
        return text;
    }

    private static (string Kind, string Text) Describe(int type, string content) => type switch
    {
        1 => ("text", content),
        3 => ("image", "[图片]"),
        43 or 62 => ("video", "[视频]"),
        34 => ("voice", "[语音]"),
        47 or 11000 => ("emoji", "[动画表情]"),
        42 => ("card", "[名片]"),
        48 => ("location", "[位置]"),
        50 => ("voip", "[音视频通话]"),
        10000 or 10002 => ("system", content),
        _ => ("other", ""),
    };

    public void Dispose()
    {
        foreach (var connection in _conns.Values)
        {
            try { connection.Dispose(); }
            catch (SqliteException) { }
        }
        _conns.Clear();
        _tables.Clear();
        _msgDbs.Clear();
        _mediaDbs.Clear();
    }
}
