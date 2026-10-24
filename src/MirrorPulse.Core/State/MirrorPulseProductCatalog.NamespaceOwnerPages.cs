using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    internal const string NamespacePendingRetirementPageSql = """
        SELECT operation_id FROM namespace_member_retirements
        WHERE phase NOT IN (2,4) AND operation_id >= $after AND ($first=1 OR operation_id<>$after)
        ORDER BY operation_id LIMIT 16;
        """;

    internal const string NamespacePendingRetirementBatchPageSql = """
        SELECT operation_id FROM namespace_member_retirement_batches
        WHERE phase<>1 AND operation_id >= $after AND ($first=1 OR operation_id<>$after)
        ORDER BY operation_id LIMIT 16;
        """;

    internal const string NamespacePendingRemoteUpdatePageSql = """
        SELECT operation_id FROM (
            SELECT operation_id FROM (
                SELECT u.operation_id FROM namespace_remote_updates u
                LEFT JOIN namespace_remote_update_completions c ON c.operation_id=u.operation_id
                WHERE (u.finished<>1 OR c.operation_id IS NULL OR c.owner_fingerprint<>u.fingerprint)
                    AND u.operation_id >= $after AND ($first=1 OR u.operation_id<>$after)
                ORDER BY u.operation_id LIMIT 16)
            UNION SELECT operation_id FROM (
                SELECT operation_id FROM namespace_remote_update_pending
                WHERE operation_id >= $after AND ($first=1 OR operation_id<>$after)
                ORDER BY operation_id LIMIT 16)
            UNION SELECT operation_id FROM (
                SELECT c.operation_id FROM namespace_remote_update_completions c
                LEFT JOIN namespace_remote_updates u ON u.operation_id=c.operation_id
                WHERE (u.operation_id IS NULL OR u.finished<>1 OR c.owner_fingerprint<>u.fingerprint)
                    AND c.operation_id >= $after AND ($first=1 OR c.operation_id<>$after)
                ORDER BY c.operation_id LIMIT 16)
        ) ORDER BY operation_id LIMIT 16;
        """;

    internal const string NamespaceBornRecoveryMemberPageSql = """
        SELECT member_id FROM (
            SELECT member_id FROM (
                SELECT DISTINCT member_id FROM namespace_born_member_recovery_actions
                WHERE member_id >= $after AND ($first=1 OR member_id<>$after)
                ORDER BY member_id LIMIT 16)
            UNION SELECT member_id FROM (
                SELECT member_id FROM namespace_born_member_recovery_heads
                WHERE member_id >= $after AND ($first=1 OR member_id<>$after)
                ORDER BY member_id LIMIT 16)
            UNION SELECT member_id FROM (
                SELECT member_id FROM namespace_born_member_recovery_pending
                WHERE member_id >= $after AND ($first=1 OR member_id<>$after)
                ORDER BY member_id LIMIT 16)
        ) ORDER BY member_id LIMIT 16;
        """;

    /// <summary>Streams canonical owner IDs while retaining one page in the caller's original transaction.</summary>
    /// <remarks>Initial inclusive comparison also visits a corrupt empty ID. Each union branch is bounded
    /// before deduplication. Payload validation and complete recovery publication remain with the caller.</remarks>
    private async IAsyncEnumerable<Guid> ReadNamespaceOwnerIdsCoreAsync(string sql, SqliteTransaction transaction,
        [EnumeratorCancellation] CancellationToken token)
    {
        string after = string.Empty;
        while (true)
        {
            var page = new List<Guid>(16);
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = sql;
                query.Parameters.AddWithValue("$after", after);
                query.Parameters.AddWithValue("$first", after.Length == 0 ? 1 : 0);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                string previous = after;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    Guid id = ParseNamespaceMemberId(reader.GetString(0));
                    string current = id.ToString("D");
                    if (page.Count == 16 || StringComparer.Ordinal.Compare(previous, current) >= 0)
                        throw new InvalidDataException("The namespace owner page lost its strict canonical continuation.");
                    page.Add(id); previous = current;
                }
            }
            foreach (Guid id in page) yield return id;
            if (page.Count < 16) yield break;
            after = page[^1].ToString("D");
        }
    }
}
