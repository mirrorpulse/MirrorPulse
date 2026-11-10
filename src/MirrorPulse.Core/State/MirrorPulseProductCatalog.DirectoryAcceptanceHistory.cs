using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseNamespaceMemberIdentity> ReadDirectoryMemberIdentityCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, (Guid? ItemId, string? RemoteId) original,
        SqliteTransaction transaction, CancellationToken token)
    {
        Guid? head = null; long total;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT a.operation_id FROM directory_acceptances a WHERE a.member_id=$member AND a.receipt IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM directory_acceptances b WHERE b.previous_operation_id=a.operation_id AND b.receipt IS NOT NULL)
                LIMIT 2;
                """;
            query.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) head = ParseNamespaceMemberId(reader.GetString(0));
            if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The directory has multiple confirmed identity heads.");
        }
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT COUNT(*) FROM directory_acceptances WHERE member_id=$member AND receipt IS NOT NULL;";
            query.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            total = (long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }
        if (head is null)
        {
            if (total != 0) throw new InvalidDataException("The directory's confirmed identity history has no head.");
            return new(member.MemberId, original.ItemId, original.RemoteId, null, original.ItemId, original.RemoteId);
        }
        var visited = new HashSet<Guid>(); Guid? current = head;
        MirrorPulseDirectoryAcceptance? accepted = null, successor = null;
        DateTimeOffset? successorTime = null;
        while (current is { } operationId)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(operationId)) throw new InvalidDataException("The directory identity history contains a cycle.");
            var value = await ReadDirectoryAcceptanceCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory identity predecessor is missing.");
            var receipt = await ReadDirectoryConfirmationCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory identity predecessor has no confirmation.");
            if (value.MemberId != member.MemberId || value.RootId != member.RootId || value.LocalObject != member.LocalObject ||
                receipt.ObservedAt < member.RegisteredAt || successor is not null &&
                (successor.PreviousItemId != value.AcceptedItemId || successor.PreviousRemoteId != value.RemoteId || successorTime < receipt.ObservedAt))
                throw new InvalidDataException("The directory identity history lost its original member or confirmed predecessor.");
            accepted ??= value; successor = value; successorTime = receipt.ObservedAt;
            current = value.PreviousAcceptanceOperationId;
            if (current is null && (value.PreviousItemId != original.ItemId || value.PreviousRemoteId != original.RemoteId))
                throw new InvalidDataException("The first directory acceptance lost its original public identity.");
        }
        if (visited.Count != total) throw new InvalidDataException("Directory confirmations do not form one continuous history.");
        return new(member.MemberId, accepted!.AcceptedItemId, accepted.RemoteId, head, original.ItemId, original.RemoteId);
    }

    private static readonly (string Type, string Name, string Sql)[] DirectoryAcceptanceSchema =
    [
        ("table", "directory_acceptances", """
            CREATE TABLE directory_acceptances(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                previous_operation_id TEXT NULL REFERENCES directory_acceptances(operation_id),
                payload BLOB NOT NULL CHECK(length(payload)<=65536),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                receipt BLOB NULL CHECK(length(receipt)<=65536),
                receipt_fingerprint BLOB NULL CHECK(length(receipt_fingerprint)=32),
                CHECK((receipt IS NULL)=(receipt_fingerprint IS NULL)))
            """),
        ("index", "ux_directory_identity_first", "CREATE UNIQUE INDEX ux_directory_identity_first ON directory_acceptances(member_id) WHERE previous_operation_id IS NULL AND receipt IS NOT NULL"),
        ("index", "ux_directory_identity_previous", "CREATE UNIQUE INDEX ux_directory_identity_previous ON directory_acceptances(previous_operation_id) WHERE previous_operation_id IS NOT NULL AND receipt IS NOT NULL"),
        ("index", "ix_directory_identity_member", "CREATE INDEX ix_directory_identity_member ON directory_acceptances(member_id)")
    ];

    private static async Task EnsureDirectoryAcceptanceSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in DirectoryAcceptanceSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            var existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 104) throw new InvalidDataException("Schema104 lost its original directory acceptance ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("Directory acceptance cannot adopt another schema definition.");
        }
        if (originalVersion < 104)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM directory_acceptances LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent original directory confirmations.");
            query.CommandText = "PRAGMA user_version=104;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
