namespace StudentReminder.WxLocal;

/// <summary>Outcome of an incremental WAL tail read.</summary>
public enum WxWalDeltaStatus
{
    /// <summary>New committed frames were decrypted and applied to the shadow database.</summary>
    Applied,

    /// <summary>No new committed frames (or the WAL is absent / has no frames yet).</summary>
    NoChange,

    /// <summary>
    /// The WAL generation changed (WeChat checkpointed and reset the WAL), so the
    /// previous cursor is invalid and the shadow database must be fully re-snapshotted.
    /// </summary>
    NeedsFullRefresh,
}

/// <summary>Incremental read position within a single database's encrypted WAL file.</summary>
public sealed record WxWalState(uint Salt1, uint Salt2, long EndOffset);

/// <summary>Result of <see cref="WxDbDecryptor.ApplyWalDelta"/>.</summary>
public sealed record WxWalDeltaResult(WxWalDeltaStatus Status, int AppliedFrames, WxWalState? State);
