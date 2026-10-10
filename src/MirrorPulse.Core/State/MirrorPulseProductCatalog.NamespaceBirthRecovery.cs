using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A finite delivery boundary for immutable birth admissions.</summary>
/// <remarks>Keep it within one Host recovery pass; it authorizes no native operation or permission change.</remarks>
public sealed record MirrorPulseNamespaceBirthRecoveryScan(long ThroughSequence);

/// <summary>Historical facts to reconcile against an actual object, not permission to repeat creation.</summary>
public sealed record MirrorPulseNamespaceBirthRecoveryEntry(long Sequence, MirrorPulseNamespaceBirthIntent Intent,
    MirrorPulseNamespaceBirthPlan? Plan, MirrorPulseNamespaceBirthStart? Start,
    MirrorPulseNamespaceBirthObservation? Observation, bool OwnsNameReservation)
{
    public MirrorPulseNamespaceBirthConversionPreparation? ConversionPreparation { get; init; }
}

public sealed record MirrorPulseNamespaceBirthRecoveryPage(IReadOnlyList<MirrorPulseNamespaceBirthRecoveryEntry> Entries,
    long LastScannedSequence, bool HasMore);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Captures the upper admission sequence without assuming prepared means unstarted.</summary>
    public async Task<MirrorPulseNamespaceBirthRecoveryScan> BeginNamespaceBirthRecoveryScanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT COALESCE(MAX(rowid),0) FROM namespace_birth_intents;";
            long through = (long)(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            return new(through);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads a bounded original-fact page, including admissions whose reservation is missing.</summary>
    /// <remarks>
    /// Birth intents are append-only; their SQLite rowids provide an in-process scan boundary.
    /// The Host must not retain this scan across a catalog migration. A missing reservation is
    /// reported for recovery, never treated as completion or silently omitted. Native inspection,
    /// current membership and protection verification remain the owner’s separate responsibilities.
    /// </remarks>
    public async Task<MirrorPulseNamespaceBirthRecoveryPage> ReadNamespaceBirthRecoveryPageAsync(
        MirrorPulseNamespaceBirthRecoveryScan scan, long afterSequence, int limit = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.ThroughSequence < 0 || afterSequence < 0 || afterSequence > scan.ThroughSequence || limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(afterSequence), "The birth recovery page bounds are invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var admissions = new List<(long Sequence, Guid OperationId)>();
            await using (SqliteCommand query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT rowid,operation_id FROM namespace_birth_intents
                    WHERE rowid>$after AND rowid<=$through ORDER BY rowid LIMIT $limit;
                    """;
                query.Parameters.AddWithValue("$after", afterSequence);
                query.Parameters.AddWithValue("$through", scan.ThroughSequence);
                query.Parameters.AddWithValue("$limit", limit + 1);
                await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    long sequence = reader.GetInt64(0);
                    string id = reader.GetString(1);
                    if (!Guid.TryParseExact(id, "D", out Guid operationId) || operationId == Guid.Empty || id != operationId.ToString("D"))
                        throw new InvalidDataException("A birth recovery admission has an invalid operation index.");
                    admissions.Add((sequence, operationId));
                }
            }
            bool hasMore = admissions.Count > limit;
            var entries = new List<MirrorPulseNamespaceBirthRecoveryEntry>(Math.Min(limit, admissions.Count));
            foreach (var admission in admissions.Take(limit))
            {
                var intent = await ReadNamespaceBirthCoreAsync(admission.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovery page lost its original birth admission.");
                var plan = await ReadNamespaceBirthPlanCoreAsync(admission.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                var start = await ReadNamespaceBirthStartCoreAsync(admission.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                var observation = await ReadNamespaceBirthObservationCoreAsync(admission.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                bool reserved;
                string expectedNameKey = intent.RelativePath[(intent.RelativePath.LastIndexOf('/') + 1)..].ToUpperInvariant();
                await using (SqliteCommand query = _connection.CreateCommand())
                {
                    query.Transaction = transaction;
                    query.CommandText = "SELECT parent_evidence_id,child_name_key FROM namespace_birth_reservations WHERE operation_id=$operation;";
                    query.Parameters.AddWithValue("$operation", admission.OperationId.ToString("D"));
                    await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    reserved = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (reserved && (reader.GetString(0) != intent.ParentEvidenceId.ToString("D") ||
                        reader.GetString(1) != expectedNameKey))
                        throw new InvalidDataException("The original birth name reservation is inconsistent.");
                }
                var conversion = await ReadNamespaceBirthConversionCoreAsync(admission.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                entries.Add(new(admission.Sequence, intent, plan, start, observation, reserved) { ConversionPreparation = conversion });
            }
            transaction.Commit();
            return new(entries.AsReadOnly(), entries.Count == 0 ? afterSequence : entries[^1].Sequence, hasMore);
        }
        finally { _gate.Release(); }
    }
}
