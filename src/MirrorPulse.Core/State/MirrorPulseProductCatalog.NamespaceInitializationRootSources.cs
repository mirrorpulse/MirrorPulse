using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseNamespaceMemberRegistration> RequireNamespaceInitializationBornRootAsync(
        MirrorPulseNamespaceInitializationRoot root, DateTimeOffset preparedAt, SqliteTransaction transaction, CancellationToken token)
    {
        var member = await ReadNamespaceMemberCoreAsync(root.BornRootMemberId!.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected born root has no retained membership.");
        var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        var birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected born root lost its original birth admission.");
        var parent = location.ParentMemberId is { } parentId
            ? await ReadNamespaceMemberCoreAsync(parentId, transaction, token).ConfigureAwait(false) : null;
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.RootId != root.RootId ||
            !member.IsDirectory || member.ParentMemberId != location.ParentMemberId ||
            parent is not { RootId: null, ParentMemberId: null, IsDirectory: true } ||
            parent.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || parent.RelativePath.Length != 0 ||
            location.RelativePath.Length == 0 || location.RelativePath.Contains('/') ||
            birth.RootId != root.RootId || birth.ParentLocalObject != parent.LocalObject ||
            member.RegisteredAt == default || member.RegisteredAt > preparedAt || member.OwnerSid != parent.OwnerSid)
            throw new InvalidDataException("Current initialization requires the actual registered born root below its retained sync-root anchor.");
        return member;
    }

    private static async Task MigrateNamespaceInitializationRootSourcesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT 1 FROM pragma_table_info('namespace_initialization_roots') WHERE name='born_member_id';";
        if (await inspect.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) return;
        using var transaction = connection.BeginTransaction();
        await using var migrate = connection.CreateCommand(); migrate.Transaction = transaction;
        migrate.CommandText = """
            CREATE TABLE namespace_initialization_roots_v54 (
                root_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
                manifest_id TEXT UNIQUE, born_member_id TEXT UNIQUE,
                CHECK ((manifest_id IS NOT NULL AND born_member_id IS NULL) OR
                    (manifest_id IS NULL AND born_member_id IS NOT NULL)));
            INSERT INTO namespace_initialization_roots_v54(root_id,operation_id,manifest_id,born_member_id)
                SELECT root_id,operation_id,manifest_id,NULL FROM namespace_initialization_roots;
            DROP TABLE namespace_initialization_roots;
            ALTER TABLE namespace_initialization_roots_v54 RENAME TO namespace_initialization_roots;
            CREATE INDEX namespace_initialization_owner ON namespace_initialization_roots(operation_id);
            """;
        await migrate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
