using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceReplacementRoleCheckpoint { AfterActionPrepared, AfterMetadataApplied, AfterHandoffRecorded }

/// <summary>Hands an admitted original replacement file to the current Host namespace role.</summary>
/// <remarks>
/// The Host excludes the previous live owner and retains its scheduler and shared actor.
/// This component reads metadata only, preserves first content facts and never marks a
/// tree ready, opens source content, restores user access or releases a root reservation.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceReplacementRoleRecovery(MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseNamespaceReplacementRoleCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async Task<MirrorPulseNamespaceReplacementRoleHandoff> RecoverAsync(Guid operationId, CloudFile file,
        CloudDirectory parent, CancellationToken cancellationToken = default)
    {
        actor.AssertAccess(); ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(parent);
        var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement admission is missing.");
        var first = facts.Action ?? throw new InvalidOperationException("Role recovery requires its immutable first permission action.");
        if (facts.Cancellation is not null || first.OwnerSid != execution.OwnerSid.Value ||
            file.RelativePath.Replace('\\', '/') != facts.Intent.Original.Context.Location.RelativePath || file.Parent is null ||
            !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase) ||
            !CloudPlaceholderIdentity.TryDecode(Convert.FromBase64String(facts.Intent.OriginalPlaceholderIdentity), out var identity) ||
            identity is null || identity.ItemId != facts.Intent.Original.Identity.ItemId || identity.RemoteId != facts.Intent.Original.Identity.RemoteId)
            throw new InvalidOperationException("Role recovery requires the original owner, path and public identity.");
        var role = facts.RoleActions.Count == 0 ? null : facts.RoleActions[^1];
        if (role?.Action.RoleSid == execution.RoleSid.Value && facts.RoleHandoff is { } historical) return historical;
        var parentProof = await MirrorPulseNamespaceReplacementContentWriter.ReadCurrentParentAsync(catalog, facts.Intent, cancellationToken).ConfigureAwait(false);
        if (parent.RelativePath.Replace('\\', '/') != parentProof.Context.Location.RelativePath)
            throw new InvalidDataException("Role recovery lost its current committed parent.");
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, cancellationToken).ConfigureAwait(false);
        var parentBefore = await parentGuard.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!parentBefore.IsDirectory || parentBefore.LocalObject != parentProof.Context.Member.LocalObject ||
            parentBefore.OwnerSid != parentProof.Context.Verification.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentProof.ExpectedDacl, parentBefore.Dacl))
            throw new InvalidDataException("Role recovery requires the actually closed current parent, without inventing its rotation.");
        if (role?.Action.RoleSid != execution.RoleSid.Value)
        {
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, cancellationToken).ConfigureAwait(false);
            var observed = await metadata.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (observed.LocalObject != first.LocalObject || observed.OwnerSid != first.OwnerSid || observed.IsDirectory || observed.LinkCount != 1)
                throw new InvalidDataException("Role recovery cannot adopt another object or owner at the original path.");
            MirrorPulseNamespaceReplacementAction? known = null;
            foreach (var candidate in facts.RoleActions.Select(value => value.Action).Prepend(first))
                if (MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(candidate.RoleOnlyDacl, observed.Dacl) ||
                    MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(candidate.RestoreDacl, observed.Dacl)) known = candidate;
            if (known is null) throw new InvalidDataException("Role recovery requires permissions owned by its complete retained action chain.");
            Guid id = Guid.NewGuid();
            var currentAction = new MirrorPulseNamespaceReplacementAction(1, operationId, id, first.LocalObject, first.OwnerSid,
                execution.RoleSid.Value, MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution),
                MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, isDirectory: false), DateTimeOffset.UtcNow);
            role = await catalog.PrepareNamespaceReplacementRoleActionAsync(new(1, operationId, id, role?.RecoveryId,
                first.ActionId, known.ActionId, currentAction, parentProof, observed.Dacl), cancellationToken).ConfigureAwait(false);
        }
        if (role is null || role.Action.OwnerSid != execution.OwnerSid.Value || role.Action.RoleSid != execution.RoleSid.Value ||
            role.Action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution) ||
            role.Action.RestoreDacl != MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, isDirectory: false))
            throw new InvalidDataException("The recorded current action differs from the freshly derived namespace policy.");
        await AtAsync(MirrorPulseNamespaceReplacementRoleCheckpoint.AfterActionPrepared, cancellationToken).ConfigureAwait(false);
        var after = await MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(file, router,
            first.LocalObject, first.OwnerSid, [role.ExpectedOwnedDacl], execution, requireOrdinary: false, identity, cancellationToken).ConfigureAwait(false);
        await AtAsync(MirrorPulseNamespaceReplacementRoleCheckpoint.AfterMetadataApplied, CancellationToken.None).ConfigureAwait(false);
        var native = await file.InspectAsync(CancellationToken.None).ConfigureAwait(false);
        if (!native.Exists || native.IsTombstone || native.Kind != CloudItemKind.File || native.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != first.LocalObject ||
            native.IsPlaceholder && !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
            throw new InvalidDataException("The independent handoff observation lost its original object or public identity.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore,
            await parentGuard.InspectAsync(CancellationToken.None).ConfigureAwait(false));
        var handoff = await catalog.RecordNamespaceReplacementRoleHandoffAsync(new(1, operationId, role.RecoveryId,
            new(after.LocalObject, after.OwnerSid, after.Dacl, after.ObservedAt), after.LinkCount!.Value,
            native.IsPlaceholder, native.IsPlaceholder ? Convert.ToBase64String(native.PlaceholderIdentity.Span) : null), CancellationToken.None).ConfigureAwait(false);
        await AtAsync(MirrorPulseNamespaceReplacementRoleCheckpoint.AfterHandoffRecorded, CancellationToken.None).ConfigureAwait(false);
        return handoff;
    }

    private ValueTask AtAsync(MirrorPulseNamespaceReplacementRoleCheckpoint point, CancellationToken token) =>
        Checkpoint?.Invoke(point, token) ?? ValueTask.CompletedTask;
}
