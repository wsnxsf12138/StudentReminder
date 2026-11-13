using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using StudentReminder.WxLocal;
using ZstdSharp;

namespace StudentReminder.Tests.IntegrationTests;

/// <summary>
/// Hermetic tests for the WeChat-4.x local reading module.
///
/// Note on fixtures: real WeChat databases run SQLite with 80 reserved bytes per page
/// (SQLCipher layout), so their cell content never touches the page tail and the
/// decrypted files pass PRAGMA quick_check. Plain SQLite created from Microsoft.Data.Sqlite
/// has no reserved bytes, so an encrypted/decrypted round trip zeroes live cell data at
/// the page tail and quick_check would report corruption that cannot happen with real data.
/// Therefore decrypt correctness is asserted byte-for-byte against the expected layout
/// (page body preserved, 80-byte tail zeroed, page-1 header restored), and the reader
/// pipeline is tested directly against the plain databases.
/// No real WeChat data is involved.
/// </summary>
public sealed class WxLocalTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "wxlocal-tests-" + Guid.NewGuid().ToString("N"));

    private string WorkPath(string name) { Directory.CreateDirectory(_workDir); return Path.Combine(_workDir, name); }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private const string Group = "12345678901@chatroom";
    private static string MsgTable => WxMessageReader.MsgTableFor(Group);
    private const string Teacher = "wxid_teacher001";
    private const string Me = "wxid_me00000001";

    private static byte[] RandomKey()
    {
        while (true)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            if (new HashSet<byte>(key).Count >= 15 && key.Any(b => b != 0) && key.Any(b => b != 0xFF))
                return key;
        }
    }

    /// <summary>Build the plain (unencrypted) database set of one synthetic account.</summary>
    private string BuildPlainDatabases()
    {
        var plainDir = Path.Combine(_workDir, "plain");
        Directory.CreateDirectory(plainDir);

        using (var connection = new SqliteConnection($"Data Source={Path.Combine(plainDir, "contact.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, """
                create table contact (id text primary key, username text, nick_name text,
                    remark text, alias text, verify_flag integer);
                create table chat_room (id integer primary key, username text);
                create table chatroom_member (room_id integer, member_id text);
                insert into contact values ('1', 'wxid_teacher001', '张老师', '', 'teacher', 0);
                insert into contact values ('2', 'wxid_me00000001', '我', '', '', 0);
                insert into contact values ('3', '12345678901@chatroom', '高数课程群', '', '', 0);
                insert into chat_room values (10, '12345678901@chatroom');
                insert into chatroom_member values (10, '1');
                insert into chatroom_member values (10, '2');
                """);
        }

        using (var connection = new SqliteConnection($"Data Source={Path.Combine(plainDir, "session.db")};Pooling=False"))
        {
            connection.Open();
            Execute(connection, $"""
                create table SessionTable (username text, summary text,
                    last_timestamp integer, sort_timestamp integer, unread_count integer);
                insert into SessionTable values ('{Group}', '下周三交实验报告',
                    1762000000, 1762000000, 1);
                """);
        }

        WriteMessageDatabase(Path.Combine(plainDir, "message_0.db"));
        return plainDir;
    }

    private static void WriteMessageDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, "create table Name2Id (rowid integer primary key, user_name text)");
        Execute(connection, $"insert into Name2Id (user_name) values ('{Teacher}'), ('{Me}')");
        Execute(connection, $"""
            create table "{MsgTable}" (local_id integer primary key, server_id integer,
                local_type integer, sort_seq integer, real_sender_id integer,
                create_time integer, message_content text, compress_content blob);
            """);
        InsertMessage(connection, 1, 1001, 1, 1762000000, 1, $"{Teacher}:\n各位同学，下周三 18:00 前提交实验报告。", null);
        InsertMessage(connection, 2, 1002, 1, 1762000100, 2, "", Compress("记得带上次的实验数据"));
        InsertMessage(connection, 3, 1003, 3, 1762000200, 1, "<msg><img aeskey=\"k\" cdnthumbaeskey=\"t\"/></msg>", null);
        InsertMessage(connection, 4, 1004, (1L << 32) | 1, 1762000300, 1, "", null);
    }

    private static void InsertMessage(SqliteConnection connection, long localId, long serverId,
        long localType, long createTime, long senderRowid, string content, byte[]? compressed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"insert into \"{MsgTable}\" values ($local, $server, $type, $sort, $sender, $time, $content, $compressed)";
        command.Parameters.AddWithValue("$local", localId);
        command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$type", localType);
        command.Parameters.AddWithValue("$sort", localId);
        command.Parameters.AddWithValue("$sender", senderRowid);
        command.Parameters.AddWithValue("$time", createTime);
        command.Parameters.AddWithValue("$content", content.Length == 0 ? DBNull.Value : content);
        command.Parameters.AddWithValue("$compressed", compressed is null ? DBNull.Value : compressed);
        command.ExecuteNonQuery();
    }

    private static byte[] Compress(string text)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Encrypt a plain SQLite file with the WeChat 4.x / SQLCipher page layout.</summary>
    private static (byte[] EncKey, string SaltHex) EncryptDatabase(string plainPath, string outputPath)
    {
        var plain = File.ReadAllBytes(plainPath);
        var pages = (plain.Length + 4095) / 4096;
        var padded = new byte[pages * 4096];
        Array.Copy(plain, padded, plain.Length);
        var salt = RandomNumberGenerator.GetBytes(16);
        var encKey = RandomKey();
        using var output = File.Create(outputPath);
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var page = new byte[4096];
            Array.Copy(padded, (pgno - 1) * 4096L, page, 0, 4096);
            var body = pgno == 1 ? page[16..4016] : page[..4016];
            using var aes = Aes.Create();
            aes.Key = encKey;
            var iv = RandomNumberGenerator.GetBytes(16);
            var encrypted = aes.EncryptCbc(body, iv, PaddingMode.None);
            var result = new byte[4096];
            if (pgno == 1)
            {
                salt.CopyTo(result, 0);
                encrypted.CopyTo(result, 16);
            }
            else
            {
                encrypted.CopyTo(result, 0);
            }
            iv.CopyTo(result, 4016);
            if (pgno == 1)
            {
                var hmac = WxCrypto.HmacForPage1(encKey, result);
                hmac.CopyTo(result, 4032);
            }
            output.Write(result);
        }
        return (encKey, Convert.ToHexString(salt).ToLowerInvariant());
    }

    /// <summary>Expected decryptor output: page bodies preserved, 80-byte tails zeroed, page-1 header restored.</summary>
    private static byte[] ExpectedDecrypted(string plainPath)
    {
        var plain = File.ReadAllBytes(plainPath);
        var pages = (plain.Length + 4095) / 4096;
        var expected = new byte[pages * 4096];
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var offset = (pgno - 1) * 4096;
            var length = Math.Min(4096, plain.Length - offset);
            Array.Copy(plain, offset, expected, offset, Math.Min(length, 4016));
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
            Execute(connection, "create table demo (value text); insert into demo values ('hello 微信');");
        }
        var (encKey, saltHex) = EncryptDatabase(plainPath, WorkPath("enc.db"));
        var encBytes = File.ReadAllBytes(WorkPath("enc.db"));
        Assert.False(encBytes.AsSpan().StartsWith("SQLite format 3"u8));
        Assert.Equal(saltHex, Convert.ToHexString(encBytes, 0, 16).ToLowerInvariant());

        Assert.True(WxCrypto.VerifyEncKey(encKey, encBytes));
        Assert.False(WxCrypto.VerifyEncKey(RandomKey(), encBytes));

        WxDbDecryptor.DecryptFile(WorkPath("enc.db"), WorkPath("dec.db"), encKey);
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
        Assert.Throws<WxDecryptException>(() =>
            WxDbDecryptor.DecryptFile(WorkPath("enc-wrong.db"), WorkPath("dec-wrong.db"), RandomKey()));
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

        WxDbDecryptor.DecryptFile(WorkPath("wal-enc.db"), WorkPath("wal-dec.db"), encKey, applyWal: true);
        // The WAL replays every page of the modified database, so the merged result
        // must equal the modified database under the reserved-80 layout.
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
        // Header announces salt 0x12345678 while the frames carry a foreign salt:
        // the merger must skip the whole stale generation.
        BuildWal(File.ReadAllBytes(modified), WorkPath("stale-enc.db-wal"), encKey,
            headerSalt1: 0x12345678u, frameSalt1: 0xDEADBEEFu);
        WxDbDecryptor.DecryptFile(WorkPath("stale-enc.db"), WorkPath("stale-dec.db"), encKey, applyWal: true);
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
        BinaryPrimitives.WriteUInt32BigEndian(header, WxConstants.WalMagic1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3007000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)WxConstants.PageSz);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), headerSalt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), salt2);
        wal.Write(header);
        for (var pgno = 1; pgno <= pages; pgno++)
        {
            var page = new byte[4096];
            Array.Copy(padded, (pgno - 1) * 4096L, page, 0, 4096);
            var body = pgno == 1 ? page[16..4016] : page[..4016];
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
            iv.CopyTo(encryptedPage, 4016);
            wal.Write(encryptedPage);
        }
    }

    [Fact]
    public void KeyExtractor_RecoversCandidatesFromXorMaskedBlob()
    {
        var key = RandomKey();
        var salt = RandomNumberGenerator.GetBytes(16);
        var literal = "x'" + Convert.ToHexString(key).ToLowerInvariant()
                            + Convert.ToHexString(salt).ToLowerInvariant() + "'";
        var blob = Encoding.ASCII.GetBytes(literal)
            .Select((b, i) => (byte)(b ^ WxConstants.CipherXorMask[i % WxConstants.CipherXorMask.Length])).ToArray();
        var candidates = WxKeyExtractor.CandidatesFromConfigBlob(blob);
        Assert.Contains(candidates, candidate => candidate.KeyHex == Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void Reader_Pipeline_ReadsSyntheticAccount()
    {
        var plainDir = BuildPlainDatabases();
        using var reader = new WxMessageReader(plainDir, Me);

        var sessions = reader.ListSessions();
        var session = Assert.Single(sessions, s => s.Username == Group);
        Assert.Equal("group", session.Category);
        Assert.Contains("实验报告", session.Summary);
        Assert.True(session.IsGroup);

        var messages = reader.ReadMessages(Group);
        Assert.Equal(4, messages.Count);

        var first = messages[0];
        Assert.Equal(1, first.Type);
        Assert.Equal("text", first.Kind);
        Assert.Equal(Teacher, first.SenderWxid);
        Assert.Equal("张老师", first.Sender);
        // group body prefix "wxid_teacher001:\n" must be stripped
        Assert.Equal("各位同学，下周三 18:00 前提交实验报告。", first.Text);

        var second = messages[1];
        Assert.Equal("记得带上次的实验数据", second.Text);
        Assert.Equal(Me, second.SenderWxid);

        var image = messages[2];
        Assert.Equal(3, image.Type);
        Assert.Equal("image", image.Kind);

        var composed = messages[3];
        Assert.Equal(1, composed.Type); // (1 << 32) | 1 normalizes to base type 1

        var recent = reader.ReadMessages(Group, startUnix: 1762000150);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void Reader_ReadMessagesAfter_ReturnsOnlyNewerMessages()
    {
        var plainDir = BuildPlainDatabases();
        using var reader = new WxMessageReader(plainDir, Me);

        var after = reader.ReadMessagesAfter(Group, 2, 2);
        Assert.Equal(2, after.Count);
        Assert.Equal(3, after[0].LocalId);
        Assert.Equal(3, after[0].SortSeq);
        Assert.Equal(4, after[1].LocalId);

        Assert.Empty(reader.ReadMessagesAfter(Group, 4, 4));
    }

    [Fact]
    public void DecryptAll_ByteFidelityPerDatabase()
    {
        var plainDir = BuildPlainDatabases();
        var storage = WorkPath("storage");
        var encDir = WorkPath("enc");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(encDir);
        var payload = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var plainPath in Directory.EnumerateFiles(plainDir, "*.db"))
        {
            var name = Path.GetFileName(plainPath);
            var (key, salt) = EncryptDatabase(plainPath, Path.Combine(encDir, name));
            File.Copy(Path.Combine(encDir, name), Path.Combine(storage, name));
            payload[name] = new Dictionary<string, string>
            {
                ["enc_key"] = Convert.ToHexString(key).ToLowerInvariant(),
                ["salt"] = salt,
            };
        }
        var cacheDir = WorkPath("cache");
        _ = WxDbDecryptor.DecryptAll(payload, storage, cacheDir);
        // Real WeChat databases reserve 80 tail bytes so quick_check passes; synthetic
        // plain databases do not, so the run above reports quick_check failures while the
        // decrypted bytes must still be byte-for-byte correct.
        foreach (var plainPath in Directory.EnumerateFiles(plainDir, "*.db"))
        {
            var name = Path.GetFileName(plainPath);
            Assert.Equal(ExpectedDecrypted(plainPath), File.ReadAllBytes(Path.Combine(cacheDir, name)));
        }
    }

    [Fact]
    public void Reader_NormalizeLocalType_HandlesCompositeValues()
    {
        Assert.Equal(1, WxMessageReader.NormalizeLocalType(1));
        Assert.Equal(1, WxMessageReader.NormalizeLocalType((1L << 32) | 1));
        Assert.Equal(57, WxMessageReader.NormalizeLocalType((57L << 32) | 57));
        Assert.Equal(10000, WxMessageReader.NormalizeLocalType(10000));
    }

    [Fact]
    public void Reader_DecodeContent_PrefersZstdAndFallsBackToPlain()
    {
        var zstd = Compress("压缩正文");
        Assert.Equal("压缩正文", WxMessageReader.DecodeContent(zstd, null));
        Assert.Equal("普通正文", WxMessageReader.DecodeContent(Encoding.UTF8.GetBytes("普通正文"), null));
        Assert.Equal("压缩优先", WxMessageReader.DecodeContent(Encoding.UTF8.GetBytes("plain"), Compress("压缩优先")));
        Assert.Equal("", WxMessageReader.DecodeContent(null, null));
    }

    [Fact]
    public void Paths_SplitWxid_TrimsFourHexSuffix()
    {
        Assert.Equal("wxid_abc123", WxPaths.SplitWxid("wxid_abc123_dc5e"));
        Assert.Equal("plainname", WxPaths.SplitWxid("plainname"));
    }

    [Fact]
    public void MsgTableFor_IsMd5OfUsername()
    {
        var table = WxMessageReader.MsgTableFor(Group);
        Assert.StartsWith("Msg_", table);
        Assert.Equal(36, table.Length);
        Assert.Matches("^[0-9a-f]{32}$", table[4..]);
    }
}
