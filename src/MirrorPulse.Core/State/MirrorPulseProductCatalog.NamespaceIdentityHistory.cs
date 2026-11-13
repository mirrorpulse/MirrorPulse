using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record NamespaceIdentityTransition(Guid OperationId, Guid? PreviousOperationId,
        Guid PreviousItemId, string PreviousRemoteId, Guid ItemId, string RemoteId, DateTimeOffset ObservedAt);

    private async Task<NamespaceIdentityTransition?> ReadNamespaceIdentityTransitionAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.IsDirectory)
        {
            var proof = await ReadDirectoryAcceptanceCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (proof is null) return null;
            var confirmation = await ReadDirectoryConfirmationCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (confirmation is null) return null;
            if (proof.MemberId != member.MemberId || proof.RootId != member.RootId || proof.LocalObject != member.LocalObject)
                throw new InvalidOperationException("The directory confirmation belongs to a different registered object.");
            return new(operationId, proof.PreviousAcceptanceOperationId, proof.PreviousItemId, proof.PreviousRemoteId,
                proof.AcceptedItemId, proof.RemoteId, confirmation.ObservedAt);
        }
        var accepted = await ReadNamespaceIdentityAcceptanceRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (accepted is null) return null;
        if (accepted.MemberId != member.MemberId || accepted.RootId != member.RootId ||
            accepted.Proof.UploadBinding.LocalObject != member.LocalObject)
            throw new InvalidOperationException("The upload confirmation belongs to a different registered object.");
        await ValidateNamespaceIdentityAcceptanceReferencesAsync(accepted, member, transaction, token).ConfigureAwait(false);
        return new(operationId, accepted.PreviousAcceptanceOperationId, accepted.PreviousItemId, accepted.PreviousRemoteId,
            accepted.Proof.AcceptedItemId, accepted.Proof.RemoteId, accepted.Confirmation.ObservedAt);
    }

    private async Task<NamespaceIdentityTransition> ValidateNamespaceIdentityHistoryAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid head, (Guid? ItemId, string? RemoteId) original,
        Guid itemId, string remoteId, DateTimeOffset latest, SqliteTransaction transaction, CancellationToken token)
    {
        var visited = new HashSet<Guid>();
        NamespaceIdentityTransition? newest = null;
        Guid? cursor = head;
        while (cursor is { } operationId)
        {
            if (!visited.Add(operationId)) throw new InvalidDataException("The member identity history contains a cycle.");
            var transition = await ReadNamespaceIdentityTransitionAsync(member, operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The member's retained identity transition is missing.");
            if (transition.ItemId != itemId || transition.RemoteId != remoteId ||
                transition.ObservedAt < member.RegisteredAt || transition.ObservedAt > latest)
                throw new InvalidOperationException("The identity history must follow the same registered object and observation order.");
            newest ??= transition;
            itemId = transition.PreviousItemId;
            remoteId = transition.PreviousRemoteId;
            latest = transition.ObservedAt;
            cursor = transition.PreviousOperationId;
        }
        if (original.ItemId != itemId || original.RemoteId != remoteId)
            throw new InvalidOperationException("The member identity history does not reach its registered identity.");
        return newest!;
    }
}
