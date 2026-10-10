using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The logical placeholder identity selected before any native birth attempt.</summary>
/// <remarks>The Host validates its encoding through CfSharp. This plan contains no native binding or acceptance proof.</remarks>
public sealed record MirrorPulseNamespaceBirthPlan(int Version, Guid OperationId, Guid ItemId,
    string RemoteId, string? RemoteRevision, DateTimeOffset PreparedAt);

/// <summary>An immutable start intent committed before a native birth attempt.</summary>
/// <remarks>
/// Started does not prove that an object exists. Reading or replaying this record never
/// authorizes a repeated native creation, nor does it prove that a prior attempt did not run.
/// The Host reconciles the original plan and retains a separate actual birth observation.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthStart(int Version, Guid OperationId, DateTimeOffset StartedAt);

/// <summary>Distinguishes the first committed start intent from historical replay.</summary>
/// <remarks>NewlyRecorded describes catalog work only; the Host still owns native admission and execution.</remarks>
public sealed record MirrorPulseNamespaceBirthStartPreparation(MirrorPulseNamespaceBirthStart Start, bool NewlyRecorded);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains the exact logical identity once, while the original parent and reservation are current.</summary>
    public async Task<MirrorPulseNamespaceBirthPlan> PrepareNamespaceBirthPlanAsync(MirrorPulseNamespaceBirthPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateNamespaceBirthPlan(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var birth = await ReadNamespaceBirthCoreAsync(plan.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original birth admission is missing.");
            ValidateNamespaceBirthPlanReference(plan, birth);
            var retained = await ReadNamespaceBirthPlanCoreAsync(plan.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != plan) throw new InvalidOperationException("The original birth identity plan is immutable.");
                transaction.Commit();
                return retained;
            }
            await using (SqliteCommand begun = _connection.CreateCommand())
            {
                begun.Transaction = transaction;
                begun.CommandText = "SELECT 1 FROM namespace_birth_starts WHERE operation_id=$operation;";
                begun.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
                if (await begun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("A started birth cannot invent a missing original identity plan.");
            }
            await ValidateNamespaceBirthCurrentParentAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand existing = _connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT 1 FROM namespace_permission_local_identities WHERE item_id=$item;";
                existing.Parameters.AddWithValue("$item", plan.ItemId.ToString("D"));
                if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("A new birth cannot adopt an original protected file identity.");
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(plan, TopologyJsonOptions);
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_plans(operation_id,item_id,payload,fingerprint)
                VALUES($operation,$item,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$item", plan.ItemId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return plan;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists the start intent after fresh catalog admission, before the Host attempts creation.</summary>
    public async Task<MirrorPulseNamespaceBirthStartPreparation> RecordNamespaceBirthStartAsync(MirrorPulseNamespaceBirthStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ValidateNamespaceBirthStart(start);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var plan = await ReadNamespaceBirthPlanCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original birth identity plan is missing.");
            if (start.StartedAt < plan.PreparedAt)
                throw new InvalidOperationException("The birth start cannot predate its original plan.");
            var retained = await ReadNamespaceBirthStartCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != start) throw new InvalidOperationException("The original birth start intent is immutable.");
                transaction.Commit();
                return new(retained, false);
            }
            var birth = await ReadNamespaceBirthCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original birth admission is missing.");
            await ValidateNamespaceBirthCurrentParentAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_birth_starts(operation_id,payload,fingerprint) VALUES($operation,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$operation", start.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new(start, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthPlan?> ReadNamespaceBirthPlanAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthPlanCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthStart?> ReadNamespaceBirthStartAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthStartCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthPlan?> ReadNamespaceBirthPlanCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var artifact = await ReadNamespaceBirthArtifactCoreAsync(operationId, isPlan: true, transaction, token).ConfigureAwait(false);
        if (artifact is null) return null;
        MirrorPulseNamespaceBirthPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthPlan>(artifact.Value.Payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The birth identity plan is empty.");
            ValidateNamespaceBirthPlan(plan);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidDataException("The birth identity plan is invalid.", exception); }
        if (plan.OperationId != operationId || plan.ItemId.ToString("D") != artifact.Value.ItemId ||
            !JsonSerializer.SerializeToUtf8Bytes(plan, TopologyJsonOptions).AsSpan().SequenceEqual(artifact.Value.Payload))
            throw new InvalidDataException("The birth identity plan's index or canonical payload is inconsistent.");
        var birth = await ReadNamespaceBirthCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The birth identity plan lost its original admission.");
        try { ValidateNamespaceBirthPlanReference(plan, birth); }
        catch (InvalidOperationException exception) { throw new InvalidDataException("The birth identity plan reference is inconsistent.", exception); }
        return plan;
    }

    private async Task<MirrorPulseNamespaceBirthStart?> ReadNamespaceBirthStartCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var artifact = await ReadNamespaceBirthArtifactCoreAsync(operationId, isPlan: false, transaction, token).ConfigureAwait(false);
        if (artifact is null) return null;
        MirrorPulseNamespaceBirthStart start;
        try
        {
            start = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthStart>(artifact.Value.Payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The birth start intent is empty.");
            ValidateNamespaceBirthStart(start);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidDataException("The birth start intent is invalid.", exception); }
        if (start.OperationId != operationId || !JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions)
            .AsSpan().SequenceEqual(artifact.Value.Payload))
            throw new InvalidDataException("The birth start intent's index or canonical payload is inconsistent.");
        var plan = await ReadNamespaceBirthPlanCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The birth start intent lost its original plan.");
        if (start.StartedAt < plan.PreparedAt) throw new InvalidDataException("The birth start intent predates its original plan.");
        return start;
    }

    private async Task<(byte[] Payload, string? ItemId)?> ReadNamespaceBirthArtifactCoreAsync(Guid operationId,
        bool isPlan, SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = isPlan
            ? "SELECT operation_id,item_id,payload,fingerprint FROM namespace_birth_plans WHERE operation_id=$operation;"
            : "SELECT operation_id,NULL,payload,fingerprint FROM namespace_birth_starts WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[2];
        byte[] fingerprint = (byte[])reader[3];
        if (payload.Length is 0 or > 65_536 || fingerprint.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint) ||
            reader.GetString(0) != operationId.ToString("D"))
            throw new InvalidDataException("The birth artifact's index or fingerprint is invalid.");
        return (payload, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task RequireNamespaceBirthReservationAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT operation_id FROM namespace_birth_reservations WHERE parent_evidence_id=$parent AND child_name_key=$child;";
        query.Parameters.AddWithValue("$parent", birth.ParentEvidenceId.ToString("D"));
        query.Parameters.AddWithValue("$child", birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..].ToUpperInvariant());
        if ((string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false) != birth.OperationId.ToString("D"))
            throw new InvalidOperationException("The original birth no longer owns its pending child name.");
    }

    private static void ValidateNamespaceBirthPlan(MirrorPulseNamespaceBirthPlan plan)
    {
        if (plan.Version != 1 || plan.OperationId == Guid.Empty || plan.ItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(plan.RemoteId) || plan.RemoteId.Length > 4096 ||
            plan.RemoteRevision is not null && (string.IsNullOrWhiteSpace(plan.RemoteRevision) || plan.RemoteRevision.Length > 4096) ||
            plan.PreparedAt == default)
            throw new ArgumentException("The birth identity plan is incomplete or unsupported.", nameof(plan));
    }

    private static void ValidateNamespaceBirthPlanReference(MirrorPulseNamespaceBirthPlan plan, MirrorPulseNamespaceBirthIntent birth)
    {
        if (plan.OperationId != birth.OperationId || plan.PreparedAt < birth.PreparedAt ||
            birth.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation && plan.RemoteRevision is not null)
            throw new InvalidOperationException("The birth identity plan must follow its original admission without inventing remote acceptance.");
    }

    private static void ValidateNamespaceBirthStart(MirrorPulseNamespaceBirthStart start)
    {
        if (start.Version != 1 || start.OperationId == Guid.Empty || start.StartedAt == default)
            throw new ArgumentException("The birth start intent is incomplete or unsupported.", nameof(start));
    }
}
