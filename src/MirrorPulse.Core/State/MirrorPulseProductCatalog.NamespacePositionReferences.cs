using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record NamespaceMemberPositionEvidence(Guid? OperationId, Guid? ParentMemberId,
        string RelativePath, Guid? CompletedMoveOperationId, DateTimeOffset VerifiedAt);

    // Legacy completions are read as evidence without persisting a synthetic position fact.
    // Check time before following operation references, including damaged cyclic histories.
    private async Task<NamespaceMemberPositionEvidence> ReadNamespaceMemberPositionEvidenceCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid? operationId, DateTimeOffset admittedAt,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (operationId is not { } positionId)
            return new(null, member.ParentMemberId, member.RelativePath, null, member.RegisteredAt);
        var position = await ReadNamespaceMemberPositionRowAsync(member.MemberId, positionId, transaction, token).ConfigureAwait(false);
        if (position is not null)
        {
            if (position.VerifiedAt > admittedAt)
                throw new InvalidOperationException("Position evidence cannot follow the operation that references it.");
            _ = await ValidateNamespaceMemberPositionHistoryAsync(member, position, transaction, token).ConfigureAwait(false);
            return new(position.OperationId, position.ParentMemberId, position.RelativePath,
                position.CompletedMoveOperationId, position.VerifiedAt);
        }
        var move = await ReadNamespaceMemberMoveRowAsync(positionId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original position operation is missing.");
        var row = await ReadNamespaceMemberMoveCompletionRowAsync(positionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A prepared or partly projected operation is not a completed position.");
        if (move.MemberId != member.MemberId || row.MemberId != member.MemberId || move.LocalObject != member.LocalObject ||
            move.RootId != member.RootId || move.OwnerSid != member.OwnerSid || row.Completion.VerifiedAt > admittedAt)
            throw new InvalidOperationException("A legacy position must retain the same member, complete binding, owner and chronology.");
        _ = await ReadNamespaceMemberMoveCoreAsync(positionId, transaction, token).ConfigureAwait(false);
        _ = await ReadNamespaceMemberMoveCompletionCoreAsync(positionId, transaction, token).ConfigureAwait(false);
        return new(positionId, move.Target.ParentMemberId, row.Completion.RelativePath, positionId, row.Completion.VerifiedAt);
    }

    private async Task RequireNamespaceMoveParentPositionAsync(MirrorPulseNamespaceMemberRegistration parent,
        MirrorPulseNamespaceMoveEndpoint endpoint, DateTimeOffset admittedAt, bool current,
        SqliteTransaction transaction, CancellationToken token)
    {
        var evidence = await ReadNamespaceMemberPositionEvidenceCoreAsync(parent, endpoint.ParentPositionOperationId,
            admittedAt, transaction, token).ConfigureAwait(false);
        if (ParentMovePath(endpoint.RelativePath) != evidence.RelativePath)
            throw new InvalidOperationException("The endpoint requires its exact retained direct parent position.");
        if (current)
        {
            var location = await ReadNamespaceMemberLocationCoreAsync(parent, transaction, token).ConfigureAwait(false);
            if (location.RelativePath != evidence.RelativePath || location.CurrentPositionOperationId != evidence.OperationId)
                throw new InvalidOperationException("A new endpoint must reference its parent's latest committed placement.");
        }
    }
}
