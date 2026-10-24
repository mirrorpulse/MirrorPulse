using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Validated product progress; settling a source name does not complete the transfer or acknowledge its journal.</summary>
public sealed record MirrorPulseNamespaceTransferStatus(Guid OperationId, RootId SourceRootId, RootId TargetRootId,
    InstanceId SourceInstanceId, InstanceId TargetInstanceId, Guid SourceMemberId, Guid? TargetMemberId,
    string SourceRelativePath, string TargetRelativePath, string Stage, DateTimeOffset PreparedAt,
    DateTimeOffset? CopyStartedAt, DateTimeOffset? CopyAcceptedAt, DateTimeOffset? SourceDeleteStartedAt,
    DateTimeOffset? SourceDeleteAcceptedAt, DateTimeOffset? SourceNativeRetiredAt, DateTimeOffset? SourceNameSettledAt,
    bool RootsFenced, bool JournalAcknowledged, bool Completed);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceTransferStatus?> ReadNamespaceTransferStatusAsync(Guid operationId,
        CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original transfer ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            var result = copy is null ? null : await ReadTransferStatusCoreAsync(copy, transaction, token).ConfigureAwait(false);
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
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var copy = await ReadPendingNamespaceTransferForRootCoreAsync(rootId, transaction, token).ConfigureAwait(false);
            MirrorPulseNamespaceTransferStatus[] result = copy is null ? [] :
                [await ReadTransferStatusCoreAsync(copy, transaction, token).ConfigureAwait(false)];
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
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
