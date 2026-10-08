using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Captured at the original path before allowing a native rename.</summary>
public sealed record MirrorPulseRootRenameProof(Guid ItemId, MirrorPulseLocalFileBinding LocalObject,
    string PlaceholderIdentity, DateTimeOffset CapturedAt);

/// <summary>The same object inspected at the intended destination after native rename.</summary>
public sealed record MirrorPulseRootRenameObservation(MirrorPulseLocalFileBinding LocalObject,
    string PlaceholderIdentity, string DirectoryName, DateTimeOffset ObservedAt);

public sealed record MirrorPulseRootRenameHistory(MirrorPulseRootRenameIntent Intent,
    MirrorPulseRootRenameProof? Proof, MirrorPulseRootRenameObservation? Observation);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<IReadOnlyList<MirrorPulseRootRenameHistory>> ReadManagedRootRenameHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload, proof, observation FROM managed_root_rename_history ORDER BY rowid;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<MirrorPulseRootRenameHistory>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadRenameHistory(reader));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRootRenameHistory> ReadManagedRootRenameHistoryCoreAsync(Guid operationId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload, proof, observation FROM managed_root_rename_history WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString());
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new FileNotFoundException("The root rename history is not registered.");
        return ReadRenameHistory(reader);
    }

    private static MirrorPulseRootRenameHistory ReadRenameHistory(SqliteDataReader reader)
    {
        var intent = JsonSerializer.Deserialize<MirrorPulseRootRenameIntent>(reader.GetString(0), TopologyJsonOptions)
            ?? throw new InvalidDataException("The root rename history is invalid.");
        var proof = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<MirrorPulseRootRenameProof>(reader.GetString(1), TopologyJsonOptions);
        var observation = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<MirrorPulseRootRenameObservation>(reader.GetString(2), TopologyJsonOptions);
        return new(intent, proof, observation);
    }

    public async Task SaveManagedRootRenameProofAsync(Guid operationId, MirrorPulseRootRenameProof proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ValidateRenameBinding(proof.LocalObject, proof.PlaceholderIdentity);
        if (proof.ItemId == Guid.Empty || proof.CapturedAt == default) throw new ArgumentException("The original root proof is incomplete.", nameof(proof));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseRootRenameHistory history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (history.Proof is not null)
            {
                if (history.Proof != proof) throw new InvalidOperationException("The original root rename proof is immutable.");
                return;
            }
            if (history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared)
                throw new InvalidOperationException("A historical rename cannot adopt the current object's identity as its original proof.");
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE managed_root_rename_history SET proof=$proof WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString());
            command.Parameters.AddWithValue("$proof", JsonSerializer.Serialize(proof, TopologyJsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRootRenameIntent> ObserveManagedRootRenameAsync(Guid operationId,
        MirrorPulseRootRenameObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateRenameBinding(observation.LocalObject, observation.PlaceholderIdentity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseRootRenameHistory history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false);
            if (history.Proof is not { } proof || observation.LocalObject != proof.LocalObject ||
                observation.PlaceholderIdentity != proof.PlaceholderIdentity || observation.DirectoryName != history.Intent.TargetName ||
                observation.ObservedAt < proof.CapturedAt)
                throw new InvalidOperationException("The native rename observation does not match the original object and intended destination.");
            if (history.Observation is not null)
            {
                if (history.Observation != observation) throw new InvalidOperationException("The native rename observation is immutable.");
                return history.Intent;
            }
            if (history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared)
                throw new InvalidOperationException("The root rename is not awaiting native observation.");
            MirrorPulseRootRenameIntent updated = history.Intent with { Phase = MirrorPulseRootRenamePhase.NativeObserved, UpdatedAt = DateTimeOffset.UtcNow };
            await using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE managed_root_rename_history SET observation=$observation WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString());
            command.Parameters.AddWithValue("$observation", JsonSerializer.Serialize(observation, TopologyJsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SaveManagedRootRenameCoreAsync(updated, cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit();
            return updated;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateRenameBinding(MirrorPulseLocalFileBinding binding, string identity)
    {
        if (binding is null || binding.VolumeSerialNumber == 0 || binding.SyncRootFileId == Guid.Empty || binding.LocalFileId == Guid.Empty)
            throw new ArgumentException("The root's native object binding is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(identity); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        { throw new ArgumentException("The root's placeholder identity is invalid.", exception); }
        if (bytes.Length is 0 or > 4096 || Convert.ToBase64String(bytes) != identity)
            throw new ArgumentException("The exact root placeholder identity is required.");
    }
}
