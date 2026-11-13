using System.Buffers.Binary;
using System.Security.Cryptography;
using StudentReminder.WxLocal;

namespace StudentReminder.Tests.IntegrationTests;

/// <summary>
/// Hermetic tests for the incremental WAL delta reader (<see cref="WxDbDecryptor.ApplyWalDelta"/>).
/// Frames are built with arbitrary page bodies — the assertions target the reader's control
/// flow (status, applied count, cursor advance, salt mismatch) rather than byte fidelity,
/// which is already covered by <see cref="WxLocalTests"/>.
/// </summary>
public sealed class WxWalIncrementalTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "wxwal-tests-" + Guid.NewGuid().ToString("N"));

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
            if (new HashSet<byte>(key).Count >= 15 && key.Any(b => b != 0) && key.Any(b => b != 0xFF)) return key;
        }
    }

    /// <summary>Build an encrypted WAL with exactly <paramref name="pageCount"/> committed pages.</summary>
    private static void BuildWalPages(string walPath, int pageCount,
        uint headerSalt1 = 0x12345678u, uint frameSalt1 = 0x12345678u)
    {
        const uint salt2 = 0x9ABCDEF0u;
        using var wal = File.Create(walPath);
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header, WxConstants.WalMagic1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3007000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)WxConstants.PageSz);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), headerSalt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), salt2);
        wal.Write(header);
        for (var pgno = 1; pgno <= pageCount; pgno++)
        {
            var frame = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)pgno);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), pgno == pageCount ? (uint)pageCount : 0);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), frameSalt1);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12), salt2);
            wal.Write(frame);
            var page = new byte[4096];
            page[0] = (byte)pgno;
            wal.Write(page);
        }
    }

    private string ShadowFile(string name)
    {
        var path = WorkPath(name);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    [Fact]
    public void ApplyWalDelta_MissingWal_NoChange()
    {
        var result = WxDbDecryptor.ApplyWalDelta(ShadowFile("missing.db"), WorkPath("missing.db-wal"), RandomKey(), null);
        Assert.Equal(WxWalDeltaStatus.NoChange, result.Status);
        Assert.Equal(0, result.AppliedFrames);
    }

    [Fact]
    public void ApplyWalDelta_NullState_AppliesCommittedFramesAndAdvancesCursor()
    {
        var shadow = ShadowFile("shadow.db");
        var wal = WorkPath("shadow.db-wal");
        BuildWalPages(wal, pageCount: 3);

        WxWalState? state = null;
        var first = WxDbDecryptor.ApplyWalDelta(shadow, wal, RandomKey(), state);
        Assert.Equal(WxWalDeltaStatus.Applied, first.Status);
        Assert.Equal(3, first.AppliedFrames);
        Assert.NotNull(first.State);
        Assert.True(first.State!.EndOffset > WxConstants.WalHdrSz);

        // Nothing new after the cursor advanced.
        var second = WxDbDecryptor.ApplyWalDelta(shadow, wal, RandomKey(), first.State);
        Assert.Equal(WxWalDeltaStatus.NoChange, second.Status);
        Assert.Equal(0, second.AppliedFrames);
    }

    [Fact]
    public void ApplyWalDelta_AppendedPages_AppliesOnlyTheDelta()
    {
        var shadow = ShadowFile("inc.db");
        var wal = WorkPath("inc.db-wal");
        BuildWalPages(wal, pageCount: 2);

        WxWalState? state = null;
        var first = WxDbDecryptor.ApplyWalDelta(shadow, wal, RandomKey(), state);
        Assert.Equal(2, first.AppliedFrames);

        // Same salts, one more committed page appended.
        BuildWalPages(wal, pageCount: 3);
        var second = WxDbDecryptor.ApplyWalDelta(shadow, wal, RandomKey(), first.State);
        Assert.Equal(WxWalDeltaStatus.Applied, second.Status);
        Assert.Equal(1, second.AppliedFrames);
    }

    [Fact]
    public void ApplyWalDelta_SaltMismatch_NeedsFullRefresh()
    {
        var shadow = ShadowFile("salt.db");
        var wal = WorkPath("salt.db-wal");
        BuildWalPages(wal, pageCount: 2, headerSalt1: 0x12345678u, frameSalt1: 0x12345678u);

        var stale = new WxWalState(0xDEADBEEFu, 0x9ABCDEF0u, 32);
        var result = WxDbDecryptor.ApplyWalDelta(shadow, wal, RandomKey(), stale);
        Assert.Equal(WxWalDeltaStatus.NeedsFullRefresh, result.Status);
        Assert.Equal(0, result.AppliedFrames);
    }
}
