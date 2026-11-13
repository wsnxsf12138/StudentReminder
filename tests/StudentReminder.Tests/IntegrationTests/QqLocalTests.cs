using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using StudentReminder.QqLocal;

namespace StudentReminder.Tests.IntegrationTests;

/// <summary>
/// Hermetic tests for the QQ NT local reading module.
///
/// Fixture note: real QQ NT databases prepend a 1024-byte custom header before the
/// SQLCipher stream and reserve 36 tail bytes per page (IV 16 + HMAC-SHA1 20). Plain
/// SQLite has no reserved bytes, so a synthetic round trip zeroes the page tail and
/// PRAGMA quick_check would report corruption that cannot happen with real data. Decrypt
/// correctness is therefore asserted byte-for-byte, and the reader pipeline is tested
/// directly against the plain database. No real QQ data is involved.
/// </summary>
public sealed class QqLocalTests : IDisposable
{
    private const int ReserveSz = QqConstants.ReserveSz;   // 48: IV(16) + HMAC-SHA1(20) + 12 padding
    private const int IvOffset = 4096 - ReserveSz;         // 4048
    private const int HmacOffset = 4096 - ReserveSz + QqConstants.IvSz; // 4064
    private const int BodyEnd = 4096 - ReserveSz;          // 4048

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "qqlocal-tests-" + Guid.NewGuid().ToString("N"));

    private string WorkPath(string name) { Directory.CreateDirectory(_workDir); return Path.Combine(_workDir, name); }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private static byte[] RandomKey()
    {
        while (true)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            if (new HashSet<byte>(key).Count >= 15 && key.Any(b => b != 0) && key.Any(b => b != 0xFF))
                return key;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] PbVarint(ulong value)
    {
        var list = new List<byte>();
        do
        {
            list.Add((byte)((value & 0x7FUL) | (value >= 0x80UL ? 0x80UL : 0UL)));
            value >>= 7;
        } while (value > 0);
        return list.ToArray();
    }

    private static byte[] PbField(int number, byte[] payload) =>
        [.. PbVarint(((ulong)number << 3) | 2), .. PbVarint((ulong)payload.Length), .. payload];

    private static byte[] PbText(int number, string text) => PbField(number, Encoding.UTF8.GetBytes(text));

    /// <summary>Outer MsgBody with one nested MsgContent carrying a 45101 text segment.</summary>
    private static byte[] TextBody(string text) => PbField(1, PbText(45101, text));

    /// <summary>Encrypt a plain SQLite file with the QQ NT SQLCipher layout (1024-byte header).</summary>
    private static (byte[] EncKey, string SaltHex) EncryptDatabase(string plainPath, string outputPath)
    {
        var plain = File.ReadAllBytes(plainPath);
        var pages = (plain.Length + 4095) / 4096;
        var padded = new byte[pages * 4096];
        Array.Copy(plain, padded, plain.Length);
        var salt = RandomNumberGenerator.GetBytes(16);
        var encKey = RandomKey();
        using var output = File.Create(outputPath);
        output.Write(new byte[1024]); // QQ custom header
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var page = new byte[4096];
            Array.Copy(padded, (pgno - 1) * 4096L, page, 0, 4096);
            var body = pgno == 1 ? page[16..BodyEnd] : page[..BodyEnd];
            using var aes = Aes.Create();
            aes.Key = encKey;
            var iv = RandomNumberGenerator.GetBytes(16);
            var encrypted = aes.EncryptCbc(body, iv, PaddingMode.None);
            var result = new byte[4096];
            if (pgno == 1) { salt.CopyTo(result, 0); encrypted.CopyTo(result, 16); }
            else encrypted.CopyTo(result, 0);
            iv.CopyTo(result, IvOffset);
            if (pgno == 1) QqCrypto.HmacForPage1(encKey, result).CopyTo(result, HmacOffset);
            output.Write(result);
        }
        return (encKey, Convert.ToHexString(salt).ToLowerInvariant());
    }

    /// <summary>Expected decryptor output: page bodies preserved, 36-byte tails zeroed.</summary>
    private static byte[] ExpectedDecrypted(string plainPath)
    {
        var plain = File.ReadAllBytes(plainPath);
        var pages = (plain.Length + 4095) / 4096;
        var expected = new byte[pages * 4096];
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var offset = (pgno - 1) * 4096;
            var length = Math.Min(4096, plain.Length - offset);
            Array.Copy(plain, offset, expected, offset, Math.Min(length, BodyEnd));
        }
        return expected;
    }

    [Fact]
    public void Crypto_VerifyAndDecrypt_RoundTrip()
    {
        var plainPath = WorkPath("plain-roundtrip.db");
        using (var connection = new SqliteConnection($"Data Source={plainPath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table demo (value text); insert into demo values ('你好 QQ');");
        }
        var (encKey, saltHex) = EncryptDatabase(plainPath, WorkPath("enc.db"));
        var encBytes = File.ReadAllBytes(WorkPath("enc.db"));
        Assert.True(encBytes.Length >= 1024 + 4096);
        Assert.False(encBytes.AsSpan(1024).StartsWith("SQLite format 3"u8));
        Assert.Equal(saltHex, Convert.ToHexString(encBytes, 1024, 16).ToLowerInvariant());

        Assert.True(QqCrypto.VerifyEncKey(encKey, encBytes.AsSpan(1024, 4096)));
        Assert.False(QqCrypto.VerifyEncKey(RandomKey(), encBytes.AsSpan(1024, 4096)));

        QqDbDecryptor.DecryptFile(WorkPath("enc.db"), WorkPath("dec.db"), encKey);
        Assert.Equal(ExpectedDecrypted(plainPath), File.ReadAllBytes(WorkPath("dec.db")));
    }

    [Fact]
    public void Decryptor_WrongKey_Rejected()
    {
        var plainPath = WorkPath("plain-wrong.db");
        using (var connection = new SqliteConnection($"Data Source={plainPath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table demo (value text)");
        }
        EncryptDatabase(plainPath, WorkPath("enc-wrong.db"));
        Assert.Throws<QqDecryptException>(() =>
            QqDbDecryptor.DecryptFile(WorkPath("enc-wrong.db"), WorkPath("dec-wrong.db"), RandomKey()));
    }

    [Fact]
    public void Decryptor_MergesCommittedWalFrames()
    {
        var original = WorkPath("wal-plain-v1.db");
        var modified = WorkPath("wal-plain-v2.db");
        using (var connection = new SqliteConnection($"Data Source={original};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table demo (value text); insert into demo values ('v1');");
        }
        File.Copy(original, modified);
        using (var connection = new SqliteConnection($"Data Source={modified};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "insert into demo values ('v2');");
        }
        var (encKey, _) = EncryptDatabase(original, WorkPath("wal-enc.db"));
        BuildWal(File.ReadAllBytes(modified), WorkPath("wal-enc.db-wal"), encKey);
        QqDbDecryptor.DecryptFile(WorkPath("wal-enc.db"), WorkPath("wal-dec.db"), encKey, applyWal: true);
        Assert.Equal(ExpectedDecrypted(modified), File.ReadAllBytes(WorkPath("wal-dec.db")));
    }

    [Fact]
    public void Decryptor_IgnoresStaleWalGeneration()
    {
        var original = WorkPath("stale-plain.db");
        using (var connection = new SqliteConnection($"Data Source={original};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table demo (value text); insert into demo values ('v1');");
        }
        var modified = WorkPath("stale-plain-v2.db");
        File.Copy(original, modified);
        using (var connection = new SqliteConnection($"Data Source={modified};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "insert into demo values ('v2');");
        }
        var (encKey, _) = EncryptDatabase(original, WorkPath("stale-enc.db"));
        BuildWal(File.ReadAllBytes(modified), WorkPath("stale-enc.db-wal"), encKey,
            headerSalt1: 0x12345678u, frameSalt1: 0xDEADBEEFu);
        QqDbDecryptor.DecryptFile(WorkPath("stale-enc.db"), WorkPath("stale-dec.db"), encKey, applyWal: true);
        Assert.Equal(ExpectedDecrypted(original), File.ReadAllBytes(WorkPath("stale-dec.db")));
    }

    /// <summary>Build an encrypted WAL whose frames reproduce every page of the modified database.</summary>
    private static void BuildWal(byte[] modifiedPages, string walPath, byte[] encKey,
        uint headerSalt1 = 0x12345678u, uint frameSalt1 = 0x12345678u)
    {
        var pages = modifiedPages.Length / 4096;
        if (modifiedPages.Length % 4096 != 0) pages++;
        var padded = new byte[pages * 4096];
        Array.Copy(modifiedPages, padded, modifiedPages.Length);
        var salt2 = 0x9ABCDEF0u;
        using var wal = File.Create(walPath);
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header, QqConstants.WalMagic1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3007000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)QqConstants.PageSz);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), headerSalt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), salt2);
        wal.Write(header);
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var page = new byte[4096];
            Array.Copy(padded, (pgno - 1) * 4096L, page, 0, 4096);
            var body = pgno == 1 ? page[16..BodyEnd] : page[..BodyEnd];
            using var aes = Aes.Create();
            aes.Key = encKey;
            var iv = RandomNumberGenerator.GetBytes(16);
            var encrypted = aes.EncryptCbc(body, iv, PaddingMode.None);
            var frame = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)pgno);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), pgno == pages ? (uint)pages : 0);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), frameSalt1);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12), salt2);
            wal.Write(frame);
            var encryptedPage = new byte[4096];
            if (pgno == 1) encrypted.CopyTo(encryptedPage, 16);
            else encrypted.CopyTo(encryptedPage, 0);
            iv.CopyTo(encryptedPage, IvOffset);
            wal.Write(encryptedPage);
        }
    }

    [Fact]
    public void KeyExtractor_CollectAndTryKey()
    {
        var plainPath = WorkPath("plain-key.db");
        using (var connection = new SqliteConnection($"Data Source={plainPath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table demo (value text)");
        }
        var storage = WorkPath("storage");
        Directory.CreateDirectory(storage);
        var (encKey, saltHex) = EncryptDatabase(plainPath, Path.Combine(storage, "nt_msg.db"));

        var extractor = new QqKeyExtractor(storage);
        Assert.Equal(1, extractor.Collect());
        Assert.Equal(1, extractor.TryKey(Convert.ToHexString(encKey).ToLowerInvariant(), saltHex));
        Assert.Single(extractor.Keys);
        Assert.Equal(0, extractor.TryKey(Convert.ToHexString(RandomKey()).ToLowerInvariant(), saltHex));
    }

    [Fact]
    public void ProtoReader_ExtractsNestedText()
    {
        Assert.Equal("你好", ProtoReader.ExtractText(TextBody("你好")));
        Assert.Equal("第一段", ProtoReader.ExtractText(PbField(1, PbText(45101, "第一段"))));
        // Concatenation of multiple segments through separate nested messages.
        var twoSegments = new byte[][] { PbField(1, PbText(45101, "甲")), PbField(1, PbText(45101, "乙")) };
        Assert.Equal("甲乙", ProtoReader.ExtractText([.. twoSegments[0], .. twoSegments[1]]));
    }

    [Fact]
    public void ProtoReader_NonText_ReturnsEmpty()
    {
        var empty = Array.Empty<byte>();
        Assert.Equal("", ProtoReader.ExtractText(empty));
        var imageOnly = PbField(1, PbField(2, new byte[] { 0x01, 0x02, 0x03 }));
        Assert.Equal("", ProtoReader.ExtractText(imageOnly));
    }

    private void WriteMessageDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, """
            create table c2c_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                "40013" integer, "40021" text, "40030" text, "40800" blob);
            create table group_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                "40013" integer, "40021" text, "40030" text, "40800" blob);
            """);
        Insert(connection, "group_msg_table", 1, 1762000000, "u_teacher", "10001", 0, "123456789", TextBody("各位同学，下周三 18:00 前提交实验报告。"));
        Insert(connection, "group_msg_table", 2, 1762000100, "u_me", "10002", 1, "123456789", TextBody("收到"));
        Insert(connection, "c2c_msg_table", 1, 1762000200, "u_friend", "10003", 0, "u_friend", TextBody("明天见"));
    }

    private static void Insert(SqliteConnection connection, string table, long msgId, long createTime,
        string senderUid, string senderQq, long direction, string peer, byte[] body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"insert into \"{table}\" (\"40001\",\"40050\",\"40020\",\"40033\",\"40013\",\"40021\",\"40030\",\"40800\")" +
            " values ($a,$b,$c,$d,$e,$f,$g,$h)";
        command.Parameters.AddWithValue("$a", msgId);
        command.Parameters.AddWithValue("$b", createTime);
        command.Parameters.AddWithValue("$c", senderUid);
        command.Parameters.AddWithValue("$d", senderQq);
        command.Parameters.AddWithValue("$e", direction);
        command.Parameters.AddWithValue("$f", peer);
        command.Parameters.AddWithValue("$g", peer);
        command.Parameters.AddWithValue("$h", body);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Reader_Pipeline_ReadsSyntheticAccount()
    {
        var dir = WorkPath("decrypted");
        Directory.CreateDirectory(dir);
        WriteMessageDatabase(Path.Combine(dir, "nt_msg.db"));
        using var reader = new QqMessageReader(dir);

        var conversations = reader.ListConversations();
        var group = Assert.Single(conversations, c => c.Id == "123456789");
        Assert.Equal("group", group.Type);
        Assert.Equal(2, group.Count);

        Assert.True(reader.IsGroup("123456789"));
        Assert.False(reader.IsGroup("u_friend"));

        var groupMessages = reader.ReadMessages("123456789", group: true);
        Assert.Equal(2, groupMessages.Count);
        Assert.Equal("各位同学，下周三 18:00 前提交实验报告。", groupMessages[0].Text);
        Assert.Equal("u_teacher", groupMessages[0].Sender);
        Assert.False(groupMessages[0].SentByMe);
        Assert.Equal("收到", groupMessages[1].Text);
        Assert.Equal("我", groupMessages[1].Sender);
        Assert.True(groupMessages[1].SentByMe);

        var privateMessages = reader.ReadMessages("u_friend", group: false);
        Assert.Single(privateMessages);
        Assert.Equal("明天见", privateMessages[0].Text);
    }

    [Theory]
    [InlineData("group_list")]
    [InlineData("group_detail_info_ver1")]
    public void Reader_ReadsGroupNamesFromQqNtNumericColumns(string table)
    {
        var dir = WorkPath("decrypted-group-names-" + table);
        Directory.CreateDirectory(dir);
        WriteMessageDatabase(Path.Combine(dir, "nt_msg.db"));
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "group_info.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, $"create table {table} (\"60001\" integer primary key, \"60007\" text)");
            Execute(connection, $"insert into {table} values (123456789, '课程＆实验群（2026）')");
        }

        using var reader = new QqMessageReader(dir);
        var group = Assert.Single(reader.ListConversations(), c => c.Id == "123456789");
        Assert.Equal("课程＆实验群（2026）", group.Name);
        Assert.Equal("课程＆实验群（2026）", reader.GetConversationName("123456789", group: true));
    }

    [Fact]
    public void Reader_ReadsBuddyNamesFromProfileInfoV6()
    {
        var dir = WorkPath("decrypted-buddy-names");
        Directory.CreateDirectory(dir);
        WriteMessageDatabase(Path.Combine(dir, "nt_msg.db"));
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "profile_info.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table profile_info_v6 (\"1000\" text primary key, \"20002\" text, \"20009\" text)");
            Execute(connection, "insert into profile_info_v6 values ('u_friend', '昵称', '备注')");
            Execute(connection, "insert into profile_info_v6 values ('u_nickonly', '只有昵称', null)");
        }

        using var reader = new QqMessageReader(dir);

        // Remark is preferred over nickname.
        Assert.Equal("备注", reader.GetConversationName("u_friend", group: false));
        // Nickname is used when no remark is set.
        Assert.Equal("只有昵称", reader.GetConversationName("u_nickonly", group: false));
        // Unknown uid falls back to the id itself.
        Assert.Equal("u_unknown", reader.GetConversationName("u_unknown", group: false));

        var c2c = Assert.Single(reader.ListConversations(), c => c.Id == "u_friend");
        Assert.Equal("备注", c2c.Name);
    }

    [Fact]
    public void Reader_ResolvesSenderNamesFromBuddyAndGroupMemberTables()
    {
        var dir = WorkPath("decrypted-sender-names");
        Directory.CreateDirectory(dir);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "nt_msg.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, """
                create table c2c_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                    "40013" integer, "40021" text, "40030" text, "40800" blob);
                create table group_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                    "40013" integer, "40021" text, "40030" text, "40800" blob);
                """);
            // 群成员发言（好友，有群名片）。
            Insert(connection, "group_msg_table", 1, 1762000000, "u_friend", "10001", 0, "123456789", TextBody("群成员发言"));
            // 我发出的群消息，direction=2 也应是「我」。
            Insert(connection, "group_msg_table", 2, 1762000100, "u_me", "10002", 2, "123456789", TextBody("我回复"));
            // 系统消息，sender 为空。
            Insert(connection, "group_msg_table", 3, 1762000200, "", "0", 3, "123456789", TextBody("某成员戳了戳你"));
            // 私聊消息，对方 sender 为好友，应显示备注。
            Insert(connection, "c2c_msg_table", 1, 1762000300, "u_friend", "10003", 0, "u_friend", TextBody("私聊"));
        }
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "profile_info.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table profile_info_v6 (\"1000\" text primary key, \"20002\" text, \"20009\" text)");
            Execute(connection, "insert into profile_info_v6 values ('u_friend', '昵称', '备注')");
        }
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "group_info.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "create table group_member3 (\"60001\" integer, \"1000\" text, \"64003\" text, \"20002\" text, \"64029\" text)");
            Execute(connection, "insert into group_member3 values (123456789, 'u_friend', '群名片', '昵称', '备注')");
        }

        using var reader = new QqMessageReader(dir);

        var groupMessages = reader.ReadMessages("123456789", group: true);
        Assert.Equal(3, groupMessages.Count);
        Assert.Equal("群名片", groupMessages[0].Sender);   // 群名片优先于好友备注
        Assert.False(groupMessages[0].SentByMe);
        Assert.Equal("我", groupMessages[1].Sender);        // direction=2 也识别为我发出
        Assert.True(groupMessages[1].SentByMe);
        Assert.Equal("系统", groupMessages[2].Sender);      // sender 为空的系统消息
        Assert.False(groupMessages[2].SentByMe);

        var privateMessages = reader.ReadMessages("u_friend", group: false);
        Assert.Single(privateMessages);
        Assert.Equal("备注", privateMessages[0].Sender);    // 私聊 sender 用好友备注
        Assert.False(privateMessages[0].SentByMe);
    }

    [Fact]
    public void Reader_IgnoresPlaceholderPeerZero()
    {
        var dir = WorkPath("decrypted-peer-zero");
        Directory.CreateDirectory(dir);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "nt_msg.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, """
                create table c2c_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                    "40013" integer, "40021" text, "40030" text, "40800" blob);
                create table group_msg_table ("40001" integer, "40050" integer, "40020" text, "40033" text,
                    "40013" integer, "40021" text, "40030" text, "40800" blob);
                """);
            Insert(connection, "group_msg_table", 1, 1762000000, "u_me", "10002", 1, "123456789", TextBody("正常群消息"));
            Insert(connection, "group_msg_table", 2, 1762000100, "0", "0", 0, "0", Array.Empty<byte>());
        }

        using var reader = new QqMessageReader(dir);
        var conversations = reader.ListConversations();
        Assert.DoesNotContain(conversations, c => c.Id == "0");
        Assert.Contains(conversations, c => c.Id == "123456789");
    }

    [Fact]
    public void Reader_ReadMessagesAfter_ReturnsOnlyNewerMessages()
    {
        var dir = WorkPath("decrypted-after");
        Directory.CreateDirectory(dir);
        WriteMessageDatabase(Path.Combine(dir, "nt_msg.db"));
        using var reader = new QqMessageReader(dir);

        var after = reader.ReadMessagesAfter("123456789", group: true, lastRowid: 1);
        Assert.Single(after);
        Assert.Equal(2, after[0].MsgId);

        Assert.Empty(reader.ReadMessagesAfter("123456789", group: true, lastRowid: 2));
    }
}
