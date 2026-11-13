using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace StudentReminder.QqLocal;

public sealed record QqDecryptStats(int Ok, int Failed, int Skipped, string Log);

public sealed class QqDecryptException(string message) : Exception(message);

/// <summary>
/// Decrypts QQ NT SQLCipher databases into plain SQLite files. Each source file begins
/// with a 1024-byte custom header before the SQLCipher stream. Snapshots the live files,
/// decrypts page by page, then replays committed frames from the encrypted WAL.
/// </summary>
public static class QqDbDecryptor
{
    /// <summary>Decrypt one database (and its WAL) from a snapshot copy.</summary>
    public static (int Pages, int WalFrames) DecryptFile(string src, string dst, byte[] encKey, bool applyWal = true)
    {
        using var input = File.OpenRead(src);
        if (input.Length < QqConstants.HeaderSz + QqConstants.PageSz)
            throw new QqDecryptException($"文件过小：{src}");
        var page1 = new byte[QqConstants.PageSz];
        input.Position = QqConstants.HeaderSz;
        if (input.Read(page1) < QqConstants.PageSz) throw new QqDecryptException($"读取失败：{src}");
        if (!QqCrypto.VerifyEncKey(encKey, page1))
            throw new QqDecryptException($"密钥校验失败（HMAC 不匹配）：{src}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        var bodyLength = input.Length - QqConstants.HeaderSz;
        var total = (int)((bodyLength + QqConstants.PageSz - 1) / QqConstants.PageSz);
        using (var output = File.Create(dst))
        {
            input.Position = QqConstants.HeaderSz;
            var page = new byte[QqConstants.PageSz];
            for (var pgno = 1; pgno <= total; pgno++)
            {
                if (input.Read(page) < QqConstants.PageSz) break;
                output.Write(QqCrypto.DecryptPage(encKey, page, pgno));
            }
        }
        var walFrames = 0;
        if (applyWal)
        {
            var wal = src + "-wal";
            if (File.Exists(wal) && new FileInfo(wal).Length > QqConstants.WalHdrSz)
                walFrames = MergeWal(dst, wal, encKey);
        }
        return (total, walFrames);
    }

    /// <summary>Apply committed frames of an encrypted WAL onto the decrypted database.</summary>
    public static int MergeWal(string dbPath, string walPath, byte[] encKey)
    {
        using var wal = File.OpenRead(walPath);
        var header = ReadWalHeader(wal, out var salt1, out var salt2);
        if (header.Length == 0) return 0;

        long lastCommit = 0;
        uint committedPages = 0;
        var frameHeader = new byte[QqConstants.WalFrameHdrSz];
        var frameSize = QqConstants.WalFrameHdrSz + QqConstants.PageSz;
        while (wal.Position + frameSize <= wal.Length)
        {
            if (wal.Read(frameHeader) < QqConstants.WalFrameHdrSz) break;
            var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
            var commit = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
            var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
            wal.Seek(QqConstants.PageSz, SeekOrigin.Current);
            if (pgno == 0) break;
            if (frameSalt1 == salt1 && frameSalt2 == salt2 && commit != 0)
            {
                lastCommit = wal.Position;
                committedPages = commit;
            }
        }
        if (lastCommit == 0) return 0;

        wal.Position = QqConstants.WalHdrSz;
        var applied = 0;
        using (var database = File.Open(dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            var page = new byte[QqConstants.PageSz];
            while (wal.Position < lastCommit)
            {
                if (wal.Read(frameHeader) < QqConstants.WalFrameHdrSz
                    || wal.Read(page) < QqConstants.PageSz)
                    throw new QqDecryptException("WAL 快照不完整，请重试");
                var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
                var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
                var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
                if (frameSalt1 != salt1 || frameSalt2 != salt2 || pgno > committedPages) continue;
                database.Position = (pgno - 1) * (long)QqConstants.PageSz;
                database.Write(QqCrypto.DecryptPage(encKey, page, (int)pgno));
                applied++;
            }
            database.SetLength(committedPages * (long)QqConstants.PageSz);
        }
        return applied;
    }

    /// <summary>Incrementally apply newly committed WAL frames onto a decrypted shadow database.</summary>
    public static QqWalDeltaResult ApplyWalDelta(string shadowDbPath, string walPath, byte[] encKey, QqWalState? state)
    {
        if (!File.Exists(walPath))
            return new(QqWalDeltaStatus.NoChange, 0, state);

        using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var header = ReadWalHeader(wal, out var salt1, out var salt2);
        if (header.Length == 0)
            return new(QqWalDeltaStatus.NoChange, 0, state);

        var start = state?.EndOffset ?? QqConstants.WalHdrSz;
        if (state is not null && (state.Salt1 != salt1 || state.Salt2 != salt2))
            return new(QqWalDeltaStatus.NeedsFullRefresh, 0, null);
        if (start > wal.Length)
            return new(QqWalDeltaStatus.NeedsFullRefresh, 0, null);

        var frameHeader = new byte[QqConstants.WalFrameHdrSz];
        var frameSize = QqConstants.WalFrameHdrSz + QqConstants.PageSz;
        long lastCommittedEnd = start;
        uint committedPages = 0;
        wal.Position = start;
        while (wal.Position + frameSize <= wal.Length)
        {
            if (wal.Read(frameHeader) < QqConstants.WalFrameHdrSz) break;
            var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
            var commit = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
            var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
            wal.Seek(QqConstants.PageSz, SeekOrigin.Current);
            if (pgno == 0) break;
            if (frameSalt1 == salt1 && frameSalt2 == salt2 && commit != 0)
            {
                lastCommittedEnd = wal.Position;
                committedPages = commit;
            }
        }

        if (committedPages == 0)
            return new(QqWalDeltaStatus.NoChange, 0, new QqWalState(salt1, salt2, start));

        var applied = 0;
        using (var database = new FileStream(shadowDbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var page = new byte[QqConstants.PageSz];
            wal.Position = start;
            while (wal.Position < lastCommittedEnd)
            {
                if (wal.Read(frameHeader) < QqConstants.WalFrameHdrSz
                    || wal.Read(page) < QqConstants.PageSz)
                    throw new QqDecryptException("WAL 快照不完整，请重试");
                var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
                var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
                var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
                if (frameSalt1 != salt1 || frameSalt2 != salt2 || pgno > committedPages) continue;
                database.Position = (pgno - 1) * (long)QqConstants.PageSz;
                database.Write(QqCrypto.DecryptPage(encKey, page, (int)pgno));
                applied++;
            }
            var committedSize = committedPages * (long)QqConstants.PageSz;
            if (committedSize > database.Length) database.SetLength(committedSize);
        }
        return new(QqWalDeltaStatus.Applied, applied, new QqWalState(salt1, salt2, lastCommittedEnd));
    }

    /// <summary>Read the WAL header, tolerating an optional 1024-byte QQ prefix.</summary>
    private static byte[] ReadWalHeader(Stream wal, out uint salt1, out uint salt2)
    {
        var header = new byte[QqConstants.WalHdrSz];
        wal.Position = 0;
        if (wal.Read(header) < QqConstants.WalHdrSz)
        {
            salt1 = salt2 = 0;
            return [];
        }
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (QqConstants.WalMagic1 or QqConstants.WalMagic2))
        {
            // Try a 1024-byte prefix some builds attach to the WAL as well.
            if (wal.Length < QqConstants.HeaderSz + QqConstants.WalHdrSz)
            {
                salt1 = salt2 = 0;
                return [];
            }
            wal.Position = QqConstants.HeaderSz;
            if (wal.Read(header) < QqConstants.WalHdrSz)
            {
                salt1 = salt2 = 0;
                return [];
            }
            magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic is not (QqConstants.WalMagic1 or QqConstants.WalMagic2))
            {
                salt1 = salt2 = 0;
                return [];
            }
        }
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (pageSize is not (0 or (uint)QqConstants.PageSz))
        {
            salt1 = salt2 = 0;
            return [];
        }
        salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        return header;
    }

    /// <summary>Copy a database (plus -wal/-shm) into a snapshot directory and return the snapshot path.</summary>
    public static string Snapshot(string src, string tempDir)
    {
        var dst = Path.Combine(tempDir, Path.GetFileName(src));
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var stale = dst + suffix;
            if (File.Exists(stale)) File.Delete(stale);
        }
        File.Copy(src, dst, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = src + suffix;
            if (File.Exists(side)) File.Copy(side, dst + suffix, overwrite: true);
        }
        return dst;
    }

