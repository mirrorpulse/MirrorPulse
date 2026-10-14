using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Registers the actual protected sync-root anchor and a sealed original tree parent first.</summary>
/// <remarks>
/// Every registration uses the existing native registrar and retained current permission epoch.
/// Partial registration preserves its first provenance on replay. This boundary does not apply
/// permissions, recapture moved or born members, acknowledge content or establish tree readiness.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceOriginalTreeRegistration(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseInstanceScheduler scheduler)
{
    public async ValueTask<IReadOnlyList<MirrorPulseNamespaceMemberRegistration>> RegisterAsync(Guid manifestId,
        MirrorPulseNamespaceAnchorProtection anchor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var tree = await catalog.ReadNamespacePermissionTreeAsync(manifestId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original tree capture is missing.");
        if (tree.Phase != MirrorPulseNamespacePermissionTreePhase.Sealed)
            throw new InvalidOperationException("Initial member registration requires a sealed original capture.");
        var registration = router.Registrations.Single(root => root.RootId == tree.Definition.Anchor.Intent.RootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            actor.AssertAccess(); IReadOnlyList<MirrorPulseNamespaceMemberRegistration> result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var current = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
                if (current.RootId != registration.RootId || current.DirectoryName != tree.Definition.Anchor.Intent.RelativePath ||
                    anchor.Original.RootId is not null || anchor.Original.RelativePath.Length != 0 ||
                    anchor.Change.Intent.RoleSid != execution.RoleSid.Value || anchor.Change.Phase != MirrorPulseNamespacePermissionPhase.Verified ||
                    anchor.Result.Permission?.Outcome is not (MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified) ||
                    anchor.Result.Receipt is not { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true })
                    throw new InvalidDataException("Initial registration requires the unchanged root mapping and verified live anchor role.");
                if (await catalog.ReadNamespacePermissionChangeAsync(anchor.Change.Intent.OperationId, stop).ConfigureAwait(false) != anchor.Change)
                    throw new InvalidDataException("The admitted anchor permission epoch changed.");
                // Revalidate the complete immutable seal before registering even the anchor.
                await catalog.SealNamespacePermissionTreeAsync(manifestId, tree.Seal!.SealedAt, stop).ConfigureAwait(false);
                var captures = new List<MirrorPulseNamespacePermissionTreeMember>(tree.CapturedMembers);
                for (int after = -1; captures.Count < tree.CapturedMembers;)
                {
                    var page = await catalog.ReadNamespacePermissionTreeMembersAsync(manifestId, after, 256, stop).ConfigureAwait(false);
                    if (page.Count == 0) throw new InvalidDataException("The original member inventory lost a page.");
                    captures.AddRange(page); after = page[^1].Sequence;
                }
                if (captures.Count != tree.CapturedMembers) throw new InvalidDataException("The sealed original member count changed.");
                await using var lease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                    fileSystem.GetDirectory(current.DirectoryName), router, stop).ConfigureAwait(false);
                await AuditInventoryAsync(lease, captures, stop).ConfigureAwait(false);
                var registered = new List<MirrorPulseNamespaceMemberRegistration>(captures.Count + 1);
                var root = await RegisterOneAsync(anchor.Original, null, fileSystem.Root, null, stop).ConfigureAwait(false);
                registered.Add(root);
                var parents = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
                foreach (var capture in captures)
                {
                    var baseline = capture.Preparation.Baseline;
                    var parent = capture.ParentEvidenceId is { } parentEvidence ? parents[parentEvidence] : root;
                    CloudDirectory parentItem = parent.RootId is null ? fileSystem.Root : fileSystem.GetDirectory(parent.RelativePath);
                    var member = await RegisterOneAsync(baseline, parent.MemberId, lease.GetItem(baseline.RelativePath), parentItem, stop).ConfigureAwait(false);
                    parents.Add(baseline.EvidenceId, member); registered.Add(member);
                }
                await AuditInventoryAsync(lease, captures, stop).ConfigureAwait(false);
                result = registered.AsReadOnly();
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceMemberRegistration> RegisterOneAsync(MirrorPulseNamespacePermissionBaseline original,
        Guid? parentId, CloudItem item, CloudDirectory? parent, CancellationToken token)
    {
        var latest = await catalog.ReadLatestNamespacePermissionChangeAsync(original.EvidenceId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original member has no retained permission epoch.");
        if (latest.Phase != MirrorPulseNamespacePermissionPhase.Verified || latest.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore ||
            latest.Intent.RoleSid != execution.RoleSid.Value || latest.Intent.RelativePath != original.RelativePath)
            throw new InvalidDataException("Original registration requires verified current-role protection at the retained original location.");
        var member = await catalog.ReadNamespaceMemberByBindingAsync(original.LocalObject, token).ConfigureAwait(false);
        if (member is not null && (member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || member.OriginId != original.EvidenceId ||
            member.ParentMemberId != parentId || member.RootId != original.RootId || member.RelativePath != original.RelativePath))
            throw new InvalidDataException("Initial registration cannot replace or relocate existing membership provenance.");
        member ??= new(1, original.EvidenceId, parentId, MirrorPulseNamespaceMemberOrigin.CapturedOriginal, original.EvidenceId,
            latest.Intent.OperationId, original.RootId, original.RelativePath, original.LocalObject, original.IsDirectory,
            original.OwnerSid, DateTimeOffset.UtcNow);
        return await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, token).ConfigureAwait(false);
    }

    private async Task AuditInventoryAsync(MirrorPulseWindowsNamespacePermissionTreeLease lease,
        IReadOnlyList<MirrorPulseNamespacePermissionTreeMember> captures, CancellationToken token)
    {
        var expected = captures.ToDictionary(capture => capture.Preparation.Baseline.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var observed in lease.InspectObjectsAsync(token).ConfigureAwait(false))
        {
            if (!expected.TryGetValue(observed.RelativePath, out var captured) || !seen.Add(observed.RelativePath))
                throw new InvalidDataException("The local original tree contains an unadmitted object.");
            var baseline = captured.Preparation.Baseline;
            var latest = await catalog.ReadLatestNamespacePermissionChangeAsync(baseline.EvidenceId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original member lost its current permission epoch.");
            if (latest.Phase != MirrorPulseNamespacePermissionPhase.Verified || latest.Intent.RoleSid != execution.RoleSid.Value ||
                latest.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore || latest.Intent.RelativePath != observed.RelativePath ||
                observed.LocalObject != baseline.LocalObject || observed.OwnerSid != baseline.OwnerSid || observed.RootId != baseline.RootId ||
                observed.IsDirectory != baseline.IsDirectory || observed.LinkCount != 1 ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(latest.Verification!.Dacl, observed.Dacl))
                throw new InvalidDataException("The live original tree disagrees with its current-role permission evidence.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("The local original tree lost a captured member.");
    }
}
