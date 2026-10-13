using System.ComponentModel;
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceReplacementRestorationCheckpoint { BeforeScope, AfterScope, AfterResult }

/// <summary>Restores access on the admitted original file using CfSharp's public same-object conversion scope.</summary>
/// <remarks>
/// The caller retains the instance scheduler, namespace actor and current role. This component
/// retains the current parent and persists a before-scope intent. It never recopies historical
/// input, hydrates, reads a source, completes a tree, releases root reservations or acknowledges
/// uploads. A completed native scope requires separate object and parent protection observations.
/// New-role recovery requires its own persisted handoff before entering this component.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceReplacementAccessRestorer(MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseNamespaceReplacementRestorationCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async Task<MirrorPulseNamespaceReplacementRestorationResult> RestoreAsync(Guid operationId, CloudFile file,
        CloudDirectory parent, CancellationToken cancellationToken = default)
    {
        actor.AssertAccess(); ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(parent);
        var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement admission is missing.");
        if (facts.RestorationResult is { ProductVerified: true } historical) return historical;
        var action = facts.Action ?? throw new InvalidOperationException("Replacement access restoration requires the first permission action.");
        if (facts.Cancellation is not null || facts.ContentCompletion is null || execution.RoleSid.Value != action.RoleSid || execution.OwnerSid.Value != action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution), action.RoleOnlyDacl) ||
            file.RelativePath.Replace('\\', '/') != facts.Intent.Original.Context.Location.RelativePath ||
            file.Parent is null || !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase) ||
            action.RestoreDacl != facts.Intent.Original.ExpectedDacl ||
            !CloudPlaceholderIdentity.TryDecode(Convert.FromBase64String(facts.Intent.OriginalPlaceholderIdentity), out var identity) ||
            identity is null || identity.ItemId != facts.Intent.Original.Identity.ItemId || identity.RemoteId != facts.Intent.Original.Identity.RemoteId)
            throw new InvalidOperationException("Restoration requires copied content, the original identity and a proven current-role action.");
        var parentProof = await MirrorPulseNamespaceReplacementContentWriter.ReadCurrentParentAsync(catalog, facts.Intent, cancellationToken).ConfigureAwait(false);
        if (parentProof.Context.Protection.RoleSid != action.RoleSid || parent.RelativePath.Replace('\\', '/') != parentProof.Context.Location.RelativePath)
            throw new InvalidDataException("Restoration lost the current protected parent or namespace role.");
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, cancellationToken).ConfigureAwait(false);
        RequireParent(await parentGuard.InspectAsync(cancellationToken).ConfigureAwait(false));
        var before = await InspectAsync(cancellationToken).ConfigureAwait(false); RequireOriginal(before.Object, before.Snapshot, restored: false);
        var attempt = facts.Restoration;
        if (attempt is null || facts.RestorationResult is not null)
        {
            attempt = await catalog.PrepareNamespaceReplacementRestorationAsync(new(1, operationId, Guid.NewGuid(),
                facts.Restoration?.RestorationId, action.ActionId, parentProof, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        await AtAsync(MirrorPulseNamespaceReplacementRestorationCheckpoint.BeforeScope, cancellationToken).ConfigureAwait(false);
        var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, action.LocalObject, identity,
            async (lease, stop) =>
            {
                var current = await lease.InspectAsync(stop).ConfigureAwait(false);
                RequireOriginalPermission(current, restored: false);
                // Reapply the same proven closed descriptor on replay so the public scope
                // produces its own access-operation and readback proof after an interrupted owner.
                await lease.ApplyDaclAsync(action.RestoreDacl, stop).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        // A drained native result must survive client cancellation. The catalog transaction is
        // short and local; no caller token can erase successful native conversion or pending projection.
        await AtAsync(MirrorPulseNamespaceReplacementRestorationCheckpoint.AfterScope, CancellationToken.None).ConfigureAwait(false);
        MirrorPulseNamespacePermissionVerification? objectVerification = null, parentVerification = null;
        uint? links = null; bool? placeholder = null, inSync = null; string? encodedIdentity = null; bool productVerified = false;
        if (receipt.Outcome == CloudProtectedLocalOperationOutcome.Completed && receipt.Drained)
        {
            try
            {
                var final = await InspectAsync(CancellationToken.None).ConfigureAwait(false);
                objectVerification = Verification(final.Object); links = final.Object.LinkCount;
                placeholder = final.Snapshot.IsPlaceholder; inSync = final.Snapshot.SynchronizationState == CloudSynchronizationState.InSync;
                encodedIdentity = placeholder.Value ? Convert.ToBase64String(final.Snapshot.PlaceholderIdentity.Span) : null;
                var parentNow = await parentGuard.InspectAsync(CancellationToken.None).ConfigureAwait(false);
                parentVerification = Verification(parentNow); RequireParent(parentNow);
                RequireOriginal(final.Object, final.Snapshot, restored: true);
                productVerified = receipt.CallbackStarted && receipt.CallbackCompleted && receipt.NativeIdentityPrepared &&
                    receipt.DurableProjectionCommitted && receipt.AccessDescriptorApplied && receipt.AccessDescriptorReadBack;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
            {
                // Preserve the actual native result and any independent observations. A failed
                // product check leaves the root fenced and requires another recovery attempt.
                productVerified = false;
            }
        }
        var binding = receipt.ExpectedBinding;
        var result = new MirrorPulseNamespaceReplacementRestorationResult(1, operationId, attempt.RestorationId,
            receipt.Outcome.ToString(), receipt.Stage.ToString(), new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId),
            receipt.CallbackStarted, receipt.CallbackCompleted, receipt.NativeIdentityPrepared, receipt.NativeConverted,
            receipt.DurableProjectionCommitted, receipt.AccessDescriptorApplied, receipt.AccessDescriptorReadBack, receipt.Drained,
            receipt.PreparationHResult, receipt.PreparationUsn, receipt.Error?.HResult, objectVerification, parentVerification,
            links, placeholder, inSync, encodedIdentity, productVerified, DateTimeOffset.UtcNow);
        result = await catalog.RecordNamespaceReplacementRestorationResultAsync(result, CancellationToken.None).ConfigureAwait(false);
        await AtAsync(MirrorPulseNamespaceReplacementRestorationCheckpoint.AfterResult, CancellationToken.None).ConfigureAwait(false);
        return result;

        async Task<(MirrorPulseNamespacePermissionObject Object, CloudItemSnapshot Snapshot)> InspectAsync(CancellationToken stop)
        {
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, stop).ConfigureAwait(false);
            return (await lease.InspectAsync(stop).ConfigureAwait(false), await file.InspectAsync(stop).ConfigureAwait(false));
        }
        void RequireParent(MirrorPulseNamespacePermissionObject current)
        {
            if (!current.IsDirectory || current.RootId != facts.Intent.RootId || current.LocalObject != parentProof.Context.Member.LocalObject ||
                current.OwnerSid != parentProof.Context.Verification.OwnerSid || current.RelativePath != parentProof.Context.Location.RelativePath ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentProof.ExpectedDacl, current.Dacl))
                throw new InvalidDataException("Restoration requires the unchanged current parent binding, owner and closed permissions.");
        }
        void RequireOriginalPermission(MirrorPulseNamespacePermissionObject current, bool restored)
        {
            if (current.IsDirectory || current.LocalObject != action.LocalObject || current.OwnerSid != action.OwnerSid || current.LinkCount != 1 ||
                current.RootId != facts.Intent.RootId || current.RelativePath != facts.Intent.Original.Context.Location.RelativePath ||
                !(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, current.Dacl) || !restored &&
                    MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, current.Dacl)))
                throw new InvalidDataException("Restoration requires the admitted single-link original object and its known permissions.");
        }
        void RequireOriginal(MirrorPulseNamespacePermissionObject current, CloudItemSnapshot snapshot, bool restored)
        {
            RequireOriginalPermission(current, restored);
            if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.File || snapshot.LocalBinding is not { } native ||
                new MirrorPulseLocalFileBinding(native.VolumeSerialNumber, native.SyncRootFileId, native.LocalFileId) != action.LocalObject ||
                snapshot.IsPlaceholder && Convert.ToBase64String(snapshot.PlaceholderIdentity.Span) != facts.Intent.OriginalPlaceholderIdentity ||
                restored && (!snapshot.IsPlaceholder || snapshot.SynchronizationState == CloudSynchronizationState.InSync && facts.Backup!.Content != facts.Intent.Content))
                throw new InvalidDataException("Restoration requires the original public identity and unaccepted changed content.");
        }
    }

    private static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject current) =>
        new(current.LocalObject, current.OwnerSid, current.Dacl, current.ObservedAt);
    private ValueTask AtAsync(MirrorPulseNamespaceReplacementRestorationCheckpoint point, CancellationToken stop) =>
        Checkpoint?.Invoke(point, stop) ?? ValueTask.CompletedTask;
}
