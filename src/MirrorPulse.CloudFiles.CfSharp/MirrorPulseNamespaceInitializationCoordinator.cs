using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A completed inventory attempt and the live anchor guard retained by its runtime.</summary>
/// <remarks>Historical replay carries no new guard or current-tree authorization.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceInitializationResult(MirrorPulseNamespaceInitializationCompletion completion,
    MirrorPulseNamespaceAnchorProtection? anchor) : IAsyncDisposable
{
    private MirrorPulseNamespaceAnchorProtection? _anchor = anchor;
    public MirrorPulseNamespaceInitializationCompletion Completion { get; } = completion;
    public bool IsHistoricalReplay { get; } = anchor is null;
    internal MirrorPulseNamespaceAnchorProtection TransferAnchor() => Interlocked.Exchange(ref _anchor, null)
        ?? throw new InvalidOperationException("The initialization has no live anchor guard to transfer.");
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _anchor, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Composes original capture, protected recovery, anchor protection, registration and final audit.</summary>
/// <remarks>
/// All admitted roots are fenced durably before local work. Every original capture is sealed
/// before any parent permission write. Later moved or born inventories require their own current
/// graph recovery, not recapture through this original-inventory path. Source work belongs outside.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceInitializationCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    public async ValueTask<MirrorPulseNamespaceInitializationResult> InitializeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.", nameof(operationId));
        var retained = await catalog.ReadNamespaceInitializationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (retained?.RequestedInventoryKind is { } requested && requested != MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
            throw new InvalidOperationException("The pending initializer requires current-inventory recovery.");
        if (await catalog.ReadNamespaceInitializationCompletionAsync(operationId, cancellationToken).ConfigureAwait(false) is { } completed)
        {
            if (completed.InventoryKind != MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
                throw new InvalidOperationException("This operation completed the current inventory rather than the original inventory.");
            return new(completed, null);
        }
        var registrations = router.Registrations.OrderBy(root => root.RootId.Value).ToArray();
        if (retained is null)
        {
            var roots = await SelectOriginalRootsAsync(catalog, registrations, false, cancellationToken).ConfigureAwait(false);
            await RequireOriginalGraphAsync(catalog, registrations, roots, cancellationToken).ConfigureAwait(false);
            retained = await catalog.PrepareNamespaceInitializationAsync(new(roots.Count == 0 ? 3 : 1, operationId, roots, DateTimeOffset.UtcNow)
            { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory }, cancellationToken).ConfigureAwait(false);
        }
        if (!retained.Roots.Select(root => root.RootId).ToHashSet().SetEquals(registrations.Select(root => root.RootId)))
            throw new InvalidDataException("The initialized root layout changed after admission.");
        await EnsureExistingRootInventoryAsync(registrations, cancellationToken).ConfigureAwait(false);
        var capture = new MirrorPulseNamespaceInitialTreeCapture(fileSystem, catalog, router, execution, actor, scheduler, permissions);
        foreach (var root in retained.Roots)
            _ = await capture.CaptureAsync(root.RootId, root.ManifestId, cancellationToken).ConfigureAwait(false);
        var recovery = new MirrorPulseNamespaceOriginalTreeRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions);
        foreach (var root in retained.Roots)
        {
            var recovered = await recovery.RecoverAsync(root.ManifestId, cancellationToken).ConfigureAwait(false);
            if (recovered.Outcome != MirrorPulseNamespacePermissionTreeOutcome.Verified)
                throw new IOException("The original root requires permission or membership recovery before initialization can complete.");
        }
        var anchor = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
            .ProtectAsync(cancellationToken).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            var registration = new MirrorPulseNamespaceOriginalTreeRegistration(fileSystem, catalog, router, execution, actor, scheduler);
            var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            if (retained.Roots.Count == 0)
            {
                var member = await registration.RegisterAnchorAsync(anchor, cancellationToken).ConfigureAwait(false);
                members.Add(member.MemberId, member);
            }
            foreach (var root in retained.Roots)
                foreach (var member in await registration.RegisterAsync(root.ManifestId, anchor, cancellationToken).ConfigureAwait(false))
                    members.TryAdd(member.MemberId, member);
            var completion = await actor.RunAsync(async token =>
            {
                MirrorPulseNamespaceInitializationCompletion result = null!;
                await execution.RunNamespaceOperationAsync(async () =>
                {
                    var snapshots = new List<MirrorPulseNamespaceMemberProtectionSnapshot>(members.Count);
                    foreach (var member in members.Values.OrderBy(member => member.RelativePath.Count(character => character == '/'))
                        .ThenBy(member => member.RelativePath, StringComparer.Ordinal))
                    {
                        var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("An initialized member disappeared before final audit.");
                        CloudItem item = member.IsDirectory ? fileSystem.GetDirectory(snapshot.Context.Location.RelativePath) :
                            fileSystem.GetFile(snapshot.Context.Location.RelativePath);
                        CloudDirectory? parent = snapshot.Context.Location.ParentMemberId is { } parentId
                            ? fileSystem.GetDirectory(members[parentId].RelativePath) : null;
                        _ = await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, token).ConfigureAwait(false);
                        snapshots.Add(snapshot);
                    }
                    // Re-enumerate every actual root after the final per-object checks. Unexpected
                    // children cannot be hidden by the immutable first inventory or member counts.
                    foreach (var root in retained.Roots)
                        await AuditCurrentLocalInventoryAsync(root.RootId, members, token).ConfigureAwait(false);
                    result = await catalog.CompleteNamespaceInitializationAsync(operationId, snapshots, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return result;
            }, cancellationToken).ConfigureAwait(false);
            transferred = true; return new(completion, anchor);
        }
        finally { if (!transferred) await anchor.DisposeAsync().ConfigureAwait(false); }
    }

    internal static async Task<IReadOnlyList<MirrorPulseNamespaceInitializationRoot>> SelectOriginalRootsAsync(MirrorPulseProductCatalog catalog,
        IReadOnlyList<RootRegistration> registrations, bool requireSealed, CancellationToken token)
    {
        var roots = new List<MirrorPulseNamespaceInitializationRoot>(registrations.Count);
        foreach (var root in registrations)
        {
            var captures = new List<MirrorPulseNamespacePermissionTree>(); Guid? after = null;
            while (true)
            {
                var page = await catalog.ReadNamespacePermissionTreesAsync(root.RootId, after, cancellationToken: token).ConfigureAwait(false);
                if (page.Count == 0) break;
                captures.AddRange(page.Where(tree => tree.Phase != MirrorPulseNamespacePermissionTreePhase.Cancelled));
                after = page[^1].Definition.ManifestId;
            }
            if (captures.Count > 1) throw new InvalidDataException("The root has multiple original captures requiring explicit recovery.");
            if (requireSealed && (captures.Count != 1 || captures[0].Phase != MirrorPulseNamespacePermissionTreePhase.Sealed))
                throw new InvalidOperationException("Current graph recovery requires the original sealed root captures.");
            roots.Add(new(root.RootId, captures.SingleOrDefault()?.Definition.ManifestId ?? Guid.NewGuid()));
        }
        return roots.AsReadOnly();
    }

    internal static async Task RequireOriginalGraphAsync(MirrorPulseProductCatalog catalog,
        IReadOnlyList<RootRegistration> registrations, IReadOnlyList<MirrorPulseNamespaceInitializationRoot> roots, CancellationToken token)
    {
        var anchors = new HashSet<Guid>();
        var selected = roots.Select(root => root.RootId).ToHashSet();
        foreach (var root in roots)
        {
            var capture = await catalog.ReadNamespacePermissionTreeAsync(root.ManifestId, token).ConfigureAwait(false);
            if (capture is null) continue;
            if (capture.Definition.Anchor.Baseline.RelativePath != registrations.Single(item => item.RootId == root.RootId).DirectoryName)
                throw new InvalidOperationException("The root Label changed; use current-inventory recovery instead of original initialization.");
            var member = await catalog.ReadNamespaceMemberByBindingAsync(capture.Definition.Anchor.Baseline.LocalObject, token).ConfigureAwait(false);
            if (member is null) continue;
            Guid anchor = member.ParentMemberId ?? throw new InvalidDataException("The registered Adapter root lost its sync-root anchor.");
            if (!anchors.Add(anchor)) continue;
            var current = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor, token).ConfigureAwait(false);
            if (current.RetiredMembers.Any(item => selected.Contains(item.Intent.RootId)) ||
                current.Members.Any(item => item.Context.Member.RootId is { } id && selected.Contains(id) &&
                    (item.Context.Member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal ||
                    item.Context.Location.CurrentPositionOperationId is not null ||
                    item.Context.Location.RelativePath != item.Context.Member.RelativePath)))
                throw new InvalidOperationException("The namespace has evolved; use current-inventory recovery instead of original initialization.");
        }
    }

    private async Task EnsureExistingRootInventoryAsync(IReadOnlyList<RootRegistration> registrations, CancellationToken token)
    {
        await actor.RunAsync(async stop =>
        {
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var expected = registrations.ToDictionary(root => root.DirectoryName, StringComparer.OrdinalIgnoreCase);
                var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await foreach (var item in fileSystem.Root.EnumerateLocalChildrenAsync(cancellationToken: stop).ConfigureAwait(false))
                    if (item.Kind != CloudItemKind.Directory || !expected.ContainsKey(item.RelativePath.Replace('\\', '/')) ||
                        !observed.Add(item.RelativePath.Replace('\\', '/')))
                        throw new InvalidDataException("An unregistered sync-root entry requires explicit recovery before parent permissions can change.");
                if (observed.Count != expected.Count)
                    throw new InvalidOperationException("Configured root entries must exist before original-tree initialization.");
            }).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task AuditCurrentLocalInventoryAsync(RootId rootId, IReadOnlyDictionary<Guid, MirrorPulseNamespaceMemberRegistration> members,
        CancellationToken token)
    {
        var root = router.Registrations.Single(root => root.RootId == rootId);
        var expected = members.Values.Where(member => member.RootId == rootId).ToDictionary(member => member.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var lease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(fileSystem.GetDirectory(root.DirectoryName), router, token).ConfigureAwait(false);
        await foreach (var item in lease.InspectObjectsAsync(token).ConfigureAwait(false))
        {
            if (!expected.TryGetValue(item.RelativePath, out var member) || !seen.Add(item.RelativePath) ||
                item.LocalObject != member.LocalObject || item.OwnerSid != member.OwnerSid || item.IsDirectory != member.IsDirectory || item.LinkCount != 1)
                throw new InvalidDataException("The final local inventory differs from its native-audited original members.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("The final local inventory lost an initialized original.");
    }
}
