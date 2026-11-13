using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace StudentReminder.WxLocal;

public sealed record WxDecryptStats(int Ok, int Failed, int Skipped, string Log);

public sealed class WxDecryptException(string message) : Exception(message);

/// <summary>
/// Decrypts WeChat 4.x SQLCipher databases into plain SQLite files.
/// Snapshots the live files (WeChat holds an exclusive lock), decrypts page by page,
/// then replays committed frames from the encrypted WAL so recent messages survive.
/// </summary>
public static class WxDbDecryptor
{
    /// <summary>Decrypt one database (and its WAL) from a snapshot copy.</summary>
    public static (int Pages, int WalFrames) DecryptFile(string src, string dst, byte[] encKey,
        bool applyWal = true)
    {
        using var input = File.OpenRead(src);
        if (input.Length < WxConstants.PageSz) throw new WxDecryptException($"文件过小：{src}");
        var page1 = new byte[WxConstants.PageSz];
        if (input.Read(page1) < WxConstants.PageSz) throw new WxDecryptException($"读取失败：{src}");
        if (!WxCrypto.VerifyEncKey(encKey, page1))
            throw new WxDecryptException($"密钥校验失败（HMAC 不匹配）：{src}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        var total = (int)((input.Length + WxConstants.PageSz - 1) / WxConstants.PageSz);
        using (var output = File.Create(dst))
        {
            input.Position = 0;
            var page = new byte[WxConstants.PageSz];
            for (var pgno = 1; pgno <= total; pgno++)
            {
                if (input.Read(page) < WxConstants.PageSz) break;
                output.Write(WxCrypto.DecryptPage(encKey, page, pgno));
            }
        }
        var walFrames = 0;
        if (applyWal)
        {
            var wal = src + "-wal";
            if (File.Exists(wal) && new FileInfo(wal).Length > WxConstants.WalHdrSz)
                walFrames = MergeWal(dst, wal, encKey);
        }
        return (total, walFrames);
    }

    /// <summary>Apply committed frames of an encrypted WAL onto the decrypted database.</summary>
    public static int MergeWal(string dbPath, string walPath, byte[] encKey)
    {
        using var wal = File.OpenRead(walPath);
        var header = new byte[WxConstants.WalHdrSz];
        if (wal.Read(header) < WxConstants.WalHdrSz) return 0;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (WxConstants.WalMagic1 or WxConstants.WalMagic2))
            throw new WxDecryptException("WAL 文件头无效，请重试");
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (pageSize is not (0 or (uint)WxConstants.PageSz))
            throw new WxDecryptException("WAL 页大小不受支持");
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));

        long lastCommit = 0;
        uint committedPages = 0;
        var frameHeader = new byte[WxConstants.WalFrameHdrSz];
        var frameSize = WxConstants.WalFrameHdrSz + WxConstants.PageSz;
        while (wal.Position + frameSize <= wal.Length)
        {
            if (wal.Read(frameHeader) < WxConstants.WalFrameHdrSz) break;
            var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
            var commit = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
            var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
            wal.Seek(WxConstants.PageSz, SeekOrigin.Current);
            if (pgno == 0) break;
            if (frameSalt1 == salt1 && frameSalt2 == salt2 && commit != 0)
            {
                lastCommit = wal.Position;
                committedPages = commit;
            }
        }
        if (lastCommit == 0) return 0;

        wal.Position = WxConstants.WalHdrSz;
        var applied = 0;
        using (var database = File.Open(dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            var page = new byte[WxConstants.PageSz];
            while (wal.Position < lastCommit)
            {
                if (wal.Read(frameHeader) < WxConstants.WalFrameHdrSz
                    || wal.Read(page) < WxConstants.PageSz)
                    throw new WxDecryptException("WAL 快照不完整，请重试");
                var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
                var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
                var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
                if (frameSalt1 != salt1 || frameSalt2 != salt2 || pgno > committedPages) continue;
                database.Position = (pgno - 1) * (long)WxConstants.PageSz;
                database.Write(WxCrypto.DecryptPage(encKey, page, (int)pgno));
                applied++;
            }
            database.SetLength(committedPages * (long)WxConstants.PageSz);
        }
        return applied;
    }

    /// <summary>
    /// Apply only the newly committed frames of a live encrypted WAL onto an already
    /// decrypted shadow database, advancing <paramref name="state"/>. The WAL is opened
    /// with shared access so the running WeChat process (which holds it for writing)
    /// does not block the read. Mirrors <see cref="MergeWal"/> two-phase replay, but only
    /// over the region after the previous cursor.
    /// </summary>
    public static WxWalDeltaResult ApplyWalDelta(string shadowDbPath, string walPath, byte[] encKey, WxWalState? state)
    {
        if (!File.Exists(walPath))
            return new(WxWalDeltaStatus.NoChange, 0, state);

        using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[WxConstants.WalHdrSz];
        if (wal.Read(header) < WxConstants.WalHdrSz)
            return new(WxWalDeltaStatus.NoChange, 0, state);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (WxConstants.WalMagic1 or WxConstants.WalMagic2))
            return new(WxWalDeltaStatus.NoChange, 0, state);
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (pageSize is not (0 or (uint)WxConstants.PageSz))
            throw new WxDecryptException("WAL 页大小不受支持");
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));

        // First incremental pass after a full refresh: the refresh already merged the WAL,
        // so start from the header and re-apply committed frames idempotently (race-free).
        var start = state?.EndOffset ?? WxConstants.WalHdrSz;
        if (state is not null && (state.Salt1 != salt1 || state.Salt2 != salt2))
            return new(WxWalDeltaStatus.NeedsFullRefresh, 0, null);
        if (start > wal.Length)
            return new(WxWalDeltaStatus.NeedsFullRefresh, 0, null);

        // Phase 1: locate the end of the newly committed region.
        var frameHeader = new byte[WxConstants.WalFrameHdrSz];
        var frameSize = WxConstants.WalFrameHdrSz + WxConstants.PageSz;
        long lastCommittedEnd = start;
        uint committedPages = 0;
        wal.Position = start;
        while (wal.Position + frameSize <= wal.Length)
        {
            if (wal.Read(frameHeader) < WxConstants.WalFrameHdrSz) break;
            var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
            var commit = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
            var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
            wal.Seek(WxConstants.PageSz, SeekOrigin.Current);
            if (pgno == 0) break;
            if (frameSalt1 == salt1 && frameSalt2 == salt2 && commit != 0)
            {
                lastCommittedEnd = wal.Position;
                committedPages = commit;
            }
        }

        if (committedPages == 0)
            return new(WxWalDeltaStatus.NoChange, 0, new WxWalState(salt1, salt2, start));

        // Phase 2: apply frames from the cursor up to the last committed frame.
        var applied = 0;
        using (var database = new FileStream(shadowDbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var page = new byte[WxConstants.PageSz];
            wal.Position = start;
            while (wal.Position < lastCommittedEnd)
            {
                if (wal.Read(frameHeader) < WxConstants.WalFrameHdrSz
                    || wal.Read(page) < WxConstants.PageSz)
                    throw new WxDecryptException("WAL 快照不完整，请重试");
                var pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHeader);
                var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
                var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));
                if (frameSalt1 != salt1 || frameSalt2 != salt2 || pgno > committedPages) continue;
                database.Position = (pgno - 1) * (long)WxConstants.PageSz;
                database.Write(WxCrypto.DecryptPage(encKey, page, (int)pgno));
                applied++;
            }
            // Only ever grow the shadow database; never truncate an incremental apply.
            var committedSize = committedPages * (long)WxConstants.PageSz;
            if (committedSize > database.Length) database.SetLength(committedSize);
        }
        return new(WxWalDeltaStatus.Applied, applied, new WxWalState(salt1, salt2, lastCommittedEnd));
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
    public static WxDecryptStats DecryptAll(Dictionary<string, Dictionary<string, string>> keys,
        string dbStorage, string outDir, TextWriter? progress = null, string? snapshotRoot = null)
    {
        var log = new List<string>();
        var ok = 0;
        var failed = new List<string>();
        var skipped = 0;
        Directory.CreateDirectory(outDir);
        var tempDir = Path.Combine(snapshotRoot ?? Path.GetTempPath(), "wxlocal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (rel, info) in keys.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var src = Path.Combine(dbStorage, rel);
                var dst = Path.Combine(outDir, rel);
                if (!File.Exists(src))
                {
                    skipped++;
                    continue;
                }
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
                    catch (WxDecryptException error)
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
