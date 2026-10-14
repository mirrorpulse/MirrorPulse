using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds retained original permission evidence by its complete native object binding.</summary>
    /// <remarks>Never selects an original by its current path or substitutes a new capture for missing history.</remarks>
    public async Task<MirrorPulseNamespacePermissionBaseline?> ReadNamespacePermissionBaselineByBindingAsync(
        MirrorPulseLocalFileBinding binding, CancellationToken cancellationToken = default)
    {
        ValidatePermissionBinding(binding);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            Guid? evidence = null;
            await using (SqliteCommand query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT evidence_id FROM namespace_permission_baselines
                    WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;
                    """;
                AddNamespaceBirthBindingParameters(query, binding);
                object? stored = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    if (stored is not string text || !Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty)
                        throw new InvalidDataException("The retained original permission binding has an invalid evidence ID.");
                    evidence = id;
                }
            }
            MirrorPulseNamespacePermissionBaseline? baseline = evidence is { } evidenceId
                ? await ReadPermissionBaselineCoreAsync(evidenceId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original binding lost its retained permission evidence.") : null;
            if (baseline is not null && baseline.LocalObject != binding)
                throw new InvalidDataException("The retained original permission binding disagrees with its evidence.");
            transaction.Commit(); return baseline;
        }
        finally { _gate.Release(); }
    }
}
