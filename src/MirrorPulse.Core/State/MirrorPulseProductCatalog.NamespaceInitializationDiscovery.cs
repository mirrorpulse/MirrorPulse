using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds retained initialization attempts without requiring a caller to know their operation IDs.</summary>
    /// <remarks>Pages include historical completions. They do not authorize current native operations.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceInitializationStatus>> ReadNamespaceInitializationsAsync(
        Guid? afterOperationId = null, int limit = 256, CancellationToken cancellationToken = default)
    {
        if (afterOperationId == Guid.Empty) throw new ArgumentException("A paging cursor must be a non-empty operation ID.", nameof(afterOperationId));
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit), "Initialization pages contain between 1 and 256 attempts.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var operations = new List<Guid>();
            await using (SqliteCommand query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                // Include all indexes: a lost admission or an orphaned completion must not
                // disappear from recovery discovery merely because its intent row is absent.
                query.CommandText = """
                    SELECT operation_id FROM (
                        SELECT operation_id FROM namespace_initialization_intents
                        UNION SELECT operation_id FROM namespace_initialization_roots
                        UNION SELECT operation_id FROM namespace_initialization_completions)
                    WHERE operation_id > $after ORDER BY operation_id COLLATE BINARY LIMIT $limit;
                    """;
                query.Parameters.AddWithValue("$after", afterOperationId?.ToString("D") ?? string.Empty);
                query.Parameters.AddWithValue("$limit", limit);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string stored = reader.GetString(0);
                    if (!Guid.TryParseExact(stored, "D", out Guid operation) || operation == Guid.Empty || stored != operation.ToString("D"))
                        throw new InvalidDataException("The initialization discovery index is invalid.");
                    operations.Add(operation);
                }
            }
            var result = new List<MirrorPulseNamespaceInitializationStatus>(operations.Count);
            foreach (Guid operation in operations)
            {
                if (await ReadNamespaceInitializationCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidDataException("Initialization discovery found an index without its original admission.");
                result.Add(await ReadNamespaceInitializationStatusCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false));
            }
            transaction.Commit(); return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceInitializationStatus> ReadNamespaceInitializationStatusCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var intent = await ReadNamespaceInitializationCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The initialization admission is missing.");
        var completion = await ReadNamespaceInitializationCompletionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        if (completion is null) await RequireNamespaceInitializationRootsAsync(intent, transaction, token).ConfigureAwait(false);
        else await RequireNamespaceInitializationTerminalAsync(intent, completion, transaction, token).ConfigureAwait(false);
        return new(intent, completion);
    }
}
