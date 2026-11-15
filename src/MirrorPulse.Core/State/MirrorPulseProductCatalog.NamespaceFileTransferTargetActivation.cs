using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Protection used at the first native start of an already selected file target.</summary>
/// <remarks>The original selection, logical item identity and native start retain their own records.</remarks>
public sealed record MirrorPulseFileTransferTargetActivation(int Version, Guid BirthOperationId, Guid SourceAuditId, Guid TargetAuditId,
    MirrorPulseNamespaceMemberProtectionSnapshot TargetParent, DateTimeOffset StartedAt)
{
    internal MirrorPulseFileTransferRecovery Recovery => new(SourceAuditId, TargetAuditId, TargetParent.Context.Protection.RoleSid);
}

public sealed partial class MirrorPulseProductCatalog
{
    private async Task RecordFileTransferTargetActivationAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthStart start,
        MirrorPulseFileTransferRecovery recovery, SqliteTransaction transaction, CancellationToken token)
    {
        if (birth.TransferEntry is not { Version: 1 } owner)
            throw new InvalidOperationException("Only an already selected file transfer can activate under a fresh role.");
        var copy = await ReadNamespaceTransferCoreAsync(owner.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Target activation requires its original transfer.");
        if (copy.TargetBirth?.Birth != birth || birth.RoleSid == recovery.RoleSid)
            throw new InvalidOperationException("Target activation requires the original selected birth under a different role.");
        var members = await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true, targetCopy: copy,
            recovery: recovery, activatingTarget: true).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Target activation requires current proof for both original roots.");
        var activation = new MirrorPulseFileTransferTargetActivation(1, birth.OperationId, recovery.SourceAuditId,
            recovery.TargetAuditId, members.TargetParent, start.StartedAt);
        await ValidateFileTransferTargetActivationAuditsAsync(copy, activation, transaction, token).ConfigureAwait(false);
        var claim = new NamespaceBirthParentClaim(members.TargetParent.PermissionOperationId, members.TargetParent.BornParent,
            recovery.RoleSid, start.StartedAt);
        _ = await ValidateNamespaceBirthLineageAsync(birth, null, true, claim, transaction, token).ConfigureAwait(false);
        await WriteNamespaceTransferCoreAsync(copy with { TargetActivation = activation }, transaction, token).ConfigureAwait(false);
    }

    private async Task RequireUnstartedFileTransferTargetAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.TargetBirth is not { } target || copy.TargetPublication is not null || copy.TargetActivation is not null ||
            await ReadNamespaceBirthStartRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) is not null ||
            await ReadNamespaceBirthObservationRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) is not null ||
            await ReadNamespaceMemberForBirthCoreAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A fresh target role requires its selected birth before the first native start.");
    }

    private async Task ValidateFileTransferTargetActivationAuditsAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseFileTransferTargetActivation activation, SqliteTransaction transaction, CancellationToken token)
    {
        var target = copy.TargetBirth ?? throw new InvalidDataException("Target activation lost its original selection.");
        var parent = activation.TargetParent;
        var original = copy.Plan.TargetParent;
        if (activation.BirthOperationId != target.Birth.OperationId || activation.StartedAt < target.Birth.PreparedAt ||
            parent.Context.Member != original.Context.Member || parent.Context.Location != original.Context.Location ||
            parent.Identity != original.Identity || activation.Recovery.RoleSid == target.Birth.RoleSid)
            throw new InvalidDataException("Target activation must retain the original selected birth and direct parent.");
        var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(copy.Plan, activation.Recovery, target.Birth.PreparedAt,
            transaction, token).ConfigureAwait(false);
        if (parent.Context.Protection.VerifiedAt > targetAudit.CompletedAt ||
            sourceAudit.CompletedAt > activation.StartedAt || targetAudit.CompletedAt > activation.StartedAt)
            throw new InvalidDataException("Target activation requires both completed original root audits before the first native start.");
        await RequireRemoteUpdateHistoricalSnapshotAsync(parent, activation.StartedAt, transaction, token).ConfigureAwait(false);
    }

    private async Task ValidateFileTransferTargetActivationAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.TargetActivation is not { } activation) return;
        var start = await ReadNamespaceBirthStartRowAsync(activation.BirthOperationId, transaction, token).ConfigureAwait(false);
        if (start is null || start.StartedAt != activation.StartedAt)
            throw new InvalidDataException("Target activation lost its exact first native start intent.");
        await ValidateFileTransferTargetActivationAuditsAsync(copy, activation, transaction, token).ConfigureAwait(false);
    }
}
