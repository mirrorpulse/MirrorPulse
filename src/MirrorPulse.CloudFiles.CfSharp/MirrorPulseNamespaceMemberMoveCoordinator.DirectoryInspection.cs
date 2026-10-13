using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private sealed record DirectoryMoveObject(MirrorPulseNamespaceDirectoryMoveEntry Entry, CloudItem Item,
        MirrorPulseWindowsNamespacePermissionLease Lease, MirrorPulseNamespaceProtectionProof Proof,
        MirrorPulseNamespacePermissionObject Facts, CloudItemSnapshot Snapshot);

    private sealed class DirectoryMoveObjects(List<DirectoryMoveObject> objects) : IAsyncDisposable
    {
        internal List<DirectoryMoveObject> Objects => objects;
        internal void Add(DirectoryMoveObject item) => objects.Add(item);

        public async ValueTask DisposeAsync()
        {
            for (int index = objects.Count - 1; index >= 0; index--)
                await objects[index].Lease.DisposeAsync().ConfigureAwait(false);
            objects.Clear();
        }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry>> ReadDirectoryMoveEntriesAsync(Guid memberId, CancellationToken token)
    {
        var tree = await _catalog.ReadNamespaceMemberTreeAsync(memberId, token).ConfigureAwait(false);
        if (tree.Count > 65_536) throw new InvalidOperationException("The controlled directory subtree is too large.");
        var entries = new List<MirrorPulseNamespaceDirectoryMoveEntry>(tree.Count);
        foreach (var item in tree)
        {
            var snapshot = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(item.MemberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The directory subtree lost a registered member.");
            var member = snapshot.Context.Member;
            var protection = snapshot.Context.Protection;
            var location = snapshot.Context.Location;
            if (item.RelativePath != location.RelativePath || item.ParentMemberId != location.ParentMemberId ||
                item.LocalObject != member.LocalObject || item.IsDirectory != member.IsDirectory)
                throw new InvalidDataException("The current directory subtree changed during admission.");
            entries.Add(new(member.MemberId, item.ParentMemberId, item.RelativePath, member.IsDirectory, member.LocalObject,
                member.OwnerSid, protection.ProtectionId, protection.VerifiedAt, location.CompletedMoveOperationId,
                snapshot.Identity, item.Depth)
            { PreviousPositionOperationId = location.PositionOperationId });
        }
        return entries.AsReadOnly();
    }

    private async Task<DirectoryMoveObjects> OpenDirectoryMoveObjectsAsync(IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry> entries,
        MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget, CancellationToken token)
    {
        _actor.AssertAccess();
        var result = new DirectoryMoveObjects([]);
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var proof = await ReadDirectoryProtectionAsync(entry, intent, token).ConfigureAwait(false);
                string path = DirectoryMoveMemberPath(entry, intent, atTarget);
                CloudItem item = entry.IsDirectory ? _fileSystem.GetDirectory(path) : _fileSystem.GetFile(path);
                var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, _router, token).ConfigureAwait(false);
                try
                {
                    var facts = await lease.InspectAsync(token).ConfigureAwait(false);
                    var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
                    RequireDirectoryMoveObject(entry, path, facts, snapshot, proof, intent);
                    result.Add(new(entry, item, lease, proof, facts, snapshot));
                }
                catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
            }
            await RequireExactLocalDirectoryTreeAsync(entries, intent, atTarget, token).ConfigureAwait(false);
            return result;
        }
        catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task RequireExactLocalDirectoryTreeAsync(IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry> entries,
        MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget, CancellationToken token)
    {
        var children = entries.ToLookup(entry => entry.ParentMemberId);
        int total = 0;
        // Public recursive enumeration deliberately skips directory reparse points, including
        // Cloud Files placeholders. Compose its direct-child API over the sealed product
        // directories instead of treating the library's recursion as a complete managed tree.
        foreach (var directory in entries.Where(entry => entry.IsDirectory))
        {
            var expected = children[directory.MemberId].Select(entry => DirectoryMoveMemberPath(entry, intent, atTarget))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int count = expected.Count, found = 0;
            CloudDirectory local = _fileSystem.GetDirectory(DirectoryMoveMemberPath(directory, intent, atTarget));
            await foreach (var item in local.EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, token).ConfigureAwait(false))
            {
                if (!expected.Remove(item.RelativePath.Replace('\\', '/')) || ++found > count)
                    throw new InvalidDataException("An unregistered or repeated local object prevents controlled directory movement.");
            }
            if (expected.Count != 0 || found != count)
                throw new InvalidDataException($"A protected directory has an incomplete registered child set: expected {count}, observed {found}.");
            total += found;
        }
        if (total != entries.Count - 1)
            throw new InvalidDataException("The actual local directory does not contain the complete registered subtree.");
    }

    private static string DirectoryMoveMemberPath(MirrorPulseNamespaceDirectoryMoveEntry entry,
        MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget)
    {
        if (entry.MemberId == intent.MemberId)
            return atTarget ? intent.Target.RelativePath : intent.Source.RelativePath;
        if (!entry.RelativePath.StartsWith(intent.Source.RelativePath + "/", StringComparison.Ordinal))
            throw new InvalidDataException("The sealed directory descendant is outside its admitted source.");
        return atTarget ? intent.Target.RelativePath + entry.RelativePath[intent.Source.RelativePath.Length..] : entry.RelativePath;
    }

    private static void RequireDirectoryMoveObject(MirrorPulseNamespaceDirectoryMoveEntry entry, string path,
        MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot, MirrorPulseNamespaceProtectionProof proof,
        MirrorPulseNamespaceMemberMoveIntent intent)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != entry.LocalObject || facts.LocalObject != proof.Verification.LocalObject || facts.LinkCount != 1 ||
            facts.OwnerSid != entry.OwnerSid || facts.OwnerSid != intent.OwnerSid || facts.OwnerSid != proof.OwnerSid ||
            facts.RootId != intent.RootId || facts.RootId != proof.RootId || facts.RelativePath != path ||
            facts.IsDirectory != entry.IsDirectory || facts.IsDirectory != proof.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != entry.IsDirectory || proof.RoleSid != intent.RoleSid ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("The actual directory member lost its retained binding, owner, type, path or protected descriptor.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, entry.Identity, requirePlaceholder: true);
    }

    private async Task ReinspectDirectoryMoveObjectsAsync(DirectoryMoveObjects objects,
        MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget, CancellationToken token)
    {
        foreach (var item in objects.Objects)
        {
            var current = await ReadDirectoryProtectionAsync(item.Entry, intent, token).ConfigureAwait(false);
            if (current != item.Proof)
                throw new InvalidOperationException("Directory member protection or identity changed during native inspection.");
            RequireDirectoryMoveObject(item.Entry, DirectoryMoveMemberPath(item.Entry, intent, atTarget),
                await item.Lease.InspectAsync(token).ConfigureAwait(false), await item.Item.InspectAsync(token).ConfigureAwait(false), current, intent);
        }
        await RequireExactLocalDirectoryTreeAsync(objects.Objects.Select(item => item.Entry).ToArray(), intent, atTarget, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceProtectionProof> ReadDirectoryProtectionAsync(MirrorPulseNamespaceDirectoryMoveEntry entry,
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var snapshot = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.MemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The sealed directory member is missing.");
        var member = snapshot.Context.Member;
        var location = snapshot.Context.Location;
        var proof = RequireAdmittedProof(snapshot, entry.ProtectionId, intent);
        if (member.LocalObject != entry.LocalObject || member.OwnerSid != entry.OwnerSid || member.IsDirectory != entry.IsDirectory ||
            proof.Verification.ObservedAt != entry.ProtectionVerifiedAt || snapshot.Identity != entry.Identity ||
            location.ParentMemberId != entry.ParentMemberId || location.RelativePath != entry.RelativePath ||
            location.CompletedMoveOperationId != entry.PreviousMoveOperationId || location.PositionOperationId != entry.PreviousPositionOperationId)
            throw new InvalidDataException("Directory execution cannot replace the sealed member, protection, identity or source position.");
        return proof;
    }

    private static void RequireDirectoryMoveProof(CloudDirectoryMoveProof proof, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        var identity = CloudPlaceholderIdentity.Decode(proof.ExpectedPlaceholderIdentity.Span);
        if (proof.SourceRelativePath.Replace('\\', '/') != intent.Source.RelativePath ||
            proof.DestinationRelativePath.Replace('\\', '/') != intent.Target.RelativePath || proof.RootItemId != intent.ItemId ||
            identity.ItemId != intent.ItemId || identity.RemoteId != intent.RemoteId ||
            intent.LocalObject != new MirrorPulseLocalFileBinding(proof.ExpectedBinding.VolumeSerialNumber,
                proof.ExpectedBinding.SyncRootFileId, proof.ExpectedBinding.LocalFileId))
            throw new InvalidDataException("Public directory evidence must retain this exact root, binding, identity and intended endpoints.");
    }
}
