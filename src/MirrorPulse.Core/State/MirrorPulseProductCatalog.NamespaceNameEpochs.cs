using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] NamespaceNameEpochSchema =
    [
        ("table", "namespace_name_epochs", """
            CREATE TABLE namespace_name_epochs(
                parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),child_name_key TEXT NOT NULL,
                epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>0),PRIMARY KEY(parent_member_id,child_name_key))
            """),
        ("trigger", "namespace_name_epoch_insert", """
            CREATE TRIGGER namespace_name_epoch_insert AFTER INSERT ON namespace_member_names BEGIN
                INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(NEW.parent_member_id,NEW.child_name_key,1)
                ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
            END
            """),
        ("trigger", "namespace_name_epoch_delete", """
            CREATE TRIGGER namespace_name_epoch_delete AFTER DELETE ON namespace_member_names BEGIN
                INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(OLD.parent_member_id,OLD.child_name_key,1)
                ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
            END
            """),
        ("trigger", "namespace_name_epoch_update", """
            CREATE TRIGGER namespace_name_epoch_update AFTER UPDATE ON namespace_member_names BEGIN
                INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(OLD.parent_member_id,OLD.child_name_key,1)
                ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
                INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(NEW.parent_member_id,NEW.child_name_key,1)
                ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
            END
            """)
    ];

    private static async Task EnsureNamespaceNameEpochSchemaAsync(SqliteConnection connection,
        long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name='namespace_name_epochs') OR (type='trigger' AND name IN ('namespace_name_epoch_insert','namespace_name_epoch_delete','namespace_name_epoch_update'));";
        if (originalVersion >= 85 && (long)(await schema.ExecuteScalarAsync(token).ConfigureAwait(false))! != 4)
            throw new InvalidDataException("Schema85 lost its native-name generation witnesses; current absence cannot replace original history.");
        foreach (var definition in NamespaceNameEpochSchema)
        {
            schema.Parameters.Clear();
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            schema.Parameters.AddWithValue("$type", definition.Type); schema.Parameters.AddWithValue("$name", definition.Name);
            var retained = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (retained is null)
            {
                schema.Parameters.Clear(); schema.CommandText = definition.Sql;
                await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (retained is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("The retained name-generation schema cannot prove its original transactional transitions.");
        }
        schema.Parameters.Clear();
        if (originalVersion < 85)
        {
            // Seed only names that are actually still registered. Never invent an epoch for
            // a historical retired name or backfill a before-native retirement witness.
            schema.CommandText = "INSERT OR IGNORE INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) SELECT parent_member_id,child_name_key,1 FROM namespace_member_names;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task<long> ReadNamespaceNameEpochCoreAsync(Guid parentMemberId, string relativePath,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT epoch FROM namespace_name_epochs WHERE parent_member_id=$parent AND child_name_key=$name;";
        query.Parameters.AddWithValue("$parent", parentMemberId.ToString("D")); query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(relativePath));
        return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is long value && value > 0 ? value :
            throw new InvalidDataException("The original source name has no retained generation witness.");
    }

    private async Task<long> RequireRetiredTransferNameEpochAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceRetirement retirement, Guid? replacementMemberId, SqliteTransaction transaction, CancellationToken token)
    {
        long expected = retirement.TransferSourceNameEpoch!.Value;
        long current = await ReadNamespaceNameEpochCoreAsync(copy.Plan.SourceParent.Context.Member.MemberId,
            copy.Plan.Source.Context.Location.RelativePath, transaction, token).ConfigureAwait(false);
        if (current == expected && replacementMemberId is null) return current;
        if (replacementMemberId is not { } memberId || current != expected + 1)
            throw new InvalidOperationException("An intervening source-name generation requires explicit reconciliation.");
        var replacement = await ReadNamespaceMemberProtectionSnapshotCoreAsync(memberId, transaction, token).ConfigureAwait(false);
        if (replacement is null || replacement.Context.Member.MemberId == copy.Plan.Source.Context.Member.MemberId ||
            replacement.Context.Member.LocalObject == copy.Plan.Source.Context.Member.LocalObject ||
            replacement.Context.Member.RootId != copy.Plan.SourceRoute.RootId ||
            replacement.Context.Location.ParentMemberId != copy.Plan.SourceParent.Context.Member.MemberId ||
            NamespaceMemberNameKey(replacement.Context.Location.RelativePath) != NamespaceMemberNameKey(copy.Plan.Source.Context.Location.RelativePath) ||
            replacement.Identity.ItemId is null || replacement.Identity.ItemId == copy.Plan.Source.Identity.ItemId)
            throw new InvalidOperationException("The current replacement must retain its own registered native binding and public identity.");
        await RequireNamespaceMemberNameAsync(replacement.Context.Member, transaction, token).ConfigureAwait(false);
        return current;
    }
}
