using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task ValidateNamespaceBirthReferenceIndexesAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction? transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NamespaceRecoveryReadSnapshot? snapshot = null;
        if (transaction is not null) _namespaceRecoveryReadSnapshots.TryGetValue(transaction, out snapshot);
        if (snapshot?.BirthReferenceIndexes.TryGetValue(birth, out _) == true) return;
        if (birth.RemoteEntry is null && birth.TransferEntry is null)
        {
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = """
                SELECT 1 FROM namespace_remote_birth_entries WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_transfer_target_births WHERE birth_id=$operation LIMIT 1;
                """;
            query.Parameters.AddWithValue("$operation", birth.OperationId.ToString("D"));
            // Writers and ordinary reads recheck every invocation. Explicitly
            // read-only snapshots may retain this fully validated reference state.
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
            {
                if (snapshot is not null) snapshot.BirthReferenceIndexes[birth] = true;
                return;
            }
        }
        await ValidateNamespaceRemoteBirthReferenceIndexAsync(birth, transaction, token).ConfigureAwait(false);
        await ValidateNamespaceTransferBirthReferenceIndexAsync(birth, transaction, token).ConfigureAwait(false);
        if (snapshot is not null) snapshot.BirthReferenceIndexes[birth] = true;
    }
}
