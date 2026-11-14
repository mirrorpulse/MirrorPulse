using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Validated product progress; settling a source name does not complete the transfer or acknowledge its journal.</summary>
public sealed record MirrorPulseNamespaceTransferStatus(Guid OperationId, RootId SourceRootId, RootId TargetRootId,
    InstanceId SourceInstanceId, InstanceId TargetInstanceId, Guid SourceMemberId, Guid? TargetMemberId,
    string SourceRelativePath, string TargetRelativePath, string Stage, DateTimeOffset PreparedAt,
    DateTimeOffset? CopyStartedAt, DateTimeOffset? CopyAcceptedAt, DateTimeOffset? SourceDeleteStartedAt,
    DateTimeOffset? SourceDeleteAcceptedAt, DateTimeOffset? SourceNativeRetiredAt, DateTimeOffset? SourceNameSettledAt,
    bool RootsFenced, bool JournalAcknowledged, bool Completed)
{
    public bool IsDirectory { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceTransferStatus?> ReadNamespaceTransferStatusAsync(Guid operationId,
        CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original transfer ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            var result = copy is null ? await ReadDirectoryTransferStatusCoreAsync(operationId, transaction, token).ConfigureAwait(false) :
                await ReadTransferStatusCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads this root's original pending transfer from one indexed, validated catalog snapshot.</summary>
    /// <remarks>This read-only status grants no native authority, root readiness or acknowledgement.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceTransferStatus>> ReadPendingNamespaceTransferStatusesAsync(RootId rootId,
        CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root ID is required.", nameof(rootId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadPendingNamespaceTransferForRootCoreAsync(rootId, transaction, token).ConfigureAwait(false);
            await RequireDirectoryTransferFenceIndexAsync(transaction, token, rootId).ConfigureAwait(false);
            Guid? directoryId;
            await using (var sql = _connection.CreateCommand())
            {
                sql.Transaction = transaction;
                sql.CommandText = "SELECT transfer_id FROM namespace_directory_transfer_fences WHERE root_id=$root;";
                sql.Parameters.AddWithValue("$root", rootId.ToString());
                directoryId = await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is string id ? ParseNamespaceMemberId(id) : null;
            }
            if (copy is not null && directoryId is not null) throw new InvalidDataException("The root has competing transfer owners.");
            MirrorPulseNamespaceTransferStatus? status = copy is not null
                ? await ReadTransferStatusCoreAsync(copy, transaction, token).ConfigureAwait(false)
                : directoryId is { } owner ? await ReadDirectoryTransferStatusCoreAsync(owner, transaction, token).ConfigureAwait(false) : null;
            if (directoryId is not null && status is not { Completed: false })
                throw new InvalidDataException("A pending directory transfer fence lost its original status.");
            MirrorPulseNamespaceTransferStatus[] result = status is null ? [] : [status];
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceTransferStatus?> ReadDirectoryTransferStatusCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var admission = await ReadDirectoryTransferAdmissionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        if (admission is null) return null;
        var copy = await ReadDirectoryTransferCopyCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        var verified = await ReadDirectoryTransferTargetCoreAsync(copy, transaction, token).ConfigureAwait(false);
        var birth = await ReadDirectoryTransferBirthRowAsync(operationId, 0, null, transaction, token).ConfigureAwait(false);
        var deletion = await ReadDirectoryTransferSourceDeletionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        var retired = await ReadDirectoryTransferRetirementsCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        var ack = await ReadDirectoryTransferAcknowledgementCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        bool completed = ack?.AcknowledgedAt is not null;
        bool journalAcknowledged = completed && admission.Originals.Members.Count != 0;
        DateTimeOffset? retiredAt = retired.Count == admission.Members.Count ? retired[^1].RetiredAt : null;
        Guid? targetMember = null;
        if (birth is not null && verified is not null)
        {
            ValidateRetainedDirectoryTransferBirth(admission, verified, birth);
            await ValidateDirectoryTransferBirthArtifactsAsync(birth, transaction, token).ConfigureAwait(false);
            if (await ReadNamespaceMemberCoreAsync(birth.Birth.OperationId, transaction, token).ConfigureAwait(false) is { } member)
                targetMember = member.MemberId;
        }
        string stage = completed ? journalAcknowledged ? "Completed" : "CompletedCommandWithoutCapturedNotifications" :
            ack is not null ? "OriginalAcknowledgementMayHaveBegun" :
            retiredAt is not null ? "SourceNameSettled" :
            retired.Count != 0 ? "SourceRetirementMayHaveBegun" :
            deletion?.AcceptedAt is not null ? "SourceDeleteAccepted" :
            deletion is not null ? "SourceDeleteMayHaveBegun" :
            targetMember is not null ? "TargetBirthPrepared" :
            verified is not null ? "TargetVerified" :
            copy.Steps[^1].AcceptedAt is not null ? "CopyAccepted" : copy.Steps[0].StartedAt is not null ? "CopyMayHaveBegun" : "Prepared";
        return new(operationId, admission.SourceRoute.RootId, admission.TargetRoute.RootId,
            admission.SourceRoute.InstanceId, admission.TargetRoute.InstanceId, admission.Command.SourceMemberId, targetMember,
            admission.Members[0].Source.Protection.Context.Location.RelativePath, admission.Members[0].TargetRelativePath,
            stage, admission.PreparedAt, copy.Steps[0].StartedAt, copy.Steps[^1].AcceptedAt, deletion?.StartedAt, deletion?.AcceptedAt,
            retiredAt, retiredAt, !completed, journalAcknowledged, completed)
        { IsDirectory = true };
    }

    private async Task<MirrorPulseNamespaceTransferStatus> ReadTransferStatusCoreAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token).ConfigureAwait(false);
        if (retirement is not null && retirement.Intent.TransferEntry?.OperationId != copy.Plan.OperationId)
            throw new InvalidDataException("The transfer source retirement belongs to a different original operation.");
        bool completed = copy.Acknowledgement?.AcknowledgedAt is not null;
        bool journalAcknowledged = completed && copy.Acknowledgement!.Kind == MirrorPulseTransferAcknowledgementKind.OriginalJournal;
        string stage = completed ? journalAcknowledged ? "Completed" : "CompletedCommandWithoutCapturedNotifications" :
            copy.Acknowledgement is not null ? "OriginalAcknowledgementMayHaveBegun" :
            retirement?.CompletedAt is not null ? "SourceNameSettled" :
            retirement?.NativeDeletedAt is not null ? "SourceNativeRetired" :
            retirement?.StartedAt is not null ? "SourceRetirementMayHaveBegun" :
            retirement is not null ? "SourceRetirementPrepared" :
            copy.SourceDeletion?.AcceptedAt is not null ? "SourceDeleteAccepted" :
            copy.SourceDeletion is not null ? "SourceDeleteMayHaveBegun" :
            copy.TargetPublication is not null ? "TargetPublished" :
            copy.TargetBirth is not null ? "TargetBirthPrepared" :
            copy.TargetVerification is not null ? "TargetVerified" :
            copy.CopyAcceptedAt is not null ? "CopyAccepted" : copy.CopyStartedAt is not null ? "CopyMayHaveBegun" : "Prepared";
        var plan = copy.Plan;
        return new(plan.OperationId, plan.SourceRoute.RootId, plan.TargetRoute.RootId, plan.SourceRoute.InstanceId,
            plan.TargetRoute.InstanceId, plan.Source.Context.Member.MemberId, copy.TargetPublication?.MemberId,
            plan.Source.Context.Location.RelativePath, plan.TargetRelativePath, stage, plan.PreparedAt,
            copy.CopyStartedAt, copy.CopyAcceptedAt, copy.SourceDeletion?.StartedAt, copy.SourceDeletion?.AcceptedAt,
            retirement?.NativeDeletedAt, retirement?.CompletedAt, RootsFenced: !completed, JournalAcknowledged: journalAcknowledged, Completed: completed);
    }
}
