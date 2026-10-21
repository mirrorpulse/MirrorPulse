namespace MirrorPulse.Control.Contracts;

public sealed record NamespaceTransferStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;
public sealed record NamespaceTransfersArguments(Guid RootId) : IMirrorPulseControlArguments;

/// <summary>Host-owned transfer progress; a settled source name is distinct from final journal acknowledgement and completion.</summary>
public sealed record MirrorPulseControlTransferStatus(Guid OperationId, Guid SourceRootId, Guid TargetRootId,
    Guid SourceInstanceId, Guid TargetInstanceId, Guid SourceMemberId, Guid? TargetMemberId,
    string SourceRelativePath, string TargetRelativePath, string Stage, DateTimeOffset PreparedAt,
    DateTimeOffset? CopyStartedAt, DateTimeOffset? CopyAcceptedAt, DateTimeOffset? SourceDeleteStartedAt,
    DateTimeOffset? SourceDeleteAcceptedAt, DateTimeOffset? SourceNativeRetiredAt, DateTimeOffset? SourceNameSettledAt,
    bool RootsFenced, bool JournalAcknowledged, bool Completed);

public sealed record MirrorPulseControlTransfers(IReadOnlyList<MirrorPulseControlTransferStatus> Entries);
