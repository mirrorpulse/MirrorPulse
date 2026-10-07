using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed record MirrorPulseManagedRootName(RootId RootId, string DirectoryName);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<IReadOnlyList<MirrorPulseManagedRootName>> ReadManagedRootNamesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT root_id, directory_name FROM managed_root_names ORDER BY directory_name;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<MirrorPulseManagedRootName>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new(RootId.Parse(reader.GetString(0)), reader.GetString(1)));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private async Task ValidateManagedRootNamesAsync(MirrorPulseAdapterTopology topology,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT root_id, directory_name FROM managed_root_names;";
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            RootId owner = RootId.Parse(reader.GetString(0));
            string name = reader.GetString(1);
            if (topology.Roots.Any(root => root.RootId != owner &&
                string.Equals(root.DirectoryName, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A managed root name is reserved by another stable root identity.");
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (MirrorPulseRootRenameIntent intent in await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
        {
            if (!intent.IsPending) continue;
            if (topology.Roots.Any(root => root.RootId != intent.RootId &&
                string.Equals(root.DirectoryName, intent.TargetName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A managed root name is reserved by a pending namespace transition.");
            RootRegistration? owner = topology.Roots.SingleOrDefault(root => root.RootId == intent.RootId);
            if (owner is not null && owner.DirectoryName != intent.SourceName && owner.DirectoryName != intent.TargetName)
                throw new InvalidOperationException("A managed root label cannot replace a pending namespace transition.");
        }
    }

    /// <summary>Changes only a managed root's display mapping, preserving source and instance identity.</summary>
    public async Task<RootRegistration> RenameManagedRootAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            await using var query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseAdapterTopology current = payload is null ? new([], [], []) : DeserializeTopology(payload);
            RootRegistration original = current.Roots.SingleOrDefault(root => root.RootId == rootId)
                ?? throw new FileNotFoundException("The managed root is not registered.");
            if (original.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
                throw new InvalidOperationException("The managed root cannot be renamed in its current state.");
            string value = label.Trim();
            var updated = new RootRegistration(original.AdapterId, original.InstanceId, original.RootId,
                original.UniquenessKey, value, value, original.CustomEntry, original.State, original.RegisteredAt, original.IdentityScope);
            var next = new MirrorPulseAdapterTopology(current.Installations, current.Instances,
                current.Roots.Select(root => root.RootId == rootId ? updated : root).ToArray());
            ValidateTopology(next);
            await ValidateManagedRootNamesAsync(next, cancellationToken, transaction).ConfigureAwait(false);
            await using (SqliteCommand names = _connection.CreateCommand())
            {
                names.Transaction = transaction;
                names.CommandText = """
                    INSERT INTO managed_root_names (directory_name, root_id) VALUES ($old, $root), ($new, $root)
                    ON CONFLICT(directory_name) DO NOTHING;
                    """;
                names.Parameters.AddWithValue("$old", original.DirectoryName);
                names.Parameters.AddWithValue("$new", updated.DirectoryName);
                names.Parameters.AddWithValue("$root", rootId.ToString());
                await names.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE adapter_topology SET payload=$payload WHERE id=1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return updated;
        }
        finally { _gate.Release(); }
    }
}
