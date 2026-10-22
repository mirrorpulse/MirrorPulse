using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A fresh local namespace observation; it neither releases fences nor freezes future edits.</summary>
public sealed record MirrorPulseNamespaceCurrentTreeAudit(MirrorPulseNamespaceCurrentTreeSnapshot Inventory,
    string RoleSid, DateTimeOffset ObservedAt);

/// <summary>Audits all current original, moved and born members against the actual local inventory.</summary>
/// <remarks>Uses the shared actor, role, permission admission and live anchor guard. Source access, hydration,
/// permission writes, registration, readiness and upload acknowledgment are outside this read-only boundary.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceCurrentTreeAuditCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseNamespacePermissionCoordinator permissions)
{
    public ValueTask<MirrorPulseNamespaceCurrentTreeAudit> AuditAsync(Guid anchorMemberId, MirrorPulseNamespaceAnchorProtection anchorGuard,
        CancellationToken cancellationToken = default) => AuditAdmittedAsync(anchorMemberId, anchorGuard, null, cancellationToken);

    /// <summary>Audits the complete current membership and actual native inventory of one configured root.</summary>
    public ValueTask<MirrorPulseNamespaceCurrentTreeAudit> AuditRootAsync(Guid anchorMemberId, MirrorPulseNamespaceAnchorProtection anchorGuard,
        RootId rootId, CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A configured root identity is required.", nameof(rootId));
        return AuditAdmittedAsync(anchorMemberId, anchorGuard, null, cancellationToken, rootId);
    }

    /// <summary>Completes the initializer inside the same shared actor, role and permission admission as its fresh audit.</summary>
    /// <remarks>A historical receipt does not supply a new native audit or authorize this Host's source dispatch.</remarks>
    public async ValueTask<MirrorPulseNamespaceInitializationCompletion> CompleteInitializationAsync(Guid operationId, Guid anchorMemberId,
        MirrorPulseNamespaceAnchorProtection anchorGuard, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.", nameof(operationId));
        if (await catalog.ReadNamespaceInitializationCompletionAsync(operationId, cancellationToken).ConfigureAwait(false) is { } historical)
        {
            if (historical.InventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
                throw new InvalidOperationException("This operation completed the original inventory rather than the current inventory.");
            return historical;
        }
        MirrorPulseNamespaceInitializationCompletion completion = null!;
        var intent = await catalog.ReadNamespaceInitializationAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Current audit requires its retained initialization admission.");
        RootId? scope = intent.Version == 4 ? intent.Roots[0].RootId : null;
        _ = await AuditAdmittedAsync(anchorMemberId, anchorGuard, async (audit, token) =>
        {
            actor.AssertAccess();
            completion = await catalog.CompleteNamespaceCurrentInitializationAsync(operationId, audit.Inventory, audit.ObservedAt, token).ConfigureAwait(false);
        }, cancellationToken, scope).ConfigureAwait(false);
        return completion;
    }

    private ValueTask<MirrorPulseNamespaceCurrentTreeAudit> AuditAdmittedAsync(Guid anchorMemberId, MirrorPulseNamespaceAnchorProtection anchorGuard,
        Func<MirrorPulseNamespaceCurrentTreeAudit, CancellationToken, Task>? onAudited, CancellationToken cancellationToken,
        RootId? scopeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(anchorGuard);
        return actor.RunAsync(async token =>
        {
            MirrorPulseNamespaceCurrentTreeAudit result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                result = await permissions.RunCurrentTreeAuditAsync(anchorMemberId, async (admission, stop) =>
                {
                    actor.AssertAccess();
                    var inventory = admission.Inventory;
                    var members = inventory.Members.ToDictionary(member => member.Context.Member.MemberId);
                    var anchor = members[anchorMemberId];
                    if (members.Values.Any(member => member.Context.Protection.RoleSid != execution.RoleSid.Value ||
                        member.Context.Member.OwnerSid != execution.OwnerSid.Value))
                        throw new InvalidOperationException("Every current member requires this Host's live role and original current-user owner.");
                    var before = await anchorGuard.InspectAsync(stop).ConfigureAwait(false);
                    if (before.LocalObject != anchor.Context.Member.LocalObject || before.OwnerSid != execution.OwnerSid.Value ||
                        before.RootId is not null || before.RelativePath.Length != 0 || !before.IsDirectory || before.LinkCount != 1 ||
                        before.Dacl != anchor.Context.Verification.Dacl || anchorGuard.Original.LocalObject != before.LocalObject)
                        throw new InvalidDataException("The current audit lost its live protected sync-root and ancestor guard.");
                    var roots = inventory.Members.Where(member => member.Context.Location.ParentMemberId == anchorMemberId).ToArray();
                    var registrations = router.Registrations.Where(root => scopeRoot is null || root.RootId == scopeRoot)
                        .ToDictionary(root => root.RootId);
                    if (roots.Length != registrations.Count || roots.Any(member => !member.Context.Member.IsDirectory ||
                        member.Context.Member.RootId is not { } id || !registrations.TryGetValue(id, out var root) ||
                        member.Context.Location.RelativePath != root.DirectoryName))
                        throw new InvalidDataException("The current graph does not match the complete configured root layout.");
                    var actualRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var selectedNames = roots.Select(root => root.Context.Location.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    await foreach (var item in fileSystem.Root.EnumerateLocalChildrenAsync(cancellationToken: stop).ConfigureAwait(false))
                    {
                        if (scopeRoot is not null && !selectedNames.Contains(item.RelativePath.Replace('\\', '/'))) continue;
                        if (item.Kind != CloudItemKind.Directory || !actualRootNames.Add(item.RelativePath.Replace('\\', '/')))
                            throw new InvalidDataException("The actual sync-root contains an unexpected first-level entry.");
                    }
                    if (!actualRootNames.SetEquals(roots.Select(root => root.Context.Location.RelativePath)))
                        throw new InvalidDataException("The actual first-level roots differ from the current graph.");
                    foreach (var member in inventory.Members)
                    {
                        var location = member.Context.Location;
                        CloudItem item = member.Context.Member.IsDirectory ? fileSystem.GetDirectory(location.RelativePath) : fileSystem.GetFile(location.RelativePath);
                        var parent = location.ParentMemberId is { } parentId ? members[parentId] : null;
                        await MirrorPulseNamespaceMemberRegistrar.InspectCurrentAuditAsync(admission, member, parent, item,
                            parent is null ? null : fileSystem.GetDirectory(parent.Context.Location.RelativePath), router, actor, stop).ConfigureAwait(false);
                    }
                    foreach (var root in roots)
                    {
                        var expected = inventory.Members.Where(member => member.Context.Member.RootId == root.Context.Member.RootId)
                            .ToDictionary(member => member.Context.Location.RelativePath, StringComparer.OrdinalIgnoreCase);
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        await using var lease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                            fileSystem.GetDirectory(root.Context.Location.RelativePath), router, stop).ConfigureAwait(false);
                        await foreach (var actual in lease.InspectObjectsAsync(stop).ConfigureAwait(false))
                        {
                            if (!expected.TryGetValue(actual.RelativePath, out var retained) || !seen.Add(actual.RelativePath) ||
                                actual.LocalObject != retained.Context.Member.LocalObject || actual.OwnerSid != retained.Context.Member.OwnerSid ||
                                actual.RootId != retained.Context.Member.RootId || actual.IsDirectory != retained.Context.Member.IsDirectory || actual.LinkCount != 1 ||
                                actual.Dacl != retained.Context.Verification.Dacl)
                                throw new InvalidDataException("The complete actual local inventory differs from its current protected membership.");
                            // Repeat the mature public identity check after final local enumeration.
                            var observed = await lease.GetItem(actual.RelativePath).InspectAsync(stop).ConfigureAwait(false);
                            MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(observed, retained.Identity,
                                retained.Context.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
                        }
                        if (seen.Count != expected.Count) throw new InvalidDataException("The actual current tree lost a registered member.");
                    }
                    MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await anchorGuard.InspectAsync(stop).ConfigureAwait(false));
                    actor.AssertAccess(); return new MirrorPulseNamespaceCurrentTreeAudit(inventory, execution.RoleSid.Value, DateTimeOffset.UtcNow);
                }, token, onAudited, scopeRoot).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, cancellationToken);
    }
}
