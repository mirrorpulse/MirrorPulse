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
        query.CommandText = "SELECT payload FROM managed_root_renames ORDER BY root_id;";
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MirrorPulseRootRenameIntent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            MirrorPulseRootRenameIntent intent = JsonSerializer.Deserialize<MirrorPulseRootRenameIntent>(reader.GetString(0), TopologyJsonOptions)
                ?? throw new InvalidDataException("The managed root rename intent is invalid.");
            if (intent.OperationId == Guid.Empty || !Enum.IsDefined(intent.Phase))
                throw new InvalidDataException("The managed root rename intent is invalid.");
            result.Add(intent);
        }
        return result.AsReadOnly();
    }

    public async Task<MirrorPulseRootRenameIntent> PrepareManagedRootRenameAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseAdapterTopology topology = payload is null ? new([], [], []) : DeserializeTopology(payload);
            RootRegistration root = topology.Roots.SingleOrDefault(root => root.RootId == rootId)
                ?? throw new FileNotFoundException("The managed root is not registered.");
            if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
                throw new InvalidOperationException("The managed root cannot be renamed in its current state.");
            string target = label.Trim();
            MirrorPulseRootRenameIntent? previous = (await ReadManagedRootRenamesCoreAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(intent => intent.RootId == rootId && intent.IsPending);
            if (previous is not null)
            {
                if (previous.TargetName == target && (root.DirectoryName == previous.SourceName || root.DirectoryName == previous.TargetName))
                    return previous;
                throw new InvalidOperationException("The managed root has a pending namespace transition.");
            }
            if (root.DirectoryName == target) throw new ArgumentException("The root already has this label.", nameof(label));
            var renamed = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
                target, target, root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
            var candidate = new MirrorPulseAdapterTopology(topology.Installations, topology.Instances,
                topology.Roots.Select(item => item.RootId == rootId ? renamed : item).ToArray());
            ValidateTopology(candidate);
            await ValidateManagedRootNamesAsync(candidate, cancellationToken).ConfigureAwait(false);
            var intent = new MirrorPulseRootRenameIntent(Guid.NewGuid(), rootId, root.DirectoryName, target,
                MirrorPulseRootRenamePhase.Prepared, DateTimeOffset.UtcNow);
            await SaveManagedRootRenameCoreAsync(intent, cancellationToken).ConfigureAwait(false);
            return intent;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRootRenameIntent> TransitionManagedRootRenameAsync(Guid operationId,
        MirrorPulseRootRenamePhase expected, MirrorPulseRootRenamePhase next, CancellationToken cancellationToken = default)
    {
        bool allowed = (expected, next) is (MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.NativeObserved)
            or (MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled)
            or (MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected)
            or (MirrorPulseRootRenamePhase.LocalProjected, MirrorPulseRootRenamePhase.Completed);
        if (!allowed) throw new InvalidOperationException("The managed root rename transition is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseRootRenameIntent current = (await ReadManagedRootRenamesCoreAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(intent => intent.OperationId == operationId)
                ?? throw new FileNotFoundException("The managed root rename intent is not registered.");
            if (current.Phase == next) return current;
            if (current.Phase != expected) throw new InvalidOperationException("The managed root rename phase has changed.");
            MirrorPulseRootRenameIntent updated = current with { Phase = next, UpdatedAt = DateTimeOffset.UtcNow };
            await SaveManagedRootRenameCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveManagedRootRenameCoreAsync(MirrorPulseRootRenameIntent intent, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO managed_root_renames (root_id, payload) VALUES ($root, $payload)
            ON CONFLICT(root_id) DO UPDATE SET payload=excluded.payload;
            """;
        command.Parameters.AddWithValue("$root", intent.RootId.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(intent, TopologyJsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
