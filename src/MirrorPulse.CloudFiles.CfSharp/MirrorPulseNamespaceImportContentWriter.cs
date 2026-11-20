using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceImportContentCheckpoint { AfterRoleHandoff, AfterContentFlush, AfterContentRecorded }

/// <summary>Copies a verified private input onto its retained, role-only ordinary birth object.</summary>
/// <remarks>
/// The caller owns namespace execution, its actor and root lifetime guards. Open the private
/// input as the normal user before acquiring the actor. This boundary never creates by path,
/// converts, publishes membership, marks in sync or acknowledges remote work. Existing copy
/// receipts return history only and never authorize overwriting newer local bytes.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceImportContentWriter(MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseNamespaceImportContentCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async Task<MirrorPulseNamespaceBirthContentCompletion> CopyAsync(Guid operationId, CloudFile file,
        CloudDirectory parent, MirrorPulseLocalImportInputLease? input, CancellationToken cancellationToken = default)
    {
        actor.AssertAccess();
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(parent);
        var birth = await catalog.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original import admission is missing.");
        var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original import content plan is missing.");
        var conversion = await catalog.ReadNamespaceBirthConversionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Import copying requires its first ordinary object binding.");
        if (birth.Origin != MirrorPulseNamespaceBirthOrigin.Import || birth.IsDirectory || plan.Content is null ||
            file.RelativePath.Replace('\\', '/') != birth.RelativePath || file.Parent is null ||
            !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Import copying requires the admitted file and its exact direct parent.");
        var completed = await catalog.ReadNamespaceBirthContentCompletionAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (completed is not null) return completed;
        if (input is null || input.OperationId != operationId || input.Content != plan.Content)
            throw new InvalidOperationException("The private input lease does not belong to this original import.");

        var recovery = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, execution);
        var action = await recovery.PrepareStagedFileAsync(Guid.NewGuid(), operationId, file, parent, cancellationToken).ConfigureAwait(false);
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, cancellationToken).ConfigureAwait(false);
        var parentProof = await MirrorPulseNamespaceBirthProtectionVerifier.ReadRetainedParentAsync(catalog, birth, cancellationToken).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent, parentProof,
            parentGuard, cancellationToken).ConfigureAwait(false);
        _ = await catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId, cancellationToken).ConfigureAwait(false);
        // Reuse the original-binding owner-metadata handoff. Normal users still cannot read
        // or alias the ordinary object while its content is incomplete.
        await MirrorPulseWindowsNamespacePermissionLease.ApplyStagedBirthRoleHandoffAsync(file, router, action, execution,
            cancellationToken).ConfigureAwait(false);
        CancellationToken durable = CancellationToken.None;
        await CheckpointAsync(MirrorPulseNamespaceImportContentCheckpoint.AfterRoleHandoff, durable).ConfigureAwait(false);
        await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, durable).ConfigureAwait(false);
        RequireOrdinary(await metadata.InspectAsync(durable).ConfigureAwait(false), await file.InspectAsync(durable).ConfigureAwait(false));
        await using var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RequireOrdinary(await metadata.InspectAsync(durable).ConfigureAwait(false), await file.InspectAsync(durable).ConfigureAwait(false));
        // From the first data write, finish local persistence independently of a client disconnect.
        writer.SetLength(0);
        input.Data.Position = 0;
        await input.Data.CopyToAsync(writer, 64 * 1024, durable).ConfigureAwait(false);
        await writer.FlushAsync(durable).ConfigureAwait(false);
        writer.Flush(flushToDisk: true);
        writer.Position = 0;
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(writer, durable).ConfigureAwait(false));
        if (writer.Length != plan.Content.Length || digest != plan.Content.Sha256)
            throw new InvalidDataException("The original import object does not contain the complete staged bytes after flush.");
        await CheckpointAsync(MirrorPulseNamespaceImportContentCheckpoint.AfterContentFlush, durable).ConfigureAwait(false);
        var final = await metadata.InspectAsync(durable).ConfigureAwait(false);
        RequireOrdinary(final, await file.InspectAsync(durable).ConfigureAwait(false));
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentGuard.InspectAsync(durable).ConfigureAwait(false));
        _ = await catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId, durable).ConfigureAwait(false);
        actor.AssertAccess();
        var completion = await catalog.RecordNamespaceBirthContentCompletionAsync(new(1, operationId, action.RecoveryId, plan.Content,
            new(final.LocalObject, final.OwnerSid, final.Dacl, final.ObservedAt), false, final.LinkCount!.Value), durable).ConfigureAwait(false);
        await CheckpointAsync(MirrorPulseNamespaceImportContentCheckpoint.AfterContentRecorded, durable).ConfigureAwait(false);
        return completion;

        void RequireOrdinary(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot)
        {
            if (!snapshot.Exists || snapshot.IsTombstone || snapshot.IsPlaceholder || snapshot.Kind != CloudItemKind.File ||
                snapshot.LocalBinding is not { } binding || facts.LocalObject != conversion.LocalObject ||
                facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
                facts.OwnerSid != action.OwnerSid || facts.OwnerSid != conversion.OwnerSid || facts.RootId != birth.RootId ||
                facts.RelativePath != birth.RelativePath || facts.IsDirectory || facts.LinkCount != 1 ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, facts.Dacl))
                throw new InvalidDataException("Import copying lost the original single-link object or its role-only protected state.");
        }
    }

    private ValueTask CheckpointAsync(MirrorPulseNamespaceImportContentCheckpoint checkpoint, CancellationToken token) =>
        Checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;
}
