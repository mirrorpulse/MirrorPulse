namespace MirrorPulse.Control.Contracts;

public sealed record NamespaceRemoteRetirementStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;
public sealed record NamespaceRemoteRetirementsArguments(Guid RootId, Guid? AfterOperationId = null, int Limit = 64) : IMirrorPulseControlArguments;

/// <summary>Historical Host observations; completion is distinct from deletion and grants no native authority.</summary>
public sealed record MirrorPulseControlRemoteRetirementStatus(Guid OperationId, Guid MemberId, Guid RootId,
    Guid InstanceId, string BatchId, int Ordinal, string RelativePath, string Phase, string Stage,
    DateTimeOffset PreparedAt, DateTimeOffset? StartedAt, string? RootOutcome, bool? RootNativeDeleted,
    bool? RootOfficialTombstone, string? RootRemoteRevision, Guid? ConflictId, string? ConflictReason,
    int MemberCount, int SettledMembers, int RemovedMembers, int RetainedMembers, int? NextOrdinal,
    DateTimeOffset? OutcomeObservedAt, DateTimeOffset? CompletedAt, string PackageVersion, string PackageSource);

public sealed record MirrorPulseControlRemoteRetirementPage(IReadOnlyList<MirrorPulseControlRemoteRetirementStatus> Entries,
    Guid? NextOperationId);
