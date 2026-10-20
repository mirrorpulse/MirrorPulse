namespace MirrorPulse.Control.Contracts;

public sealed record NamespaceRemoteDeleteStatusArguments(Guid CommandId) : IMirrorPulseControlArguments;
public sealed record NamespaceRemoteDeletesArguments(Guid RootId, Guid? AfterCommandId = null, int Limit = 64) : IMirrorPulseControlArguments;
public sealed record NamespaceRemoteDeleteRecoverArguments(Guid CommandId) : IMirrorPulseControlArguments;

/// <summary>Host observations of one original explicit Delete; a missing outcome remains unknown.</summary>
public sealed record MirrorPulseControlRemoteDeleteStatus(Guid CommandId, Guid ConflictId, Guid RootId, Guid InstanceId,
    string RelativePath, string Stage, int MemberCount, int SettledMembers, int NextOrdinal, DateTimeOffset PreparedAt,
    DateTimeOffset? StartedAt, string? Outcome, DateTimeOffset? OutcomeObservedAt, DateTimeOffset? MembersCompletedAt,
    bool CommandCompleted, string PackageVersion, string PackageSource);

public sealed record MirrorPulseControlRemoteDeletePage(IReadOnlyList<MirrorPulseControlRemoteDeleteStatus> Entries, Guid? NextCommandId);
