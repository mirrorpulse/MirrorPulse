namespace MirrorPulse.Control.Contracts;

public sealed record ConflictRefreshVersionArguments(Guid CommandId, Guid ConflictId) : IMirrorPulseControlArguments;

/// <summary>A separate current-version observation; nullable original Worker error fields retain their historical meaning.</summary>
public sealed record MirrorPulseControlConflictVersionObservation(int Version, Guid ObservationId, Guid RejectedEffectId,
    string? OriginalRequestExpectedRevision, string? OriginalErrorExpectedRevision, string? OriginalErrorActualRevision,
    string SelectedRemoteRevision, DateTimeOffset StartedAt, DateTimeOffset ObservedAt, Guid? PreviousObservationId,
    IReadOnlyList<Guid> SelectedConflictIds);

public sealed record MirrorPulseControlConflictVersionRefresh(int Version, Guid CommandId, Guid ConflictId, string State,
    MirrorPulseControlConflictVersionObservation? Observation);
