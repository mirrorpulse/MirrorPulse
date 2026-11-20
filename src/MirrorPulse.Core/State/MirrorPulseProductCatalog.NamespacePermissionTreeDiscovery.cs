using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Discovers retained original captures for one stable root in bounded manifest order.</summary>
    /// <remarks>
    /// Includes open and cancelled captures so startup cannot mistake missing executable changes
    /// for missing recovery obligations. This is a historical inventory, not current tree readiness.
    /// Application must separately revalidate the seal, live membership, binding and permissions.
    /// </remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespacePermissionTree>> ReadNamespacePermissionTreesAsync(
        RootId rootId, Guid? afterManifestId = null, int limit = 256, CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty || afterManifestId == Guid.Empty || limit is < 1 or > 256)
            throw new ArgumentException("A stable root and a bounded permission capture page are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var ids = new List<Guid>();
            await using (SqliteCommand query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT manifest_id FROM namespace_permission_trees
                    WHERE root_id=$root AND manifest_id>$after ORDER BY manifest_id LIMIT $limit;
                    """;
                query.Parameters.AddWithValue("$root", rootId.Value.ToString("D"));
                query.Parameters.AddWithValue("$after", afterManifestId?.ToString("D") ?? string.Empty);
                query.Parameters.AddWithValue("$limit", limit);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty)
                        throw new InvalidDataException("The retained permission manifest ID is invalid.");
                    ids.Add(id);
                }
            }
            var trees = new List<MirrorPulseNamespacePermissionTree>(ids.Count);
            foreach (Guid id in ids)
            {
                var tree = await ReadPermissionTreeCoreAsync(id, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A retained permission manifest disappeared from its snapshot.");
                if (tree.Definition.Anchor.Intent.RootId != rootId)
                    throw new InvalidDataException("The permission manifest escaped its indexed root.");
                await ValidatePermissionTreeCancellationCoreAsync(tree, cancellationToken, transaction).ConfigureAwait(false);
                trees.Add(tree);
            }
            transaction.Commit();
            return trees;
        }
        finally { _gate.Release(); }
    }
}
