using System.ComponentModel;
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A safe startup result for one stable root; completion is local and does not assert remote acceptance.</summary>
public sealed record MirrorPulseNamespaceStartupRoot(RootId RootId, Guid? OperationId, bool Admitted, string? RecoveryCode)
{
    /// <summary>The actual Win32 exception code, when available; never decoded from a generic managed HRESULT.</summary>
    public int? NativeErrorCode { get; init; }
}

/// <summary>Bootstraps the local namespace before Workers and background source polling start.</summary>
/// <remarks>The caller serializes the complete library lifetime. Roots start closed, retained
/// admissions keep their original route, and only a fresh native audit grants root admission.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseNamespaceStartupCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    IMirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions, MirrorPulseNamespaceWorkAdmission work,
    Action<string>? progress = null,
    Func<RootRegistration, CancellationToken, ValueTask>? recoverRemoteDelete = null)
{
    internal async ValueTask<(MirrorPulseNamespaceAnchorProtection Guard, IReadOnlyList<MirrorPulseNamespaceStartupRoot> Roots)> StartAsync(
        CancellationToken cancellationToken)
    {
        MirrorPulseNamespaceAnchorProtection? guard = null;
        var outcomes = new Dictionary<RootId, MirrorPulseNamespaceStartupRoot>();
        try
        {
            progress?.Invoke("NamespaceAnchorBegin");
            // Freeze the actual anchor and ancestor names before the first discovery.
            // This retains handles only; existing originals are still captured before ACL writes.
            MirrorPulseWindowsNamespacePermissionLease? entryGuard = null;
            progress?.Invoke("NamespaceAnchorEntryGuardBegin");
            await execution.RunNamespaceOperationAsync(async () =>
                entryGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorEntryGuardCompleted");
            await using var retainedEntryGuard = entryGuard;
            var configured = router.Registrations.ToDictionary(root => root.RootId);
            progress?.Invoke("NamespaceAnchorDiscoveryBegin");
            var existing = await ReadExistingRootsAsync(configured.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorDiscoveryCompleted");
            foreach (var pending in await catalog.ReadPendingNamespaceInitializationsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (pending.Intent.RequestedInventoryKind is null || pending.Intent.Roots.Any(root => !configured.ContainsKey(root.RootId)))
                    throw new InvalidDataException("Startup cannot replace an unclassified or no-longer-configured initialization admission.");
                var selected = new MirrorPulseRootRouter(fileSystem.Root.FullPath,
                    pending.Intent.Roots.Select(root => configured[root.RootId]));
                await using var recovery = await work.BeginRecoveryAsync(pending.Intent.Roots.Select(root => root.RootId), cancellationToken).ConfigureAwait(false);
                try
                {
                    MirrorPulseNamespaceInitializationResult result;
                    if (pending.Intent.RequestedInventoryKind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
                        result = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, selected, execution, actor, scheduler, permissions)
                            .InitializeAsync(pending.Intent.OperationId, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var coordinator = new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, selected, execution, actor, scheduler, permissions) { Progress = progress };
                        result = pending.Intent.Version == 4
                            ? await coordinator.RecoverRootAsync(pending.Intent.OperationId, pending.Intent.Roots[0].RootId, cancellationToken).ConfigureAwait(false)
                            : await coordinator.RecoverAsync(pending.Intent.OperationId, cancellationToken).ConfigureAwait(false);
                    }
                    // These guards belong to the same current role. The final guard below remains
                    // retained for the complete Host lifetime, including a later per-root failure.
                    var prior = guard; guard = result.TransferAnchor();
                    if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
                {
                    foreach (var root in pending.Intent.Roots)
                        outcomes[root.RootId] = new(root.RootId, pending.Intent.OperationId, false, "PendingInitializationRecoveryRequired")
                        { NativeErrorCode = (error as Win32Exception)?.NativeErrorCode };
                }
            }

            var nativeAnchor = await fileSystem.Root.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (nativeAnchor.LocalBinding is not { } binding)
                throw new InvalidDataException("Startup requires an actual sync-root binding.");
            var anchor = await catalog.ReadNamespaceMemberByBindingAsync(new(binding.VolumeSerialNumber, binding.SyncRootFileId,
                binding.LocalFileId), cancellationToken).ConfigureAwait(false);
            if (anchor is null)
            {
                if (outcomes.Count != 0) throw new IOException("Original namespace initialization requires its retained recovery before anchor registration.");
                var existingRouter = new MirrorPulseRootRouter(fileSystem.Root.FullPath, existing);
                progress?.Invoke("NamespaceAnchorInitializationBegin");
                var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, existingRouter,
                    execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                progress?.Invoke("NamespaceAnchorInitializationCompleted");
                var prior = guard; guard = initialized.TransferAnchor();
                if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
            }
            var previousGuard = guard;
            progress?.Invoke("NamespaceAnchorProtectionBegin");
            guard = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                .ProtectAsync(cancellationToken).ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorProtectionCompleted");
            if (previousGuard is not null) await previousGuard.DisposeAsync().ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorRegistrationBegin");
            anchor = await new MirrorPulseNamespaceOriginalTreeRegistration(fileSystem, catalog, router, execution, actor, scheduler)
                .RegisterAnchorAsync(guard, cancellationToken).ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorRegistrationCompleted");
            // Ordinary first-level creation is now denied by the anchor ACL. Detect any
            // entry inserted during capture before admitting even the first configured root.
            _ = await ReadExistingRootsAsync(configured.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            progress?.Invoke("NamespaceAnchorCompleted");
            if (configured.Count == 0)
            {
                progress?.Invoke("NamespaceEmptyInventoryRecoveryBegin");
                var result = await new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions) { Progress = progress }
                    .RecoverAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                var prior = guard; guard = result.TransferAnchor(); await prior.DisposeAsync().ConfigureAwait(false);
                progress?.Invoke("NamespaceEmptyInventoryRecoveryCompleted");
            }

            var births = await ReadUnregisteredBirthsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var root in configured.Values.OrderBy(root => root.RootId.Value))
            {
                if (outcomes.ContainsKey(root.RootId)) continue;
                Guid? operationId = null;
                try
                {
                    progress?.Invoke("NamespaceRootBegin");
                    if (root.State == RootRegistrationState.Active && recoverRemoteDelete is not null)
                        await recoverRemoteDelete(root, cancellationToken).ConfigureAwait(false);
                    var child = await catalog.ReadNamespaceMemberChildAsync(anchor.MemberId, root.DirectoryName, cancellationToken).ConfigureAwait(false);
                    var pendingOperations = await catalog.ReadNamespacePendingOperationsAsync(root.RootId, cancellationToken).ConfigureAwait(false);
                    if (child is not null && pendingOperations.Count != 0)
                    {
                        // A new initializer would fence the original operation's own executor.
                        // Keep its retry identity and closed admission until that work settles.
                        outcomes[root.RootId] = new(root.RootId, pendingOperations[0].OperationId, false,
                            "PendingNamespaceOperationRecoveryRequired");
                        continue;
                    }
                    if (child is null)
                    {
                        // A missing configured name is new only when no original history owns it.
                        // Never adopt a replacement or replay an unknown native creation by path.
                        Guid? afterManifest = null;
                        while (true)
                        {
                            var originals = await catalog.ReadNamespacePermissionTreesAsync(root.RootId, afterManifest, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (originals.Any(tree => tree.Phase != MirrorPulseNamespacePermissionTreePhase.Cancelled))
                                throw new InvalidDataException("A configured root lost its original registered object.");
                            if (originals.Count == 0) break;
                            afterManifest = originals[^1].Definition.ManifestId;
                        }
                        var pending = births.Where(birth => birth.Intent.RootId == root.RootId).ToArray();
                        if (pending.Any(birth => birth.Intent.RelativePath != root.DirectoryName ||
                            birth.Intent.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation) || pending.Length > 1)
                            throw new InvalidDataException("The new root has unresolved namespace work that cannot be replaced at startup.");
                        operationId = pending.SingleOrDefault()?.Intent.OperationId ?? Guid.NewGuid();
                        var specification = router.CreateRootPage().Children.Single(spec => spec.Name == root.DirectoryName);
                        child = await new MirrorPulseNamespaceRemoteCreationCoordinator(fileSystem, catalog, router, execution, actor)
                            .CreateAsync(operationId.Value, anchor.MemberId, specification, cancellationToken).ConfigureAwait(false);
                    }
                    operationId = Guid.NewGuid();
                    await using var recovery = await work.BeginRecoveryAsync([root.RootId], cancellationToken).ConfigureAwait(false);
                    var result = await new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions) { Progress = progress }
                        .RecoverRootAsync(operationId.Value, root.RootId, cancellationToken).ConfigureAwait(false);
                    await result.DisposeAsync().ConfigureAwait(false);
                    recovery.CompleteAfterFreshAudit();
                    outcomes[root.RootId] = new(root.RootId, operationId, true, null);
                    progress?.Invoke("NamespaceRootCompleted");
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
                {
                    outcomes[root.RootId] = new(root.RootId, operationId, false, "NamespaceStartupRecoveryRequired")
                    { NativeErrorCode = (error as Win32Exception)?.NativeErrorCode };
                }
            }
            return (guard, outcomes.Values.OrderBy(result => result.RootId.Value).ToArray());
        }
        catch { if (guard is not null) await guard.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<IReadOnlyList<RootRegistration>> ReadExistingRootsAsync(IReadOnlyList<RootRegistration> configured, CancellationToken token)
    {
        var existing = new List<RootRegistration>();
        await foreach (var child in fileSystem.Root.EnumerateLocalChildrenAsync(cancellationToken: token).ConfigureAwait(false))
        {
            var root = configured.SingleOrDefault(root => root.DirectoryName == child.RelativePath.Replace('\\', '/'));
            if (child.Kind != CloudItemKind.Directory || root is null)
                throw new InvalidDataException("An unknown first-level entry prevents namespace startup.");
            existing.Add(root);
        }
        return existing;
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceBirthRecoveryEntry>> ReadUnregisteredBirthsAsync(CancellationToken token)
    {
        var scan = await catalog.BeginNamespaceBirthRecoveryScanAsync(token).ConfigureAwait(false);
        var result = new List<MirrorPulseNamespaceBirthRecoveryEntry>(); long after = 0;
        while (true)
        {
            var page = await catalog.ReadNamespaceBirthRecoveryPageAsync(scan, after, cancellationToken: token).ConfigureAwait(false);
            result.AddRange(page.Entries.Where(entry => entry.MemberRegistration is null));
            if (result.Count > 4096) throw new InvalidDataException("Unregistered birth recovery exceeds its startup bound.");
            if (!page.HasMore) break;
            if (page.LastScannedSequence <= after) throw new InvalidDataException("Birth discovery did not advance.");
            after = page.LastScannedSequence;
        }
        return result;
    }
}
