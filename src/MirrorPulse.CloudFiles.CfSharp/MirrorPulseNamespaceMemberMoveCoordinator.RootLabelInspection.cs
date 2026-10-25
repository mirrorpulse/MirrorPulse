using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private async Task<MirrorPulseNamespaceProtectionProof> ReadRootLabelAnchorAsync(MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var anchor = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(intent.Source.ParentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original root Label anchor is missing.");
        return MirrorPulseRootLabelAnchorInspection.RequireProof(anchor, intent);
    }

    private async Task<MirrorPulseNamespacePermissionObject> InspectRootLabelAnchorAsync(MirrorPulseWindowsNamespacePermissionLease anchor,
        MirrorPulseNamespaceProtectionProof proof, MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var facts = await anchor.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseRootLabelAnchorInspection.RequireNative(facts, await _fileSystem.Root.InspectAsync(token).ConfigureAwait(false), proof, intent);
        return facts;
    }

    private async Task RequireRootLabelAdmissionAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceProtectionProof child, MirrorPulseNamespaceProtectionProof anchor, CancellationToken token)
    {
        _actor.AssertAccess();
        if (await _catalog.ValidateNamespaceMemberMoveExecutionAsync(intent.OperationId, token).ConfigureAwait(false) != intent ||
            await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false) != child ||
            await ReadRootLabelAnchorAsync(intent, token).ConfigureAwait(false) != anchor)
            throw new InvalidOperationException("The original root Label admission, member or physical anchor changed.");
    }

    private async Task<DirectoryMoveObjects> OpenRootLabelObjectsAsync(IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry> entries,
        MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget, MirrorPulseRootLabelTargetInspection inspection, CancellationToken token)
    {
        if (!atTarget) return await OpenDirectoryMoveObjectsAsync(entries, intent, atTarget: false, token).ConfigureAwait(false);
        _actor.AssertAccess();
        var result = new DirectoryMoveObjects([]);
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var proof = await ReadDirectoryProtectionAsync(entry, intent, token).ConfigureAwait(false);
                string path = DirectoryMoveMemberPath(entry, intent, atTarget: true);
                CloudItem item = entry.IsDirectory ? _fileSystem.GetDirectory(path) : _fileSystem.GetFile(path);
                var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenRootLabelTargetMetadataAsync(item, _router, inspection, token).ConfigureAwait(false);
                try
                {
                    var facts = await lease.InspectAsync(token).ConfigureAwait(false);
                    var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
                    RequireDirectoryMoveObject(entry, path, facts, snapshot, proof, intent);
                    result.Add(new(entry, item, lease, proof, facts, snapshot));
                }
                catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
            }
            await RequireExactLocalDirectoryTreeAsync(entries, intent, atTarget: true, token).ConfigureAwait(false);
            return result;
        }
        catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
