using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Records observed absence of the original local-only file at its source backend.</summary>
public sealed record MirrorPulseNamespaceTransferSourceAbsence(int Version, Guid OperationId, Guid DeleteOperationId,
    Guid TargetProtectionId, MirrorPulseTransferTargetVerification TargetAtObservation, DateTimeOffset ObservedAt)
{
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal MirrorPulseFileTransferRecovery? PublishedRecovery { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    internal async Task<MirrorPulseNamespaceTransferCopy> RecordNamespaceTransferSourceAbsenceAsync(Guid operationId,
        MirrorPulseTransferTargetVerification targetAtObservation, Guid targetProtectionId, DateTimeOffset observedAt,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(targetAtObservation);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var target = await RequireCurrentTransferSourceDispositionAsync(copy, transaction, token, recovery).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != targetProtectionId)
                throw new InvalidOperationException("Source absence requires the current independently verified target protection epoch.");
            if (copy.SourceAbsence is null)
            {
                var absence = new MirrorPulseNamespaceTransferSourceAbsence(1, operationId, copy.Plan.DeleteOperationId,
                    targetProtectionId, targetAtObservation, observedAt)
                { PublishedRecovery = recovery };
                ValidateNamespaceTransferSourceAbsence(copy, absence);
                await ValidateTransferSourceDispositionReferencesAsync(copy, targetProtectionId, observedAt, recovery, transaction, token).ConfigureAwait(false);
                copy = copy with { SourceAbsence = absence };
                await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
            return copy;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferSourceAbsence(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferSourceAbsence absence)
    {
        if (absence.Version != 1 || absence.OperationId != copy.Plan.OperationId || absence.DeleteOperationId != copy.Plan.DeleteOperationId ||
            copy.Plan.SourceRemoteRevision is not null || copy.SourceDeletion is not null || copy.Plan.SourceRoute.ProtocolVersion != 2 ||
            copy.TargetPublication is null || copy.TargetVerification is null || absence.TargetProtectionId == Guid.Empty ||
            absence.TargetAtObservation is null || absence.TargetAtObservation.RemoteId != copy.TargetVerification.RemoteId ||
            absence.TargetAtObservation.VerifiedAt < copy.TargetPublication.PublishedAt || absence.ObservedAt < absence.TargetAtObservation.VerifiedAt)
            throw new ArgumentException("Source absence requires the original local-only file and freshly verified published target.", nameof(absence));
        ValidateNamespaceTransferTargetVerification(copy, absence.TargetAtObservation);
    }
}
