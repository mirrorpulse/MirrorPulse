using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseJournalDirectoryMoveMember(Guid MemberId, MirrorPulseLocalFileBinding Binding,
    string SourcePath, string TargetPath, bool IsDirectory, Guid OriginalItemId, string OriginalRemoteId,
    string? OriginalRevision, string? OriginalNativeRevision, Guid? PreviousIdentityAcceptance);

internal sealed record MirrorPulseJournalDirectoryMoveAdmission(Guid JournalOperationId, long JournalSequence,
    string OriginalJournalPayloadSha256, Guid LocalMoveOperationId, RootId RootId, InstanceId InstanceId,
    string RootKey, IReadOnlyList<MirrorPulseJournalDirectoryMoveMember> Members)
{
    internal MirrorPulseJournalDirectoryMoveMember Root => Members[0];
}

internal sealed record MirrorPulseDirectoryMoveTarget(Guid MemberId, Guid ItemId, string RemoteId, string? Revision);

internal sealed record MirrorPulseDirectoryMoveProjectionReadback(MirrorPulseDirectoryMoveTarget Target, DateTimeOffset ObservedAt);

internal sealed record MirrorPulseJournalDirectoryMove(MirrorPulseJournalDirectoryMoveAdmission Admission,
    MirrorPulseMutationRecord Mutation, IReadOnlyList<MirrorPulseDirectoryMoveTarget>? Targets,
    int ProjectedMemberCount, bool AcknowledgementStarted);
