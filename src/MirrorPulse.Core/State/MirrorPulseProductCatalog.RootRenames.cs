using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseRootRenamePhase { Prepared, NativeObserved, LocalProjected, Completed, Cancelled }

/// <summary>Local namespace intent; it never requests a source directory move.</summary>
public sealed record MirrorPulseRootRenameIntent(Guid OperationId, RootId RootId, string SourceName,
    string TargetName, MirrorPulseRootRenamePhase Phase, DateTimeOffset UpdatedAt)
{
    public bool IsPending => Phase is not (MirrorPulseRootRenamePhase.Completed or MirrorPulseRootRenamePhase.Cancelled);
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads one root's current intent without loading other roots or original histories.</summary>
    public async Task<MirrorPulseRootRenameIntent?> ReadManagedRootRenameAsync(RootId rootId,
        CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root is required.", nameof(rootId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload, root_id FROM managed_root_renames WHERE root_id=$root;";
            query.Parameters.AddWithValue("$root", rootId.ToString());
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseRootRenameIntent>> ReadManagedRootRenamesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadManagedRootRenamesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseRootRenameIntent>> ReadManagedRootRenamesCoreAsync(
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload, root_id FROM managed_root_renames ORDER BY root_id;";
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MirrorPulseRootRenameIntent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)));
        }
        return result.AsReadOnly();
    }

    private static MirrorPulseRootRenameIntent ReadRootRenameIntent(string payload, string rootId, string? operationId = null)
    {
        try
        {
            MirrorPulseRootRenameIntent intent = JsonSerializer.Deserialize<MirrorPulseRootRenameIntent>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The managed root rename intent is invalid.");
            if (intent.OperationId == Guid.Empty || intent.RootId.Value == Guid.Empty || !Enum.IsDefined(intent.Phase) ||
                intent.UpdatedAt == default || intent.RootId.ToString() != rootId ||
                operationId is not null && intent.OperationId.ToString() != operationId)
                throw new InvalidDataException("The managed root rename intent does not match its indexed owner.");
            return intent;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The managed root rename intent is invalid.", failure);
        }
    }

    public async Task<MirrorPulseRootRenameIntent> PrepareManagedRootRenameAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var prepared = await PrepareManagedRootRenameCoreAsync(rootId, label, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return prepared.Intent;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically retains a new intent and its original public proof before native authorization.</summary>
    public async Task<MirrorPulseRootRenameHistory> PrepareManagedRootRenameWithProofAsync(RootId rootId, string label,
        string expectedSourceName, MirrorPulseRootRenameProof proof, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceName);
        ArgumentNullException.ThrowIfNull(proof);
        ValidateRootRenameProof(proof);
        if (proof.DirectoryMoveEvidence is null)
            throw new ArgumentException("A new root rename requires its original public directory move evidence.", nameof(proof));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var prepared = await PrepareManagedRootRenameCoreAsync(rootId, label, transaction, cancellationToken).ConfigureAwait(false);
            if (!prepared.Created)
            {
                var original = await ReadManagedRootRenameHistoryCoreAsync(prepared.Intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (original.Proof?.DirectoryMoveEvidence is null)
                    throw new InvalidOperationException("The pending root rename has no original public directory move evidence.");
                transaction.Commit();
                return original;
            }
            if (prepared.Root.DirectoryName != expectedSourceName)
                throw new InvalidOperationException("The managed root source mapping changed during public proof capture.");
            await using (SqliteCommand retain = _connection.CreateCommand())
            {
                retain.Transaction = transaction;
                retain.CommandText = "UPDATE managed_root_rename_history SET proof=$proof WHERE operation_id=$operation AND proof IS NULL AND observation IS NULL;";
                retain.Parameters.AddWithValue("$operation", prepared.Intent.OperationId.ToString("D"));
                retain.Parameters.AddWithValue("$proof", JsonSerializer.Serialize(proof, TopologyJsonOptions));
                if (await retain.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The new root rename cannot replace existing original evidence.");
            }
            var complete = await ReadManagedRootRenameHistoryCoreAsync(prepared.Intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit();
            return complete;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseRootRenameIntent Intent, RootRegistration Root, bool Created)> PrepareManagedRootRenameCoreAsync(
        RootId rootId, string label, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        MirrorPulseAdapterTopology topology = payload is null ? new([], [], []) : DeserializeTopology(payload);
        RootRegistration root = topology.Roots.SingleOrDefault(root => root.RootId == rootId)
            ?? throw new FileNotFoundException("The managed root is not registered.");
        if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("The managed root cannot be renamed in its current state.");
        string target = label.Trim();
        MirrorPulseRootRenameIntent? previous = (await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
            .SingleOrDefault(intent => intent.RootId == rootId && intent.IsPending);
        if (previous is not null)
        {
            if (previous.TargetName == target && (root.DirectoryName == previous.SourceName || root.DirectoryName == previous.TargetName))
                return (previous, root, false);
            throw new InvalidOperationException("The managed root has a pending namespace transition.");
        }
        await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, cancellationToken).ConfigureAwait(false);
        if (root.DirectoryName == target) throw new ArgumentException("The root already has this label.", nameof(label));
        var renamed = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
            target, target, root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
        var candidate = new MirrorPulseAdapterTopology(topology.Installations, topology.Instances,
            topology.Roots.Select(item => item.RootId == rootId ? renamed : item).ToArray());
        ValidateTopology(candidate);
        await ValidateManagedRootNamesAsync(candidate, cancellationToken, transaction).ConfigureAwait(false);
        var intent = new MirrorPulseRootRenameIntent(Guid.NewGuid(), rootId, root.DirectoryName, target,
            MirrorPulseRootRenamePhase.Prepared, DateTimeOffset.UtcNow);
        await SaveManagedRootRenameCoreAsync(intent, cancellationToken, transaction).ConfigureAwait(false);
        return (intent, root, true);
    }

    public async Task<MirrorPulseRootRenameIntent> TransitionManagedRootRenameAsync(Guid operationId,
        MirrorPulseRootRenamePhase expected, MirrorPulseRootRenamePhase next, CancellationToken cancellationToken = default)
    {
        bool allowed = (expected, next) is (MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled)
            or (MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected)
            or (MirrorPulseRootRenamePhase.LocalProjected, MirrorPulseRootRenamePhase.Completed);
        if (!allowed) throw new InvalidOperationException("The managed root rename transition is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseRootRenameIntent current = (await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
                .SingleOrDefault(intent => intent.OperationId == operationId)
                ?? throw new FileNotFoundException("The managed root rename intent is not registered.");
            if (current.Phase == next) return current;
            if (current.Phase != expected) throw new InvalidOperationException("The managed root rename phase has changed.");
            if (await ReadNamespaceMemberMoveRowAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is { Version: 3 })
                throw new InvalidOperationException("A sealed root Label operation must settle its complete subtree atomically.");
            if (next is MirrorPulseRootRenamePhase.LocalProjected or MirrorPulseRootRenamePhase.Completed)
            {
                MirrorPulseRootRenameHistory history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false);
                if (history.Proof is null || history.Observation is null)
                    throw new InvalidOperationException("The rename requires its original native object proof and matching observation.");
                await using SqliteCommand topology = _connection.CreateCommand();
                topology.Transaction = transaction;
                topology.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
                string? payload = (string?)await topology.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                RootRegistration? root = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(root => root.RootId == current.RootId);
                if (root?.DirectoryName != current.TargetName)
                    throw new InvalidOperationException("The renamed root's display mapping has not been projected.");
            }
            MirrorPulseRootRenameIntent updated = current with { Phase = next, UpdatedAt = DateTimeOffset.UtcNow };
            await SaveManagedRootRenameCoreAsync(updated, cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit();
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveManagedRootRenameCoreAsync(MirrorPulseRootRenameIntent intent, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using SqliteTransaction? owned = transaction is null ? _connection.BeginTransaction() : null;
        await using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction ?? owned;
        command.CommandText = """
            INSERT INTO managed_root_renames (root_id, payload) VALUES ($root, $payload)
            ON CONFLICT(root_id) DO UPDATE SET payload=excluded.payload;
            INSERT INTO managed_root_rename_history (operation_id, root_id, payload) VALUES ($operation, $root, $payload)
            ON CONFLICT(operation_id) DO UPDATE SET payload=excluded.payload;
            """;
        command.Parameters.AddWithValue("$operation", intent.OperationId.ToString());
        command.Parameters.AddWithValue("$root", intent.RootId.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(intent, TopologyJsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        owned?.Commit();
    }
}
