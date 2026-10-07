using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Changes only a managed root's display mapping, preserving source and instance identity.</summary>
    public async Task<RootRegistration> RenameManagedRootAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var query = _connection.CreateCommand();
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
            await using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE adapter_topology SET payload=$payload WHERE id=1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }
}
