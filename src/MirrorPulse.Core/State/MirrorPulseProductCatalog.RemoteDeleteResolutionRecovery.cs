using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds the unique original deletion owner without creating a replacement command.</summary>
    public async Task<MirrorPulseRemoteDeleteResolution?> ReadRemoteDeleteResolutionForConflictAsync(Guid conflictId, CancellationToken token = default)
    {
        if (conflictId == Guid.Empty) throw new ArgumentException("The original SDK conflict identity is required.", nameof(conflictId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT command_id FROM remote_delete_resolutions WHERE conflict_id=$conflict;";
            query.Parameters.AddWithValue("$conflict", conflictId.ToString("D"));
            var id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            var result = id is string value ? await RequireRemoteDeleteResolutionAsync(Guid.Parse(value), transaction, token).ConfigureAwait(false) : null;
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Finds a bounded page of owned decisions still requiring settlement or a terminal command receipt.</summary>
    public async Task<IReadOnlyList<MirrorPulseRemoteDeleteResolution>> ReadPendingRemoteDeleteResolutionsAsync(
        RootId rootId, Guid? afterCommandId = null, int limit = 64, CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty || afterCommandId == Guid.Empty || limit is < 1 or > 64)
            throw new ArgumentException("Explicit Delete recovery requires a stable root and a page of one to64 commands.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT r.command_id FROM remote_delete_resolutions r
                    LEFT JOIN remote_conflict_actions a ON a.command_id=r.command_id
                    WHERE r.root_id=$root AND ($after IS NULL OR r.command_id>$after)
                      AND (r.completed=0 OR a.outcome_payload IS NULL)
                    ORDER BY r.command_id LIMIT $limit;
                    """;
                query.Parameters.AddWithValue("$root", rootId.ToString());
                query.Parameters.AddWithValue("$after", afterCommandId is { } after ? after.ToString("D") : DBNull.Value);
                query.Parameters.AddWithValue("$limit", limit);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
            }
            var result = new List<MirrorPulseRemoteDeleteResolution>(ids.Count);
            foreach (var id in ids)
                result.Add(await RequireRemoteDeleteResolutionAsync(id, transaction, token).ConfigureAwait(false));
            transaction.Commit(); return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }
}
