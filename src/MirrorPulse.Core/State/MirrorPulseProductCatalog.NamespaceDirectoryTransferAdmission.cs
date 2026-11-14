using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferMember(
    MirrorPulseNamespaceProtectedTreeMember Source, string TargetRelativePath, string? SourceRevision,
    Guid CopyOperationId, Guid DeleteOperationId, Guid? PrivateContentId, MirrorPulseNamespaceImportContent? Content);

internal sealed record MirrorPulseDirectoryTransferAdmission(
    int Version, MirrorPulseNamespaceTransferCommandOrigin Command, MirrorPulseTransferRoute SourceRoute,
    MirrorPulseTransferRoute TargetRoute, MirrorPulseNamespaceMemberProtectionSnapshot SourceParent,
    MirrorPulseNamespaceMemberProtectionSnapshot TargetParent, IReadOnlyList<MirrorPulseDirectoryTransferMember> Members,
    MirrorPulseNamespaceTransferJournalClaim Originals, DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    private const int DirectoryTransferAdmissionLimit = 8 * 1024 * 1024;
    private const string DirectoryTransferAdmissionTable = """
        CREATE TABLE namespace_directory_transfer_admissions(
            operation_id TEXT PRIMARY KEY NOT NULL,
            source_root_id TEXT NOT NULL,
            target_root_id TEXT NOT NULL CHECK(target_root_id<>source_root_id),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 8388608),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;
    private const string DirectoryTransferIdsTable = """
        CREATE TABLE namespace_directory_transfer_ids(
            operation_id TEXT PRIMARY KEY NOT NULL,
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_admissions(operation_id),
            journal_original INTEGER NOT NULL CHECK(journal_original IN (0,1)))
        """;
    private const string DirectoryTransferIdIndex = """
        CREATE INDEX namespace_directory_transfer_ids_by_transfer ON namespace_directory_transfer_ids(transfer_id,operation_id)
        """;
    private const string DirectoryTransferRootIndex = """
        CREATE UNIQUE INDEX namespace_directory_transfer_source_root ON namespace_directory_transfer_admissions(source_root_id)
        """;
    private const string DirectoryTransferTargetIndex = """
        CREATE UNIQUE INDEX namespace_directory_transfer_target_root ON namespace_directory_transfer_admissions(target_root_id)
        """;

    private static async Task EnsureDirectoryTransferAdmissionSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        foreach (var (name, definition) in new[]
        {
            ("namespace_directory_transfer_admissions", DirectoryTransferAdmissionTable),
            ("namespace_directory_transfer_ids", DirectoryTransferIdsTable),
            ("namespace_directory_transfer_copy_steps", DirectoryTransferCopyStepTable),
            ("namespace_directory_transfer_ids_by_transfer", DirectoryTransferIdIndex),
            ("namespace_directory_transfer_source_root", DirectoryTransferRootIndex),
            ("namespace_directory_transfer_target_root", DirectoryTransferTargetIndex)
        })
        {
            sql.Parameters.Clear();
            sql.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name;";
            sql.Parameters.AddWithValue("$name", name);
            var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null && originalVersion < 111)
            {
                sql.CommandText = definition;
                await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string text || NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(definition))
                throw new InvalidDataException($"The original directory transfer schema object {name} is missing or changed.");
        }
        if (originalVersion < 111)
        {
            sql.Parameters.Clear();
            sql.CommandText = """
                SELECT 1 FROM namespace_directory_transfer_admissions
                UNION ALL SELECT 1 FROM namespace_directory_transfer_ids
                UNION ALL SELECT 1 FROM namespace_directory_transfer_copy_steps LIMIT 1;
                """;
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply original directory transfer evidence.");
            sql.CommandText = "PRAGMA user_version=111;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task<MirrorPulseDirectoryTransferAdmission> PrepareNamespaceDirectoryTransferAsync(
        MirrorPulseDirectoryTransferAdmission original, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        int size = original.Members?.Count ?? 0;
        if (size is < 1 or > 65_536 || original.Originals?.Members is not { Count: <= 4096 })
            throw new ArgumentException("The directory transfer requires bounded original members and journal observations.", nameof(original));
        var plan = original with
        {
            Members = Array.AsReadOnly(original.Members!.ToArray()),
            Originals = SnapshotNamespaceTransferJournalClaim(original.Originals)
        };
        ValidateDirectoryTransferAdmission(plan);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(plan, TopologyJsonOptions);
        if (payload.Length > DirectoryTransferAdmissionLimit) throw new ArgumentException("The original directory transfer exceeds its payload limit.", nameof(original));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var replay = await ReadDirectoryTransferAdmissionCoreAsync(plan.Command.CommandId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(replay, TopologyJsonOptions)))
                    throw new InvalidOperationException("The original directory transfer, member inventory and copy inputs are immutable.");
                transaction.Commit();
                return replay;
            }

            var fenced = await ReadNamespaceRootFencesCoreAsync(true, transaction, token).ConfigureAwait(false);
            if (fenced.Contains(plan.SourceRoute.RootId) || fenced.Contains(plan.TargetRoute.RootId))
                throw new InvalidOperationException("Another original namespace owner prevents directory transfer admission.");
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await RequireCurrentDirectoryTransferTreeAsync(plan, transaction, token).ConfigureAwait(false);
            foreach (var (id, journalOriginal) in DirectoryTransferIds(plan))
            {
                try { await RequireNamespaceTransferIdAvailableAsync(id, transaction, token).ConfigureAwait(false); }
                catch (InvalidOperationException) when (journalOriginal) { throw new MirrorPulseTransferAdmissionBusyException(); }
            }

            await using var write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO namespace_directory_transfer_admissions(operation_id,source_root_id,target_root_id,payload,fingerprint)
                VALUES($id,$source,$target,$payload,$hash);
                """;
            write.Parameters.AddWithValue("$id", plan.Command.CommandId.ToString("D"));
            write.Parameters.AddWithValue("$source", plan.SourceRoute.RootId.ToString());
            write.Parameters.AddWithValue("$target", plan.TargetRoute.RootId.ToString());
            write.Parameters.AddWithValue("$payload", payload);
            write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            write.Parameters.Clear();
            write.CommandText = "INSERT INTO namespace_directory_transfer_ids(operation_id,transfer_id,journal_original) VALUES($id,$transfer,$journal);";
            var identifier = write.Parameters.Add("$id", SqliteType.Text);
            write.Parameters.AddWithValue("$transfer", plan.Command.CommandId.ToString("D"));
            var journal = write.Parameters.Add("$journal", SqliteType.Integer);
            foreach (var (id, journalOriginal) in DirectoryTransferIds(plan))
            {
                identifier.Value = id.ToString("D");
                journal.Value = journalOriginal ? 1 : 0;
                await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await InsertDirectoryTransferCopyStepsAsync(plan, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return plan;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferAdmission?> ReadNamespaceDirectoryTransferAsync(
        Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original transfer ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadDirectoryTransferAdmissionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseDirectoryTransferAdmission?> ReadDirectoryTransferAdmissionCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseDirectoryTransferAdmission plan;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT source_root_id,target_root_id,payload,fingerprint
                FROM namespace_directory_transfer_admissions WHERE operation_id=$id;
                """;
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            plan = ReadNamespaceMovePayload<MirrorPulseDirectoryTransferAdmission>((byte[])reader[2], (byte[])reader[3],
                DirectoryTransferAdmissionLimit, ValidateDirectoryTransferAdmission);
            if (plan.Command.CommandId != operationId || plan.SourceRoute.RootId.ToString() != reader.GetString(0) ||
                plan.TargetRoute.RootId.ToString() != reader.GetString(1))
                throw new InvalidDataException("The directory transfer's indexed routes disagree with its original admission.");
        }
        var ids = DirectoryTransferIds(plan).ToDictionary(value => value.Id, value => value.JournalOriginal);
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,journal_original FROM namespace_directory_transfer_ids WHERE transfer_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (!ids.Remove(ParseNamespaceMemberId(reader.GetString(0)), out bool original) || reader.GetInt64(1) != (original ? 1 : 0))
                    throw new InvalidDataException("The original directory transfer identifier index changed.");
        }
        if (ids.Count != 0) throw new InvalidDataException("The directory transfer lost an original effect or journal reservation.");
        foreach (var snapshot in plan.Members.Select(member => member.Source.Protection).Prepend(plan.SourceParent).Prepend(plan.TargetParent))
            await RequireRemoteUpdateHistoricalSnapshotAsync(snapshot, plan.PreparedAt, transaction, token).ConfigureAwait(false);
        return plan;
    }

    private sealed record DirectoryTransferFence(Guid OperationId, RootId Source, RootId Target);

    private async Task<IReadOnlyList<DirectoryTransferFence>> ReadDirectoryTransferFencesCoreAsync(
        SqliteTransaction transaction, CancellationToken token)
    {
        var fences = new List<DirectoryTransferFence>();
        var roots = new HashSet<RootId>();
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT operation_id,source_root_id,target_root_id,payload,fingerprint
            FROM namespace_directory_transfer_admissions ORDER BY operation_id LIMIT 4097;
            """;
        var originals = new Dictionary<Guid, (Guid Owner, bool Journal)>();
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (fences.Count == 4096) throw new InvalidDataException("The pending directory transfer inventory exceeds its recovery bound.");
                var plan = ReadNamespaceMovePayload<MirrorPulseDirectoryTransferAdmission>((byte[])reader[3], (byte[])reader[4],
                    DirectoryTransferAdmissionLimit, ValidateDirectoryTransferAdmission);
                RootId source = plan.SourceRoute.RootId, target = plan.TargetRoute.RootId;
                Guid owner = plan.Command.CommandId;
                if (reader.GetString(0) != owner.ToString("D") || reader.GetString(1) != source.ToString() ||
                    reader.GetString(2) != target.ToString() || !roots.Add(source) || !roots.Add(target))
                    throw new InvalidDataException("The pending directory transfer root fences disagree with their original admissions.");
                fences.Add(new(owner, source, target));
                foreach (var (id, journal) in DirectoryTransferIds(plan))
                    if (!originals.TryAdd(id, (owner, journal)))
                        throw new InvalidDataException("Two original directory transfers reserve the same identifier.");
            }
        }
        query.CommandText = "SELECT operation_id,transfer_id,journal_original FROM namespace_directory_transfer_ids;";
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (!originals.Remove(ParseNamespaceMemberId(reader.GetString(0)), out var original) ||
                    reader.GetString(1) != original.Owner.ToString("D") || reader.GetInt64(2) != (original.Journal ? 1 : 0))
                    throw new InvalidDataException("A directory transfer identifier lost its original owner.");
        if (originals.Count != 0) throw new InvalidDataException("A directory transfer lost its original reservations.");
        return fences;
    }

    private async Task RequireCurrentDirectoryTransferTreeAsync(MirrorPulseDirectoryTransferAdmission plan,
        SqliteTransaction transaction, CancellationToken token)
    {
        foreach (var parent in new[] { plan.SourceParent, plan.TargetParent })
            if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != parent)
                throw new InvalidOperationException("A directory transfer parent changed before admission.");
        var tree = await ReadNamespaceMemberTreeCoreAsync(plan.Command.SourceMemberId, transaction, token).ConfigureAwait(false);
        if (tree.Count != plan.Members.Count)
            throw new InvalidOperationException("The directory transfer source tree changed before admission.");
        for (int index = 0; index < tree.Count; index++)
        {
            var member = plan.Members[index].Source;
            var item = tree[index];
            if (member.Depth != item.Depth || member.Protection.Context.Member.MemberId != item.MemberId ||
                await ReadNamespaceMemberProtectionSnapshotCoreAsync(item.MemberId, transaction, token).ConfigureAwait(false) != member.Protection)
                throw new InvalidOperationException("A directory transfer member changed before admission.");
        }
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM namespace_member_names WHERE parent_member_id=$parent AND child_name_key=$name;";
        query.Parameters.AddWithValue("$parent", plan.Command.TargetParentMemberId.ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(plan.Members[0].TargetRelativePath));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An existing member owns the directory transfer destination.");
    }

    private static IEnumerable<(Guid Id, bool JournalOriginal)> DirectoryTransferIds(MirrorPulseDirectoryTransferAdmission plan)
    {
        yield return (plan.Command.CommandId, false);
        foreach (var member in plan.Members)
        {
            yield return (member.CopyOperationId, false);
            yield return (member.DeleteOperationId, false);
            if (member.PrivateContentId is { } content) yield return (content, false);
        }
        foreach (var original in plan.Originals.Members) yield return (original.OperationId, true);
    }

    private static void ValidateDirectoryTransferAdmission(MirrorPulseDirectoryTransferAdmission plan)
    {
        if (plan.Version != 1 || plan.Command is not { Version: 1 } command || command.CommandId == Guid.Empty ||
            command.RequestedAt == default || command.RequestedAt > plan.PreparedAt ||
            string.IsNullOrWhiteSpace(command.TargetName) || command.TargetName.Contains('/') ||
            plan.SourceRoute is null || plan.TargetRoute is null || plan.SourceRoute.InstanceId == plan.TargetRoute.InstanceId ||
            plan.SourceRoute.RootId == plan.TargetRoute.RootId || plan.SourceRoute.ProtocolVersion != 2 || plan.TargetRoute.ProtocolVersion != 2 ||
            plan.Members is not { Count: >= 1 and <= 65_536 } ||
            plan.Originals is not { Version: 1, ThroughSequence: >= 0, Members.Count: <= 4096 } originals ||
            originals.OperationId != command.CommandId || originals.CapturedAt < plan.PreparedAt)
            throw new ArgumentException("Directory transfer admission requires one original command and distinct V2 instances.");
        if (plan.SourceParent?.Context.Member is not { IsDirectory: true } sourceParent ||
            plan.TargetParent?.Context.Member is not { IsDirectory: true } targetParent ||
            sourceParent.RootId != plan.SourceRoute.RootId || targetParent.RootId != plan.TargetRoute.RootId ||
            sourceParent.LocalObject.VolumeSerialNumber != targetParent.LocalObject.VolumeSerialNumber ||
            sourceParent.LocalObject.SyncRootFileId != targetParent.LocalObject.SyncRootFileId ||
            targetParent.OwnerSid != sourceParent.OwnerSid ||
            plan.TargetParent.Context.Protection.RoleSid != plan.SourceParent.Context.Protection.RoleSid ||
            command.TargetParentMemberId != targetParent.MemberId)
            throw new ArgumentException("Directory transfer admission requires both original managed parents.");

        string targetRootPath = plan.TargetParent.Context.Location.RelativePath + "/" + command.TargetName;
        string? sourceRootPath = null;
        var members = new Dictionary<Guid, MirrorPulseDirectoryTransferMember>();
        var publicItems = new Dictionary<Guid, MirrorPulseDirectoryTransferMember>();
        var bindings = new HashSet<MirrorPulseLocalFileBinding>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Members)
        {
            if (entry?.Source?.Protection is not { } protection || protection.Context is not { } context ||
                protection.Identity is not { ItemId: { } itemId, RemoteId: not null } identity ||
                itemId == Guid.Empty || identity.MemberId != context.Member.MemberId ||
                context.Member.RootId != plan.SourceRoute.RootId || context.Member.OwnerSid != sourceParent.OwnerSid ||
                context.Member.LocalObject.VolumeSerialNumber != sourceParent.LocalObject.VolumeSerialNumber ||
                context.Member.LocalObject.SyncRootFileId != sourceParent.LocalObject.SyncRootFileId ||
                context.Protection.RoleSid != plan.SourceParent.Context.Protection.RoleSid ||
                context.Member.RegisteredAt > plan.PreparedAt || context.Verification.ObservedAt > plan.PreparedAt ||
                context.PositionVerification is { } position && position.ObservedAt > plan.PreparedAt ||
                (entry.SourceRevision is not null && InvalidRemoteUpdateText(entry.SourceRevision, 4096)) ||
                !members.TryAdd(context.Member.MemberId, entry) || !publicItems.TryAdd(itemId, entry) ||
                !bindings.Add(context.Member.LocalObject) || !paths.Add(context.Location.RelativePath))
                throw new ArgumentException("Every directory transfer member must retain its exact original identity, revision, position and protection.");

            string path = context.Location.RelativePath;
            if (sourceRootPath is null)
            {
                if (entry.Source.Depth != 0 || !context.Member.IsDirectory || context.Member.MemberId != command.SourceMemberId ||
                    context.Location.ParentMemberId != sourceParent.MemberId || ParentMovePath(path) != plan.SourceParent.Context.Location.RelativePath ||
                    !path.StartsWith(plan.SourceRoute.DirectoryName + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Directory transfer must start below its original managed root.");
                sourceRootPath = path;
            }
            else if (entry.Source.Depth < 1 || !path.StartsWith(sourceRootPath + "/", StringComparison.Ordinal) ||
                context.Location.ParentMemberId is not { } parentId || !members.TryGetValue(parentId, out var parent) ||
                !parent.Source.Protection.Context.Member.IsDirectory || entry.Source.Depth != parent.Source.Depth + 1 ||
                ParentMovePath(path) != parent.Source.Protection.Context.Location.RelativePath)
                throw new ArgumentException("Directory transfer members must retain their original parents before their children.");

            ValidatePermissionRelativePath(entry.TargetRelativePath);
            if (entry.TargetRelativePath != targetRootPath + path[sourceRootPath.Length..] ||
                !entry.TargetRelativePath.StartsWith(plan.TargetRoute.DirectoryName + "/", StringComparison.Ordinal))
                throw new ArgumentException("A directory transfer member does not map to its exact target path.");
            if (context.Member.IsDirectory
                ? entry.PrivateContentId is not null || entry.Content is not null
                : entry.PrivateContentId is null || entry.Content is not { Length: >= 0, Sha256.Length: 64 } content ||
                    content.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
                throw new ArgumentException("Every file requires complete retained content and every directory requires exact membership.");
        }
        long previous = 0;
        foreach (var original in originals.Members)
        {
            if (original is null || original.ItemId is not { } item || !publicItems.TryGetValue(item, out var member) ||
                original.Sequence <= previous || original.Sequence > originals.ThroughSequence ||
                original.ObservedAt == default || original.ObservedAt > originals.CapturedAt || original.PreviousRelativePath is not null ||
                original.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
                original.IsDirectory != member.Source.Protection.Context.Member.IsDirectory ||
                original.IsDirectory && original.Kind != MirrorPulseWorkerChangeKind.Create ||
                original.RelativePath?.Replace('\\', '/') != member.Source.Protection.Context.Location.RelativePath)
                throw new ArgumentException("The directory transfer journal must bind exact original scope, sequence and members.");
            previous = original.Sequence;
        }
        var ids = new HashSet<Guid>();
        if (DirectoryTransferIds(plan).Any(value => value.Id == Guid.Empty || !ids.Add(value.Id)))
            throw new ArgumentException("The original directory transfer identifiers must be distinct and nonempty.");
    }
}
