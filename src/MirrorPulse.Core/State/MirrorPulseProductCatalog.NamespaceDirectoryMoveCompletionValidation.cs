using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseNamespaceDirectoryMoveSnapshot> ValidateNamespaceDirectoryMoveCompletionReferencesAsync(
        MirrorPulseNamespaceDirectoryMoveCompletion completion, SqliteTransaction transaction, CancellationToken token)
    {
        var intent = await ReadNamespaceMemberMoveCoreAsync(completion.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory settlement lost its original intent.");
        var snapshot = await ReadNamespaceDirectoryMoveSnapshotCoreAsync(completion.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Directory settlement requires its original complete admission snapshot.");
        var start = await ReadNamespaceMemberMoveStartCoreAsync(completion.OperationId, transaction, token, false).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory settlement lost its before-native start.");
        var native = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveObservation>(completion.OperationId,
            true, transaction, token, false).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory settlement lost its root native observation.");
        var projection = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveProjection>(completion.OperationId,
            false, transaction, token, false).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory settlement lost its root public projection.");
        await ValidateNamespaceMemberMoveCompletionReferencesAsync(completion.Root, transaction, token).ConfigureAwait(false);
        if (completion.PublicEvidenceSha256 != snapshot.PublicEvidenceSha256 || completion.Entries.Count != snapshot.Entries.Count ||
            !native.IsDirectory || completion.PublicResult.ObservedAt < native.Child.ObservedAt ||
            completion.PublicResult.ObservedAt < native.Parent.ObservedAt)
            throw new InvalidOperationException("The complete subtree must follow this original proof and successful root projection.");
        var sealedMembers = snapshot.Entries.ToDictionary(entry => entry.MemberId);
        for (int index = 0; index < snapshot.Entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var before = snapshot.Entries[index];
            var after = completion.Entries[index];
            bool root = before.Depth == 0;
            Guid parentId = root ? intent.Target.ParentMemberId : before.ParentMemberId!.Value;
            string path = root ? intent.Target.RelativePath : intent.Target.RelativePath + before.RelativePath[intent.Source.RelativePath.Length..];
            var member = await ReadNamespaceMemberCoreAsync(before.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A settled descendant lost its original membership.");
            var retainedChild = await ReadNamespaceMoveRetainedVerificationAsync(member, before.ProtectionId, transaction, token).ConfigureAwait(false);
            var parent = await ReadNamespaceMemberCoreAsync(parentId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A settled descendant lost its actual target parent.");
            Guid parentProtection = root ? intent.Target.ParentProtectionId : sealedMembers[parentId].ProtectionId;
            var retainedParent = await ReadNamespaceMoveRetainedVerificationAsync(parent, parentProtection, transaction, token).ConfigureAwait(false);
            if (after.MemberId != before.MemberId || after.ParentMemberId != parentId || after.Native.RelativePath != path ||
                after.Native.IsDirectory != before.IsDirectory || after.Native.Child.LocalObject != before.LocalObject ||
                after.Native.Parent.LocalObject != parent.LocalObject || after.Native.Child.OwnerSid != before.OwnerSid ||
                after.Native.Parent.OwnerSid != intent.OwnerSid || after.Native.Child.Dacl != retainedChild.Dacl ||
                after.Native.Parent.Dacl != retainedParent.Dacl || after.Native.Child.ObservedAt < start.StartedAt ||
                after.Native.Parent.ObservedAt < start.StartedAt ||
                before.Identity.ItemId is { } knownItem && (after.Native.ItemId != knownItem || after.Native.RemoteId != before.Identity.RemoteId))
                throw new InvalidOperationException("Every subtree member requires its exact original identity, binding, parent, owner and retained protection.");
            var projected = after.Projection;
            var fresh = after.Completion;
            if (projected.ItemId != after.Native.ItemId || projected.RemoteId != after.Native.RemoteId || projected.RelativePath != path ||
                projected.ObservedAt < after.Native.Child.ObservedAt || projected.ObservedAt < after.Native.Parent.ObservedAt ||
                (!root && projected.ObservedAt < completion.PublicResult.ObservedAt) || fresh.ItemId != after.Native.ItemId ||
                fresh.RemoteId != after.Native.RemoteId || fresh.RelativePath != path || fresh.IsDirectory != before.IsDirectory ||
                fresh.OfficialRemoteRevision != projected.ProjectedRemoteRevision || fresh.VerifiedAt != completion.Root.VerifiedAt ||
                fresh.VerifiedAt <= projected.ObservedAt || !SameMoveVerification(fresh.Child, after.Native.Child) ||
                !SameMoveVerification(fresh.Parent, after.Native.Parent) || fresh.Child.ObservedAt <= projected.ObservedAt ||
                fresh.Parent.ObservedAt <= projected.ObservedAt || fresh.Child.ObservedAt <= projection.ObservedAt ||
                fresh.Parent.ObservedAt <= projection.ObservedAt || fresh.Child.ObservedAt <= completion.PublicResult.ObservedAt ||
                fresh.Parent.ObservedAt <= completion.PublicResult.ObservedAt ||
                fresh.VerifiedAt < fresh.Child.ObservedAt || fresh.VerifiedAt < fresh.Parent.ObservedAt)
                throw new InvalidOperationException("Every descendant requires an independent public projection and fresh final protected observations.");
            // Native identity revision and the official accepted baseline are independent;
            // neither their equality nor successful relocation proves source acceptance.
            // Recovery may repeat public reconciliation after the immutable root projection
            // was recorded. It must inspect all final objects again after that fresh result.
            if (root && (after.Native != native || projected != projection || fresh != completion.Root))
                throw new InvalidOperationException("The sealed root must retain its original native and public operation facts.");
        }
        return snapshot;
    }
}
