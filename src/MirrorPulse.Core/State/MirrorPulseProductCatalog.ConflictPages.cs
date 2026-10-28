using System.Globalization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed record MirrorPulseConflictPage(IReadOnlyList<MirrorPulseConflictRecord> Items, string? NextCursor);

public sealed partial class MirrorPulseProductCatalog
{
    // DateTimeOffset's persisted round-trip form retains seven fractional digits
    // and an explicit offset. Remove the fraction before SQLite converts seconds,
    // so its millisecond date rounding cannot change a tick-precise boundary.
    private const string ConflictDetectedOrderExpression =
        "-(621355968000000000 + CAST(strftime('%s', substr(detected_utc,1,19) || substr(detected_utc,28)) AS INTEGER)*10000000 + CAST(substr(detected_utc,21,7) AS INTEGER))";

    /// <summary>Reads a bounded, newest-first page of both pending conflict families.</summary>
    /// <remarks>The cursor retains a strict time/source/ID boundary, not an offset.
    /// A page is one catalog snapshot; later pages can observe later changes without
    /// repeating a newer prefix or skipping a row because earlier conflicts resolved.
    /// Remote entries remain projections and grant no SDK conflict authority.</remarks>
    public async Task<MirrorPulseConflictPage> ReadPendingConflictPageAsync(int limit = 100, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ConflictPageKey? after = cursor is null ? null : ParseConflictPageCursor(cursor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var remote = await ReadConflictPageFamilyAsync(0, limit + 1, after, transaction, cancellationToken).ConfigureAwait(false);
            var upload = await ReadConflictPageFamilyAsync(1, limit + 1, after, transaction, cancellationToken).ConfigureAwait(false);
            var ordered = remote.Concat(upload).OrderBy(item => item.Key.Order)
                .ThenBy(item => item.Key.Source).ThenBy(item => item.Key.Id.ToString("D"), StringComparer.Ordinal).Take(limit + 1).ToArray();
            var rows = ordered.Take(limit).Select(item => item.Record).ToArray();
            string? next = ordered.Length > limit ? FormatConflictPageCursor(ordered[limit - 1].Key) : null;
            transaction.Commit(); return new(rows, next);
        }
        finally { _gate.Release(); }
    }

    private readonly record struct ConflictPageKey(long Order, int Source, Guid Id);

    private static ConflictPageKey ParseConflictPageCursor(string cursor)
    {
        if (cursor.Length is < 1 or > 96) throw new ArgumentException("The conflict cursor is invalid.", nameof(cursor));
        string[] parts = cursor.Split(':');
        if (parts.Length != 4 || parts[0] != "1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || parts[1] != ticks.ToString(CultureInfo.InvariantCulture) ||
            parts[2] is not ("0" or "1") || !Guid.TryParseExact(parts[3], "D", out Guid id) || id == Guid.Empty || parts[3] != id.ToString("D"))
            throw new ArgumentException("The conflict cursor is invalid or unsupported.", nameof(cursor));
        return new(-ticks, parts[2] == "0" ? 0 : 1, id);
    }

    private static string FormatConflictPageCursor(ConflictPageKey key) =>
        string.Create(CultureInfo.InvariantCulture, $"1:{-key.Order}:{key.Source}:{key.Id:D}");

    private async Task<IReadOnlyList<(ConflictPageKey Key, MirrorPulseConflictRecord Record)>> ReadConflictPageFamilyAsync(
        int source, int limit, ConflictPageKey? after, SqliteTransaction transaction, CancellationToken token)
    {
        string table = source == 0 ? "remote_conflict_projections" : "upload_conflicts";
        string pending = source == 0 ? string.Empty : "status=0 AND ";
        await using (var invalid = _connection.CreateCommand())
        {
            invalid.Transaction = transaction;
            invalid.CommandText = $"SELECT 1 FROM {table} WHERE {pending}{ConflictDetectedOrderExpression} IS NULL LIMIT 1;";
            if (await invalid.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A pending conflict lost its persisted detection time.");
        }
        if (after is { } retained && source == retained.Source)
        {
            // SQLite does not range-seek this expression index for a row-value
            // comparison. Keep the equal-time ID suffix and older-time range
            // as separate indexed seeks, sharing the original bounded row budget.
            var sameTime = await ReadConflictPageRangeAsync(source, limit, after,
                ConflictDetectedOrderExpression + "=$order AND conflict_id COLLATE BINARY>$after AND ", transaction, token, sameTime: true).ConfigureAwait(false);
            if (sameTime.Count == limit) return sameTime;
            var older = await ReadConflictPageRangeAsync(source, limit - sameTime.Count, after,
                ConflictDetectedOrderExpression + ">$order AND ", transaction, token).ConfigureAwait(false);
            return sameTime.Concat(older).ToArray();
        }
        string boundary = after is null ? string.Empty : ConflictDetectedOrderExpression +
            (source < after.Value.Source ? ">$order AND " : ">=$order AND ");
        return await ReadConflictPageRangeAsync(source, limit, after, boundary, transaction, token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<(ConflictPageKey Key, MirrorPulseConflictRecord Record)>> ReadConflictPageRangeAsync(
        int source, int limit, ConflictPageKey? after, string boundary, SqliteTransaction transaction, CancellationToken token, bool sameTime = false)
    {
        string table = source == 0 ? "remote_conflict_projections" : "upload_conflicts";
        string pending = source == 0 ? string.Empty : "status=0 AND ";
        string ordering = sameTime ? "conflict_id COLLATE BINARY" : ConflictDetectedOrderExpression + ",conflict_id COLLATE BINARY";
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = $"""
            SELECT conflict_id,instance_id,change_id,relative_path,reason,version_comparison,
                   local_revision,remote_revision,detected_utc,{ConflictDetectedOrderExpression}
            FROM {table} WHERE {pending}{boundary}1=1
            ORDER BY {ordering} LIMIT $limit;
            """;
        if (after is { } key) { query.Parameters.AddWithValue("$order", key.Order); query.Parameters.AddWithValue("$after", key.Id.ToString("D")); }
        query.Parameters.AddWithValue("$limit", limit);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<(ConflictPageKey, MirrorPulseConflictRecord)>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var detected = DateTimeOffset.ParseExact(reader.GetString(8), "O", CultureInfo.InvariantCulture);
            Guid id = Guid.ParseExact(reader.GetString(0), "D"); long order = reader.GetInt64(9);
            if (id == Guid.Empty || reader.GetString(0) != id.ToString("D") || order != -detected.UtcTicks)
                throw new InvalidDataException("A pending conflict lost its canonical paging identity or time.");
            result.Add((new(order, source, id), new(id, InstanceId.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                (MirrorPulseConflictReason)reader.GetInt32(4), (MirrorPulseVersionComparison)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), detected,
                source: source == 0 ? MirrorPulseConflictSource.CfSharpRemote : MirrorPulseConflictSource.Upload)));
        }
        return result;
    }

    private static async Task EnsureConflictPageIndexesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var indexes = connection.CreateCommand(); indexes.Transaction = transaction;
        // Optional query access paths, no record format or authority change. They
        // are created atomically with the existing base DDL on old and new catalogs.
        indexes.CommandText = $"""
            CREATE INDEX IF NOT EXISTS ix_remote_conflicts_page ON remote_conflict_projections({ConflictDetectedOrderExpression},conflict_id COLLATE BINARY);
            CREATE INDEX IF NOT EXISTS ix_upload_conflicts_page ON upload_conflicts(status,{ConflictDetectedOrderExpression},conflict_id COLLATE BINARY);
            """;
        await indexes.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
