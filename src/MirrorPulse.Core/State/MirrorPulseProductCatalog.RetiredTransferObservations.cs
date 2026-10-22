using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>Historical generation candidates, never a path-based inference of notification ownership.</summary>
public sealed record MirrorPulseRetiredTransferSource(MirrorPulseNamespaceTransferCopy Transfer, MirrorPulseNamespaceRetirement Retirement);

/// <summary>A separate original notification accepted as belonging to an already completed source generation.</summary>
/// <remarks>The first start is durable before public null-revision ACK. It does not extend the pre-copy claim,
/// invoke another remote delete, or prove that the SDK call returned until AcknowledgedAt is retained.</remarks>
public sealed record MirrorPulseRetiredTransferObservationAcknowledgement(int Version, Guid TransferOperationId,
    Guid RetirementOperationId, RootId SourceRootId, string SourceProjectionSha256, DateTimeOffset TransferCompletedAt,
    MirrorPulseTransferJournalObservation Observation, DateTimeOffset StartedAt, DateTimeOffset? AcknowledgedAt = null)
{
    public long SourceNameEpoch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReplacementMemberId { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Returns at most two original generations. Multiple candidates require separate reconciliation.</summary>
    public async Task<IReadOnlyList<MirrorPulseRetiredTransferSource>> ReadNamespaceTransferSourceGenerationsAsync(
        Guid itemId, CancellationToken token = default)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("A public logical item is required.", nameof(itemId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT operation_id FROM namespace_member_retirements INDEXED BY namespace_transfer_source_items
                    WHERE json_extract(CAST(payload AS TEXT),'$.StartedItemId')=$item
                        AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL
                    ORDER BY operation_id LIMIT 2;
                    """;
                query.Parameters.AddWithValue("$item", itemId.ToString("D"));
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            var results = new List<MirrorPulseRetiredTransferSource>(ids.Count);
            foreach (Guid id in ids)
            {
                var retirement = await ReadNamespaceRetirementCoreAsync(id, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("An indexed source generation lost its retirement.");
                var reference = retirement.Intent.TransferEntry ?? throw new InvalidDataException("A source generation lost its transfer owner.");
                var copy = await ReadNamespaceTransferCoreAsync(reference.OperationId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A source generation lost its original transfer.");
                if (retirement.StartedItemId != itemId) throw new InvalidDataException("The original item index disagrees with its source generation.");
                results.Add(new(copy, retirement));
            }
            transaction.Commit(); return results.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reserves one exact late notification before any ordinary product owner or public SDK ACK.</summary>
    /// <remarks>The Host first verifies the current official tombstone and source lineage under its actor and root lease.</remarks>
    public async Task<MirrorPulseRetiredTransferObservationAcknowledgement> StartRetiredTransferObservationAcknowledgementAsync(
        Guid transferOperationId, MirrorPulseTransferJournalObservation observation, DateTimeOffset startedAt,
        Guid? replacementMemberId = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(transferOperationId, transaction, token).ConfigureAwait(false);
            var projection = retirement.TransferSourceProjection
                ?? throw new InvalidOperationException("A legacy source cannot adopt a generation after deletion.");
            if (retirement.TransferSourceNameEpoch is null)
                throw new InvalidOperationException("A legacy retirement cannot adopt a name-generation witness after its name was released.");
            var existing = await ReadRetiredTransferObservationCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            long nameEpoch = existing?.SourceNameEpoch ?? await RequireRetiredTransferNameEpochAsync(copy, retirement,
                replacementMemberId, transaction, token).ConfigureAwait(false);
            var proposed = new MirrorPulseRetiredTransferObservationAcknowledgement(1, transferOperationId, retirement.OperationId,
                copy.Plan.SourceRoute.RootId, RetiredSourceProjectionHash(projection),
                copy.Acknowledgement?.AcknowledgedAt ?? throw new InvalidOperationException("Late notification settlement requires the completed original transfer."),
                observation, startedAt)
            { SourceNameEpoch = nameEpoch, ReplacementMemberId = replacementMemberId };
            ValidateRetiredTransferObservation(copy, retirement, proposed);
            if (existing is not null)
            {
                if (existing with { AcknowledgedAt = null } != (proposed with { StartedAt = existing.StartedAt }))
                    throw new InvalidOperationException("The original notification, source generation and first ACK start are immutable.");
                transaction.Commit(); return existing;
            }
            await RequireSoleRetiredTransferSourceAsync(projection.ItemId, retirement.OperationId, transaction, token).ConfigureAwait(false);
            await RequireNamespaceTransferIdAvailableAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            await WriteRetiredTransferObservationCoreAsync(proposed, transaction, insert: true, token).ConfigureAwait(false);
            transaction.Commit(); return proposed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains an actual successful public null-revision ACK return, without another remote effect.</summary>
    public async Task<MirrorPulseRetiredTransferObservationAcknowledgement> CompleteRetiredTransferObservationAcknowledgementAsync(
        MirrorPulseRetiredTransferObservationAcknowledgement originalStart, DateTimeOffset acknowledgedAt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(originalStart);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRetiredTransferObservationCoreAsync(originalStart.Observation.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("An ACK return requires its retained original notification start.");
            if (originalStart.AcknowledgedAt is not null || retained with { AcknowledgedAt = null } != originalStart)
                throw new InvalidOperationException("The ACK return must belong to the same original notification and first start.");
            if (retained.AcknowledgedAt is not null) { transaction.Commit(); return retained; }
            if (acknowledgedAt < retained.StartedAt) throw new ArgumentException("The SDK return cannot predate its original start.", nameof(acknowledgedAt));
            var completed = retained with { AcknowledgedAt = acknowledgedAt };
            await WriteRetiredTransferObservationCoreAsync(completed, transaction, insert: false, token).ConfigureAwait(false);
            await using var clear = _connection.CreateCommand(); clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM blocked_local_operations WHERE operation_id=$id AND reason IN ($reconcile,$identity);";
            clear.Parameters.AddWithValue("$id", retained.Observation.OperationId.ToString("D"));
            clear.Parameters.AddWithValue("$reconcile", (int)MirrorPulseLocalOperationBlockReason.RootReconciliationRequired);
            clear.Parameters.AddWithValue("$identity", (int)MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch);
            await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRetiredTransferObservationAcknowledgement?> ReadRetiredTransferObservationAcknowledgementAsync(
        Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original notification ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRetiredTransferObservationCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Bounded, indexed recovery for one root; callers rotate active roots rather than scan lifetime history.</summary>
    public async Task<IReadOnlyList<MirrorPulseRetiredTransferObservationAcknowledgement>> ReadPendingRetiredTransferObservationsAsync(
        RootId sourceRootId, int limit = 8, CancellationToken token = default)
    {
        if (sourceRootId.Value == Guid.Empty || limit is < 1 or > 64) throw new ArgumentException("A root and bounded recovery size are required.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true); var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT operation_id FROM retired_transfer_observations INDEXED BY retired_transfer_observation_pending WHERE status=0 AND source_root_id=$root ORDER BY started_utc,operation_id LIMIT $limit;";
                query.Parameters.AddWithValue("$root", sourceRootId.ToString()); query.Parameters.AddWithValue("$limit", limit);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            var results = new List<MirrorPulseRetiredTransferObservationAcknowledgement>(ids.Count);
            foreach (Guid id in ids) results.Add(await ReadRetiredTransferObservationCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A pending original notification lost its immutable ACK start."));
            transaction.Commit(); return results.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private static string RetiredSourceProjectionHash(MirrorPulseNamespaceTransferSourceProjection projection) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection, TopologyJsonOptions)));

    private static void ValidateRetiredTransferObservation(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement retirement,
        MirrorPulseRetiredTransferObservationAcknowledgement value)
    {
        var observation = value.Observation; var projection = retirement.TransferSourceProjection;
        if (value.Version != 1 || value.TransferOperationId != copy.Plan.OperationId || value.RetirementOperationId != retirement.OperationId ||
            retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.TransferReceipt is null || projection is null ||
            retirement.TransferSourceNameEpoch is not { } nameEpoch || value.SourceNameEpoch < nameEpoch ||
            (value.ReplacementMemberId is null ? value.SourceNameEpoch != nameEpoch : value.SourceNameEpoch != nameEpoch + 1) ||
            copy.Acknowledgement?.AcknowledgedAt is not { } completed || value.TransferCompletedAt != completed || value.StartedAt < completed ||
            value.SourceRootId != copy.Plan.SourceRoute.RootId || value.SourceProjectionSha256 != RetiredSourceProjectionHash(projection) ||
            observation is null || observation.OperationId == Guid.Empty || observation.Sequence <= 0 || observation.ItemId != projection.ItemId ||
            observation.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate or MirrorPulseWorkerChangeKind.MetadataUpdate or MirrorPulseWorkerChangeKind.Delete) ||
            observation.RelativePath is null || observation.RelativePath.Replace('\\', '/') != projection.RelativePath.Replace('\\', '/') ||
            observation.PreviousRelativePath is not null || observation.IsDirectory || observation.ObservedAt == default || observation.ObservedAt > value.StartedAt ||
            value.AcknowledgedAt is { } acknowledged && acknowledged < value.StartedAt)
            throw new ArgumentException("An original late notification requires its exact completed source generation, raw observation and ordered ACK receipt.");
    }

    private async Task<MirrorPulseRetiredTransferObservationAcknowledgement?> ReadRetiredTransferObservationCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseRetiredTransferObservationAcknowledgement value;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint,transfer_id,retirement_id,source_root_id,started_utc,status FROM retired_transfer_observations WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0], fingerprint = (byte[])reader[1];
            if (payload.Length > 262_144 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The original late notification fingerprint is invalid.");
            value = JsonSerializer.Deserialize<MirrorPulseRetiredTransferObservationAcknowledgement>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original notification ACK start is empty.");
            if (value.Observation is null || value.Observation.OperationId != operationId || reader.GetString(2) != value.TransferOperationId.ToString("D") ||
                reader.GetString(3) != value.RetirementOperationId.ToString("D") || reader.GetString(4) != value.SourceRootId.ToString() ||
                reader.GetString(5) != value.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ||
                reader.GetInt32(6) != (value.AcknowledgedAt is null ? 0 : 1) || !JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original notification ACK indexes disagree with its immutable record.");
        }
        var (copy, retirement) = await ReadTransferRetirementAsync(value.TransferOperationId, transaction, token).ConfigureAwait(false);
        try { ValidateRetiredTransferObservation(copy, retirement, value); }
        catch (ArgumentException error) { throw new InvalidDataException("The original notification lost its completed source generation.", error); }
        return value;
    }

    private async Task WriteRetiredTransferObservationCoreAsync(MirrorPulseRetiredTransferObservationAcknowledgement value,
        SqliteTransaction transaction, bool insert, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 262_144) throw new ArgumentException("The complete original notification ACK record must be bounded.", nameof(value));
        await using var write = _connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = insert ? "INSERT INTO retired_transfer_observations(operation_id,transfer_id,retirement_id,source_root_id,started_utc,status,payload,fingerprint) VALUES($id,$transfer,$retirement,$root,$started,$status,$payload,$hash);" :
            "UPDATE retired_transfer_observations SET status=$status,payload=$payload,fingerprint=$hash WHERE operation_id=$id AND status=0;";
        write.Parameters.AddWithValue("$id", value.Observation.OperationId.ToString("D"));
        write.Parameters.AddWithValue("$status", value.AcknowledgedAt is null ? 0 : 1);
        write.Parameters.AddWithValue("$payload", payload); write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (insert)
        {
            write.Parameters.AddWithValue("$transfer", value.TransferOperationId.ToString("D"));
            write.Parameters.AddWithValue("$retirement", value.RetirementOperationId.ToString("D"));
            write.Parameters.AddWithValue("$root", value.SourceRootId.ToString());
            write.Parameters.AddWithValue("$started", value.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        if (await write.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The original late notification ACK record was not retained.");
    }
}
