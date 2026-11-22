using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A drained native attempt and independently retained facts for one staged action.</summary>
/// <remarks>Verification is historical evidence, not current membership, tree readiness or source acceptance.</remarks>
public sealed record MirrorPulseNamespaceBirthRecoveryResult(MirrorPulseNamespaceBirthRecoveryAction Action,
    CloudProtectedLocalOperationResult Receipt, MirrorPulseNamespaceBirthRecoveryVerification? Verification);

internal enum MirrorPulseNamespaceBirthRecoveryCheckpoint { AfterRoleHandoff, BeforeAccessDescriptor, AfterNativeOperation }

/// <summary>Recovers the original staged local file through public CfSharp same-object conversion.</summary>
/// <remarks>
/// The Host must retain its shared namespace actor and root/ancestor lifetime guards and run
/// inside the current namespace role. This coordinator retains the direct parent while doing
/// local work; it does not serialize other Host operations or enable strict tree protection.
/// Persisted actions precede native writes. Missing first bindings, changed objects, foreign
/// descriptors and stale parent epochs require explicit recovery. No source or Worker work,
/// creation, first-birth publication, reservation release or journal acknowledgement occurs.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceBirthRecoveryCoordinator
{
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseNamespaceExecutionSession _session;
    private readonly Func<MirrorPulseNamespaceBirthRecoveryCheckpoint, CancellationToken, ValueTask>? _checkpoint;

    public MirrorPulseNamespaceBirthRecoveryCoordinator(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession session) : this(catalog, router, session, null) { }

    internal MirrorPulseNamespaceBirthRecoveryCoordinator(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession session,
        Func<MirrorPulseNamespaceBirthRecoveryCheckpoint, CancellationToken, ValueTask>? checkpoint)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _checkpoint = checkpoint;
    }

    /// <summary>Inspects the original object and retains a before-write intent using current derived policy.</summary>
    public async Task<MirrorPulseNamespaceBirthRecoveryAction> PrepareStagedFileAsync(Guid recoveryId, Guid birthOperationId,
        CloudFile file, CloudDirectory parent, CancellationToken cancellationToken = default)
    {
        if (recoveryId == Guid.Empty) throw new ArgumentException("A recovery ID is required.", nameof(recoveryId));
        var context = await ReadContextAsync(birthOperationId, file, parent, cancellationToken).ConfigureAwait(false);
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, _router, cancellationToken).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(
            context.Birth, parent, context.Parent, parentGuard, cancellationToken).ConfigureAwait(false);
        var (facts, _) = await InspectFileAsync(context, file, cancellationToken).ConfigureAwait(false);
        string roleOnly = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(_session);
        string converted = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, false);
        var retained = await _catalog.ReadNamespaceBirthRecoveryActionAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            RequireCurrentAction(context, retained, roleOnly, converted);
            RequireOwnedDescriptor(retained, facts.Dacl);
            _ = await _catalog.ValidateNamespaceBirthRecoveryExecutionAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var previous = await _catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birthOperationId, cancellationToken).ConfigureAwait(false);
            retained = new(1, recoveryId, birthOperationId, previous?.RecoveryId, facts.LocalObject, facts.OwnerSid,
                _session.RoleSid.Value, context.Parent.PermissionOperationId, facts.Dacl, roleOnly, converted, DateTimeOffset.UtcNow)
            { BornParent = context.Parent.BornParent };
            retained = await _catalog.PrepareNamespaceBirthRecoveryActionAsync(retained, cancellationToken).ConfigureAwait(false);
        }
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before,
            await parentGuard.InspectAsync(cancellationToken).ConfigureAwait(false));
        return retained;
    }

    /// <summary>Currently admits the retained intent, performs native recovery and independently verifies it.</summary>
    public async Task<MirrorPulseNamespaceBirthRecoveryResult> ExecuteStagedFileAsync(Guid recoveryId,
        CloudFile file, CloudDirectory parent, CancellationToken cancellationToken = default)
    {
        var action = await _catalog.ValidateNamespaceBirthRecoveryExecutionAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        var context = await ReadContextAsync(action.BirthOperationId, file, parent, cancellationToken).ConfigureAwait(false);
        if (context.Plan.Content is not null &&
            await _catalog.ReadNamespaceBirthContentCompletionAsync(action.BirthOperationId, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("Imported content must be completely copied on the original object before conversion or permission handoff.");
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, _router, cancellationToken).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(
            context.Birth, parent, context.Parent, parentGuard, cancellationToken).ConfigureAwait(false);
        RequireCurrentAction(context, action, MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(_session),
            MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, false));
        var (facts, snapshot) = await InspectFileAsync(context, file, cancellationToken).ConfigureAwait(false);
        RequireOwnedDescriptor(action, facts.Dacl);
        if (!snapshot.IsPlaceholder)
        {
            // Metadata-only owner rights rotate the role without granting normal content access.
            await MirrorPulseWindowsNamespacePermissionLease.ApplyStagedBirthRoleHandoffAsync(file, _router, action, _session, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(MirrorPulseNamespaceBirthRecoveryCheckpoint.AfterRoleHandoff, cancellationToken).ConfigureAwait(false);
        }
        _ = await _catalog.ValidateNamespaceBirthRecoveryExecutionAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        var identity = new CloudPlaceholderIdentity(context.Plan.ItemId, context.Plan.RemoteId, context.Plan.RemoteRevision);
        var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, _router, action.LocalObject, identity,
            async (lease, stop) =>
            {
                var current = await lease.InspectAsync(stop).ConfigureAwait(false);
                RequireOriginalObject(context, current);
                RequireOwnedDescriptor(action, current.Dacl);
                await CheckpointAsync(MirrorPulseNamespaceBirthRecoveryCheckpoint.BeforeAccessDescriptor, stop).ConfigureAwait(false);
                if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.ConvertedDacl, current.Dacl))
                    await lease.ApplyDaclAsync(action.ConvertedDacl, stop).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != CloudProtectedLocalOperationOutcome.Completed || !receipt.CallbackStarted || !receipt.CallbackCompleted ||
            !receipt.Drained || !receipt.NativeIdentityPrepared || !receipt.DurableProjectionCommitted)
            return new(action, receipt, null);
        await CheckpointAsync(MirrorPulseNamespaceBirthRecoveryCheckpoint.AfterNativeOperation, cancellationToken).ConfigureAwait(false);
        var (verified, finalSnapshot) = await InspectFileAsync(context, file, cancellationToken).ConfigureAwait(false);
        if (!finalSnapshot.IsPlaceholder || finalSnapshot.ItemId != context.Plan.ItemId || finalSnapshot.RemoteId != context.Plan.RemoteId ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.ConvertedDacl, verified.Dacl))
            throw new InvalidDataException("The independent recovery inspection requires the original public identity and current converted policy.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before,
            await parentGuard.InspectAsync(cancellationToken).ConfigureAwait(false));
        _ = await _catalog.ValidateNamespaceBirthRecoveryExecutionAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        var verification = await _catalog.ReadNamespaceBirthRecoveryVerificationAsync(recoveryId, cancellationToken).ConfigureAwait(false);
        verification ??= await _catalog.RecordNamespaceBirthRecoveryVerificationAsync(new(1, recoveryId, action.BirthOperationId,
            new(verified.LocalObject, verified.OwnerSid, verified.Dacl, verified.ObservedAt),
            context.Plan.ItemId, context.Plan.RemoteId, context.Plan.RemoteRevision, true, false, verified.LinkCount!.Value), cancellationToken).ConfigureAwait(false);
        return new(action, receipt, verification);
    }

    private sealed record Context(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan,
        MirrorPulseNamespaceBirthConversionPreparation Conversion, MirrorPulseNamespaceProtectionProof Parent);

    private async Task<Context> ReadContextAsync(Guid operationId, CloudFile file, CloudDirectory parent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(parent);
        var birth = await _catalog.ReadNamespaceBirthAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var plan = await _catalog.ReadNamespaceBirthPlanAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        var conversion = await _catalog.ReadNamespaceBirthConversionAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Recovery cannot reconstruct a missing first native binding from the current path.");
        if (birth.IsDirectory || birth.Origin == MirrorPulseNamespaceBirthOrigin.RemotePopulation ||
            file.RelativePath.Replace('\\', '/') != birth.RelativePath ||
            !string.Equals(file.Parent?.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase) ||
            await _catalog.ReadNamespaceBirthObservationAsync(operationId, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Staged recovery requires the original unobserved local file and its direct parent.");
        var retainedParent = await MirrorPulseNamespaceBirthProtectionVerifier.ReadRetainedParentAsync(_catalog, birth, token).ConfigureAwait(false);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != conversion.OwnerSid || _session.OwnerSid.Value != conversion.OwnerSid ||
            retainedParent.OwnerSid != conversion.OwnerSid || retainedParent.RoleSid != _session.RoleSid.Value ||
            !new WindowsPrincipal(caller).IsInRole(_session.RoleSid))
            throw new UnauthorizedAccessException("Recovery requires the original owner and the current protected parent's namespace role.");
        return new(birth, plan, conversion, retainedParent);
    }

    private async Task<(MirrorPulseNamespacePermissionObject Facts, CloudItemSnapshot Snapshot)> InspectFileAsync(Context context,
        CloudFile file, CancellationToken token)
    {
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, _router, token).ConfigureAwait(false);
        var facts = await lease.InspectAsync(token).ConfigureAwait(false);
        RequireOriginalObject(context, facts);
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false);
        var expected = new CloudPlaceholderIdentity(context.Plan.ItemId, context.Plan.RemoteId, context.Plan.RemoteRevision);
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.File || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            snapshot.SynchronizationState == CloudSynchronizationState.InSync ||
            snapshot.IsPlaceholder && !snapshot.PlaceholderIdentity.Span.SequenceEqual(expected.Encode()))
            throw new InvalidDataException("Recovery requires the original unaccepted native file and its planned placeholder identity.");
        return (facts, snapshot);
    }

    private static void RequireOriginalObject(Context context, MirrorPulseNamespacePermissionObject facts)
    {
        if (facts.LocalObject != context.Conversion.LocalObject || facts.OwnerSid != context.Conversion.OwnerSid ||
            facts.RootId != context.Birth.RootId || facts.RelativePath != context.Birth.RelativePath || facts.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("Recovery cannot adopt a replacement object, owner, path or alias.");
    }

    private void RequireCurrentAction(Context context, MirrorPulseNamespaceBirthRecoveryAction action, string roleOnly, string converted)
    {
        if (action.BirthOperationId != context.Birth.OperationId || action.LocalObject != context.Conversion.LocalObject ||
            action.OwnerSid != context.Conversion.OwnerSid || action.RoleSid != _session.RoleSid.Value ||
            action.ParentPermissionOperationId != context.Parent.PermissionOperationId || action.BornParent != context.Parent.BornParent ||
            action.RoleOnlyDacl != roleOnly || action.ConvertedDacl != converted)
            throw new InvalidDataException("The original action does not retain the current derived role and direct-parent policy.");
    }

    private static void RequireOwnedDescriptor(MirrorPulseNamespaceBirthRecoveryAction action, string actual)
    {
        if (!new[] { action.ExpectedDacl, action.RoleOnlyDacl, action.ConvertedDacl }
            .Any(owned => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(owned, actual)))
            throw new InvalidDataException("Recovery cannot overwrite a descriptor outside its retained owned states.");
    }

    private ValueTask CheckpointAsync(MirrorPulseNamespaceBirthRecoveryCheckpoint checkpoint, CancellationToken token) =>
        _checkpoint is null ? ValueTask.CompletedTask : _checkpoint(checkpoint, token);
}
