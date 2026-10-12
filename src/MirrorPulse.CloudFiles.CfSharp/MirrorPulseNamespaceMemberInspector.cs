using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A path lookup of native membership; it carries no mutation or tree-readiness authority.</summary>
public sealed record MirrorPulseNamespaceMemberInspection(Guid MemberId, Guid? ActualParentMemberId,
    RootId? RootId, string RelativePath, bool IsDirectory, Guid? NativeItemId, DateTimeOffset ObservedAt);

[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceMemberInspector(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    /// <summary>Looks up a local object by its complete binding without reading or hydrating content.</summary>
    public async ValueTask<MirrorPulseNamespaceMemberInspection> InspectAsync(string relativePath, bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return await actor.RunAsync(async token => await execution.RunNormalUserOperationAsync(async () =>
        {
            actor.AssertAccess();
            CloudItem item = isDirectory ? fileSystem.GetDirectory(relativePath) : fileSystem.GetFile(relativePath);
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
            var facts = await lease.InspectAsync(token).ConfigureAwait(false);
            var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            if (!snapshot.Exists || snapshot.LocalBinding is not { } binding ||
                facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
                facts.LinkCount != 1 || facts.IsDirectory != isDirectory || snapshot.IsTombstone)
                throw new InvalidDataException("The selected path cannot provide unambiguous native membership.");
            var member = await catalog.ReadNamespaceMemberByBindingAsync(facts.LocalObject, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The selected native object has no registered namespace membership.");
            if (member.OwnerSid != facts.OwnerSid || member.RootId != facts.RootId || member.IsDirectory != facts.IsDirectory)
                throw new InvalidDataException("The selected object disagrees with its retained namespace membership.");
            Guid? parentId = null;
            if (item.Parent is { } parent)
            {
                await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
                var parentFacts = await parentLease.InspectAsync(token).ConfigureAwait(false);
                var parentMember = await catalog.ReadNamespaceMemberByBindingAsync(parentFacts.LocalObject, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The actual direct parent has no registered namespace membership.");
                if (!parentMember.IsDirectory || !parentFacts.IsDirectory || parentFacts.LinkCount != 1 ||
                    parentMember.OwnerSid != member.OwnerSid || parentFacts.OwnerSid != parentMember.OwnerSid || parentFacts.RootId != parentMember.RootId ||
                    parentMember.RootId is { } parentRoot && parentRoot != member.RootId)
                    throw new InvalidDataException("The actual direct parent belongs to another namespace.");
                parentId = parentMember.MemberId;
            }
            Guid? nativeItemId = snapshot.IsPlaceholder && !snapshot.PlaceholderIdentity.IsEmpty
                ? CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).ItemId : null;
            actor.AssertAccess();
            return new MirrorPulseNamespaceMemberInspection(member.MemberId, parentId, member.RootId, facts.RelativePath,
                facts.IsDirectory, nativeItemId, facts.ObservedAt);
        }).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
}
