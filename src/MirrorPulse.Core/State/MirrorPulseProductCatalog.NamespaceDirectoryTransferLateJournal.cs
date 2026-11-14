using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferLateAcknowledgement(Guid TransferId, RootId SourceRootId,
    MirrorPulseTransferJournalObservation Observation, DateTimeOffset StartedAt, DateTimeOffset? AcknowledgedAt = null);

public sealed partial class MirrorPulseProductCatalog
{
    internal async Task<MirrorPulseDirectoryTransferLateAcknowledgement> StartDirectoryTransferLateAcknowledgementAsync(
        Guid transferId, MirrorPulseTransferJournalObservation observation, DateTimeOffset startedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var prior = await ReadDirectoryTransferLateAcknowledgementCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.TransferId != transferId || prior.Observation != observation)
                    throw new InvalidOperationException("The original late directory journal notification cannot change owners.");
                transaction.Commit();
                return prior;
            }
            var (plan, _) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
            var completed = await ReadDirectoryTransferAcknowledgementCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            var retirements = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            var retired = retirements.SingleOrDefault(row => row.Projection.ItemId == observation.ItemId);
            if (completed?.AcknowledgedAt is not { } acknowledged || retired?.RetiredAt is null ||
                startedAt < acknowledged || observation.ObservedAt > startedAt || observation.ObservedAt == default ||
                observation.Sequence <= 0 || observation.PreviousRelativePath is not null ||
                observation.IsDirectory != plan.Members[retired.Ordinal].Source.Protection.Context.Member.IsDirectory ||
                observation.RelativePath.Replace('\\', '/') != retired.Projection.RelativePath.Replace('\\', '/') ||
                observation.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate or
                    MirrorPulseWorkerChangeKind.MetadataUpdate or MirrorPulseWorkerChangeKind.Delete) ||
                plan.Originals.Members.Any(original => original.OperationId == observation.OperationId))
                throw new InvalidOperationException("The late notification does not identify an original retired directory transfer member.");
            await RequireDirectoryTransferSettledSourceNamesAsync(plan, retirements, transaction, token).ConfigureAwait(false);
            await RequireNamespaceTransferIdAvailableAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            var result = new MirrorPulseDirectoryTransferLateAcknowledgement(transferId, plan.SourceRoute.RootId, observation, startedAt);
            validation.Dispose();
            await WriteDirectoryTransferLateAcknowledgementAsync(result, true, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferLateAcknowledgement?> ReadDirectoryTransferLateAcknowledgementAsync(Guid operationId,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var original = await ReadDirectoryTransferLateAcknowledgementCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return original;
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteDirectoryTransferLateAcknowledgementAsync(MirrorPulseDirectoryTransferLateAcknowledgement start,
        DateTimeOffset acknowledgedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var prior = await ReadDirectoryTransferLateAcknowledgementCoreAsync(start.Observation.OperationId, transaction, token).ConfigureAwait(false);
            if (prior is null || (prior with { AcknowledgedAt = null }) != (start with { AcknowledgedAt = null }) ||
                acknowledgedAt < prior.StartedAt)
                throw new InvalidOperationException("Late acknowledgement requires its first original SDK invocation.");
            if (prior.AcknowledgedAt is null)
                await WriteDirectoryTransferLateAcknowledgementAsync(prior with { AcknowledgedAt = acknowledgedAt }, false, transaction, token).ConfigureAwait(false);
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = "DELETE FROM blocked_local_operations WHERE operation_id=$id AND reason=$reason;";
            sql.Parameters.AddWithValue("$id", start.Observation.OperationId.ToString("D"));
            sql.Parameters.AddWithValue("$reason", (int)MirrorPulseLocalOperationBlockReason.RootReconciliationRequired);
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<MirrorPulseDirectoryTransferLateAcknowledgement>> ReadPendingDirectoryTransferLateAcknowledgementsAsync(
        RootId rootId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var ids = new List<Guid>();
            await using (var sql = _connection.CreateCommand())
            {
                sql.Transaction = transaction;
                sql.CommandText = """
                    SELECT operation_id FROM namespace_directory_transfer_late_acknowledgements
                    WHERE source_root_id=$root AND acknowledged=0 ORDER BY operation_id LIMIT 2;
                    """;
                sql.Parameters.AddWithValue("$root", rootId.ToString());
                await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            var result = new List<MirrorPulseDirectoryTransferLateAcknowledgement>(ids.Count);
            foreach (var id in ids)
            {
                var start = await ReadDirectoryTransferLateAcknowledgementCoreAsync(id, transaction, token).ConfigureAwait(false);
                if (start is null || start.SourceRootId != rootId || start.AcknowledgedAt is not null)
                    throw new InvalidDataException("The pending late directory notification lost its original start.");
                result.Add(start);
            }
            transaction.Commit();
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseDirectoryTransferLateAcknowledgement?> ReadDirectoryTransferLateAcknowledgementCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = """
            SELECT l.transfer_id,l.payload,l.fingerprint,l.source_root_id,l.acknowledged,a.source_root_id
            FROM namespace_directory_transfer_late_acknowledgements l
            JOIN namespace_directory_transfer_admissions a ON a.operation_id=l.transfer_id
            WHERE l.operation_id=$id;
            """;
        sql.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        Guid owner = ParseNamespaceMemberId(reader.GetString(0));
        return ReadNamespaceMovePayload<MirrorPulseDirectoryTransferLateAcknowledgement>((byte[])reader[1], (byte[])reader[2],
            131_072, row =>
            {
                if (row.TransferId != owner || row.Observation?.OperationId != operationId || row.Observation.ItemId is null ||
                    row.StartedAt < row.Observation.ObservedAt || row.AcknowledgedAt < row.StartedAt ||
                    row.SourceRootId.ToString() != reader.GetString(3) || reader.GetString(3) != reader.GetString(5) ||
                    (row.AcknowledgedAt is null ? 0 : 1) != reader.GetInt64(4))
                    throw new InvalidDataException("The late notification acknowledgement changed its original identity or time.");
            });
    }

    private async Task WriteDirectoryTransferLateAcknowledgementAsync(MirrorPulseDirectoryTransferLateAcknowledgement row,
        bool insert, SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(row, TopologyJsonOptions);
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = insert
            ? """
                INSERT INTO namespace_directory_transfer_late_acknowledgements(operation_id,transfer_id,source_root_id,acknowledged,payload,fingerprint)
                VALUES($id,$transfer,$root,$ack,$payload,$hash);
                """
            : """
                UPDATE namespace_directory_transfer_late_acknowledgements SET payload=$payload,fingerprint=$hash,acknowledged=$ack
                WHERE operation_id=$id AND transfer_id=$transfer AND source_root_id=$root AND acknowledged=0;
                """;
        sql.Parameters.AddWithValue("$id", row.Observation.OperationId.ToString("D"));
        sql.Parameters.AddWithValue("$transfer", row.TransferId.ToString("D"));
        sql.Parameters.AddWithValue("$root", row.SourceRootId.ToString());
        sql.Parameters.AddWithValue("$ack", row.AcknowledgedAt is null ? 0 : 1);
        sql.Parameters.AddWithValue("$payload", payload);
        sql.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The late directory notification acknowledgement could not be retained.");
    }
}
