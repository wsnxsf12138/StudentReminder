namespace StudentReminder.QqLocal;

/// <summary>Outcome of an incremental WAL tail read.</summary>
public enum QqWalDeltaStatus
{
    Applied,
    NoChange,
    NeedsFullRefresh,
}

/// <summary>Incremental read position within a single database's encrypted WAL file.</summary>
public sealed record QqWalState(uint Salt1, uint Salt2, long EndOffset);

/// <summary>Result of <see cref="QqDbDecryptor.ApplyWalDelta"/>.</summary>
public sealed record QqWalDeltaResult(QqWalDeltaStatus Status, int AppliedFrames, QqWalState? State);
