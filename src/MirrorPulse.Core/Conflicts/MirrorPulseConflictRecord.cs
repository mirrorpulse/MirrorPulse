using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Conflicts;

public enum MirrorPulseConflictReason
{
    Content,
    Metadata,
    Move,
    Delete,
    PathCollision,
    StaleRemoteRevision,
    MissingItem,
    Unknown,
    LocalDeleteReconciliationRequired,
}

public enum MirrorPulseConflictStatus
{
    Pending,
    Resolved,
    Dismissed,
}

public enum MirrorPulseConflictSource
{
    Upload,
    Protocol,
    CfSharpRemote,
}

/// <summary>
/// Durable conflict-center entry kept outside the Cloud Files sync root.
/// </summary>
public sealed record MirrorPulseConflictRecord
{
    public MirrorPulseConflictRecord(
        Guid conflictId,
        InstanceId instanceId,
        string changeId,
        string relativePath,
        MirrorPulseConflictReason reason,
        MirrorPulseVersionComparison versionComparison,
        string? localRevision,
        string? remoteRevision,
        DateTimeOffset detectedAt,
        MirrorPulseConflictStatus status = MirrorPulseConflictStatus.Pending,
        MirrorPulseConflictSource source = MirrorPulseConflictSource.Upload)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("A conflict ID cannot be empty.", nameof(conflictId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Contains('\0'))
        {
            throw new ArgumentException("Conflict paths cannot contain a null character.", nameof(relativePath));
        }

        ConflictId = conflictId;
        InstanceId = instanceId;
        ChangeId = changeId;
        RelativePath = relativePath;
        Reason = reason;
        VersionComparison = versionComparison;
        LocalRevision = localRevision;
        RemoteRevision = remoteRevision;
        DetectedAt = detectedAt;
        Status = status;
        Source = source;
    }

    public Guid ConflictId { get; }

    public InstanceId InstanceId { get; }

    public string ChangeId { get; }

    public string RelativePath { get; }

    public MirrorPulseConflictReason Reason { get; }

    public MirrorPulseVersionComparison VersionComparison { get; }

    public string? LocalRevision { get; }

    public string? RemoteRevision { get; }

    public DateTimeOffset DetectedAt { get; }

    public MirrorPulseConflictStatus Status { get; }

    public MirrorPulseConflictSource Source { get; }

    public bool IsPending => Status == MirrorPulseConflictStatus.Pending;
}
