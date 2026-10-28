using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task ValidateNamespaceBirthReferenceIndexesAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction? transaction, CancellationToken token)
    {
        if (birth.RemoteEntry is null && birth.TransferEntry is null)
        {
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = """
                SELECT 1 FROM namespace_remote_birth_entries WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_transfer_target_births WHERE birth_id=$operation LIMIT 1;
                """;
            query.Parameters.AddWithValue("$operation", birth.OperationId.ToString("D"));
            // Recheck both indexes on every invocation, including within a writer
            // transaction. An unexpected row must still pass the original validators.
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is null) return;
        }
        await ValidateNamespaceRemoteBirthReferenceIndexAsync(birth, transaction, token).ConfigureAwait(false);
        await ValidateNamespaceTransferBirthReferenceIndexAsync(birth, transaction, token).ConfigureAwait(false);
    }
}
