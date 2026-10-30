using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A separate current-version observation after a durably rejected effect.</summary>
/// <remarks>This is not historical Worker error metadata or mutation authorization.</remarks>
public sealed record MirrorPulsePostRejectionRemoteVersion(int Version, Guid ObservationId,
    MirrorPulseMutationIntent RejectedEffect, string RemoteRevision,
    DateTimeOffset StartedAt, DateTimeOffset ObservedAt);

/// <summary>The current complete rejected group captured without preparing or authorizing another mutation.</summary>
public sealed record MirrorPulseCoalescedConflictRemoteTarget(Guid ConflictId, InstanceId InstanceId,
    Guid ItemId, string RootKey, string LocalRelativePath, MirrorPulseMutationIntent RejectedEffect,
    Guid? CurrentObservationId)
{
    public string LocalRootRelativePath { get; init; } = string.Empty;
    public string RemoteRelativePath { get; init; } = string.Empty;
}

/// <summary>The immutable observation and complete new conflict epoch selected for one rejected group.</summary>
/// <remarks>Previous conflicts and their optional response fields remain in the catalog.
/// The rejected effect retains the actual request baseline independently of those fields.
/// No original journal ID is acknowledged by creating or reading this observation.</remarks>
public sealed record MirrorPulseCoalescedConflictRemoteObservation(int Version, Guid ObservationId,
    Guid EntryConflictId, InstanceId InstanceId, Guid PlanId, string PlanFingerprint,
    string RootKey, Guid ItemId, string LocalRelativePath, MirrorPulseMutationIntent RejectedEffect,
    string RemoteRelativePath, string RemoteRevision, string? OriginalErrorExpectedRevision,
    DateTimeOffset StartedAt, DateTimeOffset ObservedAt,
    IReadOnlyList<Guid> OriginalOperationIds, IReadOnlyList<Guid> PreviousConflictIds,
    IReadOnlyList<Guid> SelectedConflictIds)
{
    public string? OriginalErrorActualRevision { get; init; }
    public IReadOnlyList<Guid>? InitialConflictIds { get; init; }
    public Guid? PreviousObservationId { get; init; }
    public Guid? RefreshCommandId { get; init; }
}
