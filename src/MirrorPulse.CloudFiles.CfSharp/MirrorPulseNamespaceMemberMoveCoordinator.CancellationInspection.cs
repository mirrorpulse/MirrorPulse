using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private static MirrorPulseNamespaceDirectoryMoveEntry[] RootCancellationEntries(MirrorPulseRootLabelCancellationTree tree)
        => tree.Members.Select(snapshot =>
        {
            var context = snapshot.Context; var member = context.Member; var location = context.Location;
            return new MirrorPulseNamespaceDirectoryMoveEntry(member.MemberId, location.ParentMemberId, location.RelativePath,
                member.IsDirectory, member.LocalObject, member.OwnerSid, context.Protection.ProtectionId, context.Protection.VerifiedAt,
                location.CompletedMoveOperationId, snapshot.Identity, location.RelativePath.Count(character => character == '/'))
            { PreviousPositionOperationId = location.PositionOperationId };
        }).ToArray();

    private static MirrorPulseNamespaceProtectionProof RootCancellationProof(MirrorPulseNamespaceDirectoryMoveEntry entry,
        MirrorPulseNamespaceMemberProtectionSnapshot snapshot, RootId root, string owner, MirrorPulseNamespaceMemberMoveIntent? intent)
    {
        var context = snapshot.Context; var member = context.Member; var location = context.Location;
        var proof = intent is null ? MirrorPulseNamespaceProtectionProofReader.FromSnapshot(snapshot) : RequireAdmittedProof(snapshot, entry.ProtectionId, intent);
        if (member.MemberId != entry.MemberId || member.RootId != root || member.OwnerSid != owner || member.OwnerSid != entry.OwnerSid ||
            member.LocalObject != entry.LocalObject || member.IsDirectory != entry.IsDirectory ||
            location.ParentMemberId != entry.ParentMemberId || location.RelativePath != entry.RelativePath ||
            location.CompletedMoveOperationId != entry.PreviousMoveOperationId || location.PositionOperationId != entry.PreviousPositionOperationId ||
            snapshot.Identity != entry.Identity || proof.ProtectionId != entry.ProtectionId || proof.Verification.ObservedAt != entry.ProtectionVerifiedAt)
            throw new InvalidDataException("Cancellation inspection cannot replace current membership, position, identity or protection provenance.");
        return proof;
    }

    private async Task<DirectoryMoveObjects> OpenRootCancellationObjectsAsync(IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry> entries,
        MirrorPulseRootLabelCancellationTree tree, RootId root, string owner, MirrorPulseNamespaceMemberMoveIntent? intent, CancellationToken token)
    {
        _actor.AssertAccess(); var result = new DirectoryMoveObjects([]);
        var byId = tree.Members.ToDictionary(snapshot => snapshot.Context.Member.MemberId);
        if (entries.Count != byId.Count) throw new InvalidDataException("Cancellation inspection requires the complete coherent inventory.");
        try
        {
            foreach (var entry in entries)
            {
                if (!byId.TryGetValue(entry.MemberId, out var retained)) throw new InvalidDataException("Cancellation lost a guarded member.");
                var proof = RootCancellationProof(entry, retained, root, owner, intent);
                CloudItem item = entry.IsDirectory ? _fileSystem.GetDirectory(entry.RelativePath) : _fileSystem.GetFile(entry.RelativePath);
                var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, _router, token).ConfigureAwait(false);
                try
                {
                    var facts = await lease.InspectAsync(token).ConfigureAwait(false); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
                    RequireUnsealedCancellationObject(facts, snapshot, proof, entry.LocalObject, owner, root, entry.RelativePath, entry.IsDirectory);
                    MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, entry.Identity, requirePlaceholder: true);
                    if (intent is not null) RequireDirectoryMoveObject(entry, entry.RelativePath, facts, snapshot, proof, intent);
                    result.Add(new(entry, item, lease, proof, facts, snapshot));
                }
                catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
            }
            await RequireExactLocalDirectoryTreeAsync(entries, entry => entry.RelativePath, token).ConfigureAwait(false);
            return result;
        }
        catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task ReinspectRootCancellationObjectsAsync(DirectoryMoveObjects objects, MirrorPulseRootLabelCancellationTree original,
        RootId root, string owner, MirrorPulseNamespaceMemberMoveIntent? intent, CancellationToken token)
    {
        var current = await _catalog.ReadRootLabelCancellationTreeAsync(original.OperationId, token).ConfigureAwait(false);
        var byId = current.Members.ToDictionary(snapshot => snapshot.Context.Member.MemberId);
        if (current.RootMemberId != original.RootMemberId || current.Anchor != original.Anchor || byId.Count != original.Members.Count ||
            original.Members.Any(snapshot => !byId.TryGetValue(snapshot.Context.Member.MemberId, out var latest) || latest != snapshot))
            throw new InvalidOperationException("The complete cancellation inventory changed during guarded public reconciliation.");
        foreach (var item in objects.Objects)
        {
            var proof = RootCancellationProof(item.Entry, byId[item.Entry.MemberId], root, owner, intent);
            if (proof != item.Proof) throw new InvalidOperationException("Guarded cancellation protection changed during inspection.");
            var facts = await item.Lease.InspectAsync(token).ConfigureAwait(false); var snapshot = await item.Item.InspectAsync(token).ConfigureAwait(false);
            RequireUnsealedCancellationObject(facts, snapshot, proof, item.Entry.LocalObject, owner, root, item.Entry.RelativePath, item.Entry.IsDirectory);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, item.Entry.Identity, requirePlaceholder: true);
            if (intent is not null) RequireDirectoryMoveObject(item.Entry, item.Entry.RelativePath, facts, snapshot, proof, intent);
        }
        await RequireExactLocalDirectoryTreeAsync(objects.Objects.Select(item => item.Entry).ToArray(), entry => entry.RelativePath, token).ConfigureAwait(false);
    }
}
