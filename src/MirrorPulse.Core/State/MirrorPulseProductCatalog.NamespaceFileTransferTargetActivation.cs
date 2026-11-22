using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Protection used at the first native start of an already selected file target.</summary>
/// <remarks>The original selection, logical item identity and native start retain their own records.</remarks>
public sealed record MirrorPulseFileTransferTargetActivation(int Version, Guid BirthOperationId, Guid SourceAuditId, Guid TargetAuditId,
    MirrorPulseNamespaceMemberProtectionSnapshot TargetParent, DateTimeOffset StartedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AdmittedAt { get; init; }

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

    private async Task<MirrorPulseNamespaceBirthStart> RequireFileTransferTargetAwaitingCreationAsync(
        MirrorPulseNamespaceTransferCopy copy, Guid birthId, SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.TargetBirth is not { } target || target.Birth.OperationId != birthId || copy.TargetPublication is not null ||
            await ReadNamespaceBirthStartRowAsync(birthId, transaction, token).ConfigureAwait(false) is not { Version: 2 } start ||
            await ReadNamespaceBirthObservationRowAsync(birthId, transaction, token).ConfigureAwait(false) is not null ||
            await ReadNamespaceMemberForBirthCoreAsync(birthId, transaction, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Started-target recovery requires its original recoverable start with no observation or registered member.");
        return start;
    }

    internal async Task RecordFileTransferStartedTargetRecoveryAsync(Guid operationId, Guid birthId,
        MirrorPulseFileTransferRecovery recovery, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("Target recovery requires its original transfer.");
            var start = await RequireFileTransferTargetAwaitingCreationAsync(copy, birthId, transaction, token).ConfigureAwait(false);
            var members = await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true, targetCopy: copy,
                recovery: recovery, recoveringStartedTarget: start).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Target recovery requires both original root audits.");
            if (copy.TargetStartRecovery?.Recovery != recovery)
            {
                if (copy.TargetRoleSid == recovery.RoleSid)
                    throw new InvalidOperationException("Started-target recovery requires a new audited role.");
                var activation = new MirrorPulseFileTransferTargetActivation(2, birthId, recovery.SourceAuditId, recovery.TargetAuditId,
                    members.TargetParent, start.StartedAt)
                { AdmittedAt = DateTimeOffset.UtcNow };
                await ValidateFileTransferTargetActivationAuditsAsync(copy, activation, transaction, token).ConfigureAwait(false);
                var claim = new NamespaceBirthParentClaim(members.TargetParent.PermissionOperationId, members.TargetParent.BornParent,
                    recovery.RoleSid, activation.AdmittedAt.Value);
                _ = await ValidateNamespaceBirthLineageAsync(copy.TargetBirth!.Birth, null, true, claim, transaction, token).ConfigureAwait(false);
                await RequireNamespaceBirthReservationAsync(copy.TargetBirth.Birth, transaction, token).ConfigureAwait(false);
                await WriteNamespaceTransferCoreAsync(copy with { TargetStartRecovery = activation }, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private static void ValidateFileTransferTargetActivationShape(Guid? birthId, MirrorPulseFileTransferTargetActivation? activation, int version)
    {
        if (activation is null) return;
        if (activation.Version != version || activation.BirthOperationId != birthId || activation.BirthOperationId == Guid.Empty ||
            activation.SourceAuditId == Guid.Empty || activation.TargetAuditId == Guid.Empty || activation.SourceAuditId == activation.TargetAuditId ||
            activation.TargetParent?.Context?.Protection is null || activation.StartedAt == default ||
            (version == 2) != (activation.AdmittedAt is not null) || activation.AdmittedAt < activation.StartedAt)
            throw new ArgumentException("A transfer target activation requires its original birth, both root audits and valid admission times.");
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
        DateTimeOffset admittedAt = activation.AdmittedAt ?? activation.StartedAt;
        DateTimeOffset earliest = activation.Version == 2 ? activation.StartedAt : target.Birth.PreparedAt;
        var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(copy.Plan, activation.Recovery, earliest,
            transaction, token, activation.Version == 2 ? activation.BirthOperationId : null).ConfigureAwait(false);
        if (parent.Context.Protection.VerifiedAt > targetAudit.CompletedAt ||
            sourceAudit.CompletedAt > admittedAt || targetAudit.CompletedAt > admittedAt)
            throw new InvalidDataException("Target activation requires both completed original root audits before admission.");
        await RequireRemoteUpdateHistoricalSnapshotAsync(parent, admittedAt, transaction, token).ConfigureAwait(false);
    }

    private async Task ValidateFileTransferTargetActivationAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        foreach (var activation in new[] { copy.TargetActivation, copy.TargetStartRecovery })
        {
            if (activation is null) continue;
            var start = await ReadNamespaceBirthStartRowAsync(activation.BirthOperationId, transaction, token).ConfigureAwait(false);
            if (start is null || start.StartedAt != activation.StartedAt || activation.Version == 2 && start.Version != 2)
                throw new InvalidDataException("Target activation lost its exact first native start intent.");
            await ValidateFileTransferTargetActivationAuditsAsync(copy, activation, transaction, token).ConfigureAwait(false);
        }
    }
}
