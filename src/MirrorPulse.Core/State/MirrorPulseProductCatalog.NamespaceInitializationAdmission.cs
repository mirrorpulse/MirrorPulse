using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task InsertNamespaceInitializationCoreAsync(MirrorPulseNamespaceInitializationIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
        await using (SqliteCommand insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_initialization_intents(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var root in intent.Roots)
        {
            await using SqliteCommand reserve = _connection.CreateCommand(); reserve.Transaction = transaction;
            reserve.CommandText = "INSERT INTO namespace_initialization_roots(root_id,operation_id,manifest_id,born_member_id) VALUES($root,$id,$manifest,$born);";
            reserve.Parameters.AddWithValue("$root", root.RootId.Value.ToString("D"));
            reserve.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
            reserve.Parameters.AddWithValue("$manifest", root.ManifestId == Guid.Empty ? DBNull.Value : root.ManifestId.ToString("D"));
            reserve.Parameters.AddWithValue("$born", (object?)root.BornRootMemberId?.ToString("D") ?? DBNull.Value);
            await reserve.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}