    /// <summary>Sanity check that a decrypted file is a usable SQLite database.</summary>
    public static bool QuickCheck(string dbPath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check(1)";
                return command.ExecuteScalar() is "ok";
            }
            catch (SqliteException)
            {
                if (!dbPath.EndsWith("_fts.db", StringComparison.OrdinalIgnoreCase)) return false;
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "select count(*) from sqlite_master";
                    command.ExecuteScalar();
                    return true;
                }
                catch (SqliteException) { return false; }
            }
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Batch decrypt: keys maps the relative db path to enc_key/salt hex values.</summary>
    public static QqDecryptStats DecryptAll(Dictionary<string, Dictionary<string, string>> keys,
        string ntDbDir, string outDir, TextWriter? progress = null, string? snapshotRoot = null)
    {
        var log = new List<string>();
        var ok = 0;
        var failed = new List<string>();
        var skipped = 0;
        Directory.CreateDirectory(outDir);
        var tempDir = Path.Combine(snapshotRoot ?? Path.GetTempPath(), "qqlocal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (rel, info) in keys.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var src = Path.Combine(ntDbDir, rel);
                var dst = Path.Combine(outDir, rel);
                if (!File.Exists(src)) { skipped++; continue; }
                var encKey = Convert.FromHexString(info["enc_key"]);
                string? lastError = null;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
                        var snap = Snapshot(src, tempDir);
                        DecryptFile(snap, dst, encKey);
                        if (QuickCheck(dst))
                        {
                            ok++;
                            log.Add($"[ok] {rel}");
                            lastError = null;
                            break;
                        }
                        lastError = "quick_check 未通过";
                    }
                    catch (QqDecryptException error)
                    {
                        lastError = error.Message;
                        break;
                    }
                    catch (IOException error)
                    {
                        lastError = $"IO 错误：{error.Message}";
                    }
                    if (attempt < 3) Thread.Sleep(300);
                }
                if (lastError is not null)
                {
                    failed.Add(rel);
                    log.Add($"[fail] {rel}: {lastError}");
                    progress?.WriteLine($"[fail] {rel}: {lastError}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
        return new(ok, failed.Count, skipped, string.Join(Environment.NewLine, log));
    }
}
