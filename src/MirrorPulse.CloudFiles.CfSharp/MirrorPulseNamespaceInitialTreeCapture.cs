using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Captures every existing local original before any permission application.</summary>
/// <remarks>
/// Only the local inventory is enumerated. No content is read, hydrated or accepted, and no ACL
/// is written. An interrupted prefix retains its exact original evidence; a changed inventory
/// requires recovery instead of recapture. Sealing does not establish tree readiness.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceInitialTreeCapture(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    private const int PageSize = 256;

    public async ValueTask<MirrorPulseNamespacePermissionTree> CaptureAsync(RootId rootId, Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty || manifestId == Guid.Empty)
            throw new ArgumentException("A stable root and original capture manifest are required.");
        var registration = router.Registrations.Single(root => root.RootId == rootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            actor.AssertAccess();
            var retained = await catalog.ReadNamespacePermissionTreeAsync(manifestId, stop).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained.Definition.Anchor.Intent.RootId != rootId)
                    throw new InvalidOperationException("The original capture belongs to another root.");
                if (retained.Phase == MirrorPulseNamespacePermissionTreePhase.Cancelled)
                    throw new InvalidOperationException("A cancelled original capture cannot be resumed.");
                if (retained.Phase == MirrorPulseNamespacePermissionTreePhase.Sealed) return retained;
            }
            MirrorPulseNamespacePermissionTree result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var current = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
                if (current != registration) throw new InvalidOperationException("The root mapping changed before original capture.");
                await using var lease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                    fileSystem.GetDirectory(current.DirectoryName), router, stop).ConfigureAwait(false);
                var inventory = await ReadInventoryAsync(lease, rootId, stop).ConfigureAwait(false);
                if (retained is not null && (retained.Definition.ExpectedMembers != inventory.Count ||
                    retained.Definition.Anchor.Intent.RelativePath != current.DirectoryName))
                    throw new InvalidDataException("The interrupted original inventory changed.");
                var captured = new List<MirrorPulseNamespacePermissionTreeMember>(inventory.Count);
                if (retained is not null)
                {
                    for (int after = -1; captured.Count < retained.CapturedMembers;)
                    {
                        var page = await catalog.ReadNamespacePermissionTreeMembersAsync(manifestId, after, PageSize, stop).ConfigureAwait(false);
                        if (page.Count == 0) throw new InvalidDataException("The retained capture prefix lost a page.");
                        captured.AddRange(page); after = page[^1].Sequence;
                    }
                    if (captured.Count != retained.CapturedMembers) throw new InvalidDataException("The capture cursor disagrees with its prefix.");
                }
                DateTimeOffset created = retained?.Definition.CreatedAt ?? DateTimeOffset.UtcNow;
                string captureRole = retained?.Definition.Anchor.Intent.RoleSid ?? execution.RoleSid.Value;
                var parents = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < inventory.Count; index++)
                {
                    var observed = inventory[index];
                    MirrorPulseNamespacePermissionTreeMember member;
                    if (index < captured.Count || index == 0 && retained is not null)
                    {
                        member = index < captured.Count ? captured[index] : new(0, null, retained!.Definition.Anchor);
                        var original = member.Preparation.Baseline;
                        if (member.Sequence != index || original.RootId != observed.RootId || original.LocalObject != observed.LocalObject ||
                            original.RelativePath != observed.RelativePath || original.IsDirectory != observed.IsDirectory ||
                            original.OwnerSid != observed.OwnerSid || original.OriginalDacl != observed.Dacl ||
                            member.Preparation.Intent.RoleSid != captureRole)
                            throw new InvalidDataException("An interrupted original capture cannot replace its retained facts or role.");
                        if (index == captured.Count) captured.Add(member);
                    }
                    else
                    {
                        var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), rootId, observed.LocalObject,
                            observed.RelativePath, observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                        var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject,
                            rootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, captureRole,
                            baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution.OwnerSid,
                                new SecurityIdentifier(captureRole), baseline.IsDirectory), DateTimeOffset.UtcNow);
                        Guid? parent = index == 0 ? null : parents[ParentPath(observed.RelativePath)];
                        member = new(index, parent, new(baseline, intent));
                        captured.Add(member);
                    }
                    if (observed.IsDirectory) parents.Add(observed.RelativePath, member.Preparation.Baseline.EvidenceId);
                }
                var definition = retained?.Definition ?? new(manifestId, captured[0].Preparation, inventory.Count, created);
                result = await permissions.CaptureNamespacePermissionTreeAsync(
                    new(definition.Anchor.Baseline.LocalObject.VolumeSerialNumber, definition.Anchor.Baseline.LocalObject.SyncRootFileId, rootId),
                    async captureStop =>
                    {
                        var drained = await ReadInventoryAsync(lease, rootId, captureStop).ConfigureAwait(false);
                        if (drained.Count != inventory.Count || drained.Where((item, index) => !SameOriginal(item, inventory[index])).Any())
                            throw new InvalidDataException("The original inventory changed while permission applications drained.");
                        return definition;
                    }, async (tree, captureStop) =>
                    {
                        for (int index = tree.CapturedMembers; index < captured.Count; index += PageSize)
                            await catalog.AppendNamespacePermissionTreeMembersAsync(manifestId,
                                captured.Skip(index).Take(PageSize).ToArray(), captureStop).ConfigureAwait(false);
                        var final = await ReadInventoryAsync(lease, rootId, captureStop).ConfigureAwait(false);
                        if (final.Count != inventory.Count || final.Where((item, index) => !SameOriginal(item, inventory[index])).Any())
                            throw new InvalidDataException("The local inventory changed before original capture sealing.");
                        await catalog.SealNamespacePermissionTreeAsync(manifestId, DateTimeOffset.UtcNow, captureStop).ConfigureAwait(false);
                    }, stop).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<MirrorPulseNamespacePermissionObject>> ReadInventoryAsync(
        MirrorPulseWindowsNamespacePermissionTreeLease lease, RootId root, CancellationToken token)
    {
        var items = new List<MirrorPulseNamespacePermissionObject>();
        await foreach (var item in lease.InspectObjectsAsync(token).ConfigureAwait(false))
        {
            if (item.RootId != root || item.OwnerSid != execution.OwnerSid.Value || item.LinkCount != 1 || items.Count == 1_000_000)
                throw new InvalidDataException("The original capture requires bounded, single-link, current-user objects in the same root.");
            if (await catalog.ReadNamespaceMemberByBindingAsync(item.LocalObject, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Registered membership must be recovered through its retained provenance, not recaptured as an original.");
            items.Add(item);
        }
        return items.OrderBy(item => item.RelativePath.Count(character => character == '/'))
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static string ParentPath(string path) => path[..path.LastIndexOf('/')];

    private static bool SameOriginal(MirrorPulseNamespacePermissionObject left, MirrorPulseNamespacePermissionObject right) =>
        left.LocalObject == right.LocalObject && left.RootId == right.RootId && left.RelativePath == right.RelativePath &&
        left.IsDirectory == right.IsDirectory && left.OwnerSid == right.OwnerSid && left.Dacl == right.Dacl && left.LinkCount == right.LinkCount;
}
