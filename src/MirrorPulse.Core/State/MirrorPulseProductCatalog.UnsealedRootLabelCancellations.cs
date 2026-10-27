using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The complete guarded cancellation of a public preparation that never acquired a namespace move.</summary>
/// <remarks>The retained inventory is cancellation evidence, not a fabricated move admission or native authority.</remarks>
public sealed record MirrorPulseRootLabelUnsealedCancellation(int Version, Guid RootMemberId,
    MirrorPulseRootLabelCancellation Observation, MirrorPulseNamespaceDirectoryMoveSnapshot Inventory)
{
    /// <summary>The exact original initializer retained by a version 2 cancellation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseRootLabelCancellationRecoveryOwner? RecoveryOwner { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseRootLabelUnsealedCancellation> CancelUnsealedRootLabelAsync(
        MirrorPulseRootLabelUnsealedCancellation cancellation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ValidateUnsealedRootLabelCancellation(cancellation);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cancellation, TopologyJsonOptions);
        if (payload.Length > RootLabelCancellationPayloadLimit)
            throw new ArgumentException("The complete unsealed cancellation must be bounded.", nameof(cancellation));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var observation = cancellation.Observation;
            var replay = await ReadUnsealedRootLabelCancellationRowAsync(observation.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(replay, TopologyJsonOptions)))
                    throw new InvalidOperationException("The original unsealed cancellation receipt is immutable.");
                await ValidateUnsealedRootLabelCancellationReferencesAsync(replay, false, transaction, cancellationToken).ConfigureAwait(false);
                transaction.Commit(); return replay;
            }
            await ValidateUnsealedRootLabelCancellationReferencesAsync(cancellation, true, transaction, cancellationToken).ConfigureAwait(false);
            var history = await ReadManagedRootRenameHistoryCoreAsync(observation.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            await using (var write = _connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = """
                    INSERT INTO root_label_unsealed_cancellations(operation_id,root_id,payload,fingerprint)
                    VALUES($operation,$root,$payload,$hash);
                    """;
                write.Parameters.AddWithValue("$operation", observation.OperationId.ToString("D"));
                write.Parameters.AddWithValue("$root", observation.RootId.ToString());
                write.Parameters.AddWithValue("$payload", payload); write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            // There is no namespace move, reservation or predecessor claim to
            // manufacture or delete. Only this exact original Label is closed.
            await SaveManagedRootRenameCoreAsync(history.Intent with
            {
                Phase = MirrorPulseRootRenamePhase.Cancelled,
                UpdatedAt = observation.VerifiedAt,
                UnsealedCancellationOperationId = observation.OperationId
            }, cancellationToken, transaction).ConfigureAwait(false);
            await ValidateUnsealedRootLabelCancellationReferencesAsync(cancellation, false, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return cancellation;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRootLabelUnsealedCancellation?> ReadUnsealedRootLabelCancellationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadUnsealedRootLabelCancellationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRootLabelUnsealedCancellation?> ReadUnsealedRootLabelCancellationCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var result = await ReadUnsealedRootLabelCancellationRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (result is null) return null;
        try { await ValidateUnsealedRootLabelCancellationReferencesAsync(result, false, transaction, token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The unsealed cancellation lost its original guarded terminal ownership.", failure); }
        return result;
    }

    private async Task<MirrorPulseRootLabelUnsealedCancellation?> ReadUnsealedRootLabelCancellationRowAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,root_id,payload,fingerprint FROM root_label_unsealed_cancellations WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceMovePayload<MirrorPulseRootLabelUnsealedCancellation>((byte[])reader[2], (byte[])reader[3],
            RootLabelCancellationPayloadLimit, ValidateUnsealedRootLabelCancellation);
        if (value.Observation.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") ||
            reader.GetString(1) != value.Observation.RootId.ToString())
            throw new InvalidDataException("The unsealed cancellation indexes disagree with their original Label.");
        return value;
    }
}
