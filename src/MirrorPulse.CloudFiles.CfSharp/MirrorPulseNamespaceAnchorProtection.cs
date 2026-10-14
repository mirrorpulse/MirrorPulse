using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains the actual sync-root and its ancestors after owned permission reconciliation.</summary>
/// <remarks>Ancestor ACLs are not changed. This lease is not whole-tree readiness or source authorization.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceAnchorProtection : IAsyncDisposable
{
    private readonly MirrorPulseWindowsNamespacePermissionLease _guard;
    public MirrorPulseNamespacePermissionBaseline Original { get; }
    public MirrorPulseNamespacePermissionChange Change { get; }
    public MirrorPulseProtectedNamespacePermissionResult Result { get; }

    internal MirrorPulseNamespaceAnchorProtection(MirrorPulseWindowsNamespacePermissionLease guard,
        MirrorPulseNamespacePermissionBaseline original, MirrorPulseNamespacePermissionChange change,
        MirrorPulseProtectedNamespacePermissionResult result) => (_guard, Original, Change, Result) = (guard, original, change, result);

    public ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken = default) => _guard.InspectAsync(cancellationToken);
    public ValueTask DisposeAsync() => _guard.DisposeAsync();
}

/// <summary>Recovers retained sync-root evidence before rotating its owned ACL to the live Host role.</summary>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceAnchorProtectionCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseNamespacePermissionCoordinator permissions)
{
    public ValueTask<MirrorPulseNamespaceAnchorProtection> ProtectAsync(CancellationToken cancellationToken = default) =>
        actor.RunAsync(async token =>
        {
            actor.AssertAccess(); MirrorPulseNamespaceAnchorProtection result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, token).ConfigureAwait(false);
                bool transferred = false;
                try
                {
                    var observed = await guard.InspectAsync(token).ConfigureAwait(false);
                    if (observed.RootId is not null || observed.RelativePath.Length != 0 || !observed.IsDirectory ||
                        observed.OwnerSid != execution.OwnerSid.Value || observed.LinkCount != 1 ||
                        observed.LocalObject.LocalFileId != observed.LocalObject.SyncRootFileId)
                        throw new InvalidDataException("Anchor protection requires the actual single-link current-user sync-root directory.");
                    var original = await catalog.ReadNamespacePermissionBaselineByBindingAsync(observed.LocalObject, token).ConfigureAwait(false);
                    if (original is null && (await catalog.ReadNamespacePermissionChangesAsync(token).ConfigureAwait(false))
                        .Any(change => change.Intent.RootId is null))
                        throw new InvalidDataException("The sync-root has retained permission history for a different or missing original binding.");
                    var latest = original is null ? null : await catalog.ReadLatestNamespacePermissionChangeAsync(original.EvidenceId, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The original sync-root permissions lost their owned intent.");
                    if (original is not null && (original.RootId is not null || original.RelativePath.Length != 0 ||
                        !original.IsDirectory || original.OwnerSid != observed.OwnerSid))
                        throw new InvalidDataException("The anchor cannot adopt another object's original permissions.");
                    original ??= new(Guid.NewGuid(), null, observed.LocalObject, string.Empty, true,
                        observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                    MirrorPulseProtectedNamespacePermissionResult applied = null!;
                    if (latest is not null)
                    {
                        applied = await permissions.ApplyProtectedAsync(latest.Intent.OperationId, fileSystem.Root, router,
                            cancellationToken: token).ConfigureAwait(false);
                        if (!Verified(applied)) { Transfer(); return; }
                        latest = await catalog.ReadNamespacePermissionChangeAsync(latest.Intent.OperationId, token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("The recovered anchor intent disappeared.");
                    }
                    if (latest is null || latest.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore || latest.Intent.RoleSid != execution.RoleSid.Value)
                    {
                        latest = await catalog.PrepareNamespacePermissionChangeAsync(original, new(Guid.NewGuid(), original.EvidenceId,
                            original.LocalObject, null, string.Empty, latest is null || latest.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore
                                ? MirrorPulseNamespacePermissionChangeKind.Protect : MirrorPulseNamespacePermissionChangeKind.RotateRole,
                            execution.RoleSid.Value, latest?.Verification!.Dacl ?? original.OriginalDacl,
                            MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, true), DateTimeOffset.UtcNow), token).ConfigureAwait(false);
                        applied = await permissions.ApplyProtectedAsync(latest.Intent.OperationId, fileSystem.Root, router,
                            cancellationToken: token).ConfigureAwait(false);
                        latest = await catalog.ReadNamespacePermissionChangeAsync(latest.Intent.OperationId, token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("The current anchor intent disappeared.");
                    }
                    var final = await guard.InspectAsync(token).ConfigureAwait(false);
                    if (Verified(applied) && (final.LocalObject != original.LocalObject || final.OwnerSid != original.OwnerSid ||
                        !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, true), final.Dacl)))
                        applied = applied with
                        {
                            Permission = new(latest!.Intent.OperationId,
                            MirrorPulseNamespacePermissionOutcome.RecoveryRequired, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged)
                        };
                    Transfer();

                    void Transfer()
                    {
                        result = new(guard, original, latest!, applied); transferred = true;
                    }
                }
                finally { if (!transferred) await guard.DisposeAsync().ConfigureAwait(false); }
            }).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    private static bool Verified(MirrorPulseProtectedNamespacePermissionResult result) =>
        result.Permission?.Outcome is MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified &&
        result.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true };
}
