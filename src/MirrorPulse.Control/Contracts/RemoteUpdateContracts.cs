namespace MirrorPulse.Control.Contracts;

public sealed record NamespaceRemoteUpdateStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;
public sealed record NamespaceRemoteUpdatesArguments(Guid RootId) : IMirrorPulseControlArguments;
public sealed record NamespaceRemoteUpdateRecoverArguments(Guid OperationId) : IMirrorPulseControlArguments;

/// <summary>Host observations of product settlement, without claiming that an Adapter cursor was published.</summary>
public sealed record MirrorPulseControlRemoteUpdateStatus(Guid OperationId, Guid RootId, Guid InstanceId,
    Guid MemberId, string RelativePath, string BatchId, int Ordinal, string ChangeId, string Stage,
    DateTimeOffset PreparedAt, DateTimeOffset? StartedAt, string? Outcome, string? EvidenceSource,
    DateTimeOffset? OutcomeObservedAt, string PackageVersion, string PackageSource);

/// <summary>A root has at most one pending original update owner.</summary>
public sealed record MirrorPulseControlRemoteUpdates(IReadOnlyList<MirrorPulseControlRemoteUpdateStatus> Entries);
