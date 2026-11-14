using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferRetirement(Guid TransferId, int Ordinal, Guid EffectId,
    Guid MemberId, MirrorPulseNamespaceTransferSourceProjection Projection, DateTimeOffset StartedAt,
    MirrorPulseNamespacePermissionVerification? Parent = null, DateTimeOffset? RetiredAt = null, long? NameEpoch = null);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferNativeRetirementTable = """
        CREATE TABLE namespace_directory_transfer_retirements(
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_source_deletions(transfer_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 65535),
            effect_id TEXT NOT NULL UNIQUE REFERENCES namespace_directory_transfer_ids(operation_id),
            member_id TEXT NOT NULL UNIQUE REFERENCES namespace_members(member_id),
            completed INTEGER NOT NULL CHECK(completed IN (0,1)),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 131072),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
            PRIMARY KEY(transfer_id,ordinal))
        """;

    private static async Task EnsureDirectoryTransferRetirementSchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE name='namespace_directory_transfer_retirements';";
        var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null && version < 114)
        {
            sql.CommandText = DirectoryTransferNativeRetirementTable;
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string text ||
            NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(DirectoryTransferNativeRetirementTable))
        {
            throw new InvalidDataException("The original directory transfer native retirement schema is missing or changed.");
        }
        if (version < 114)
        {
            sql.CommandText = "SELECT 1 FROM namespace_directory_transfer_retirements LIMIT 1;";
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply original directory native retirement evidence.");
            sql.CommandText = "PRAGMA user_version=114;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task<IReadOnlyList<MirrorPulseDirectoryTransferRetirement>> ReadNamespaceDirectoryTransferRetirementsAsync(
        Guid transferId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return rows;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferRetirement> StartNamespaceDirectoryTransferRetirementAsync(
        Guid transferId, int ordinal, MirrorPulseNamespaceTransferSourceProjection projection,
        MirrorPulseDirectoryTransferTargetVerification fresh, DateTimeOffset startedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var (plan, verified) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
            var deletion = await ReadDirectoryTransferSourceDeletionCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            if (deletion?.AcceptedAt is not { } accepted || fresh.OperationId != transferId ||
                fresh.Version != verified.Version || !fresh.Members.SequenceEqual(verified.Members) ||
                fresh.VerifiedAt < accepted || startedAt < fresh.VerifiedAt)
                throw new InvalidOperationException("Native retirement requires accepted source deletion and current whole target verification.");
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            if (ordinal < 0 || ordinal >= plan.Members.Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
            var member = plan.Members[ordinal];
            var row = new MirrorPulseDirectoryTransferRetirement(transferId, ordinal, member.DeleteOperationId,
                member.Source.Protection.Context.Member.MemberId, projection, startedAt);
            ValidateDirectoryTransferRetirement(plan, accepted, row);
            var original = rows.SingleOrDefault(value => value.Ordinal == ordinal);
            if (original is not null)
            {
                if (original.Projection != projection with { CapturedAt = original.Projection.CapturedAt })
                    throw new InvalidOperationException("The original native source projection is immutable.");
                transaction.Commit();
                return original;
            }
            if (ordinal != plan.Members.Count - 1 - rows.Count || rows.Any(value => value.RetiredAt is null))
                throw new InvalidOperationException("Directory transfer retires source members in their original leaf-first order.");
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await RequireCurrentDirectoryTransferPublicationAsync(plan, verified, transaction, requireUnretiredSource: rows.Count == 0, token)
                .ConfigureAwait(false);
            await RequireCurrentDirectoryTransferRetirementMemberAsync(plan, ordinal, transaction, token).ConfigureAwait(false);
            validation.Dispose();
            await WriteDirectoryTransferRetirementAsync(row, true, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return row;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferRetirement> CompleteNamespaceDirectoryTransferRetirementAsync(
        Guid transferId, int ordinal, MirrorPulseNamespacePermissionVerification parent, DateTimeOffset retiredAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var (plan, _) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            var row = rows.SingleOrDefault(value => value.Ordinal == ordinal)
                ?? throw new InvalidOperationException("A native retirement receipt requires its original start.");
            if (row.RetiredAt is not null) { transaction.Commit(); return row; }
            var deletion = await ReadDirectoryTransferSourceDeletionCoreAsync(transferId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Native retirement lost the accepted original source deletion.");
            var source = await RequireCurrentDirectoryTransferRetirementMemberAsync(plan, ordinal, transaction, token).ConfigureAwait(false);
            Guid parentId = source.Context.Location.ParentMemberId!.Value;
            long epoch = await ReadNamespaceNameEpochCoreAsync(parentId, source.Context.Location.RelativePath, transaction, token).ConfigureAwait(false);
            var completed = row with { Parent = parent, RetiredAt = retiredAt, NameEpoch = checked(epoch + 1) };
            ValidateDirectoryTransferRetirement(plan, deletion.AcceptedAt!.Value, completed);
            validation.Dispose();
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
            sql.Parameters.AddWithValue("$member", row.MemberId.ToString("D"));
            if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1 ||
                await ReadNamespaceNameEpochCoreAsync(parentId, source.Context.Location.RelativePath, transaction, token).ConfigureAwait(false) != completed.NameEpoch)
                throw new InvalidDataException("The retired directory transfer member lost its original name epoch.");
            await WriteDirectoryTransferRetirementAsync(completed, false, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> RequireCurrentDirectoryTransferRetirementMemberAsync(
        MirrorPulseDirectoryTransferAdmission plan, int ordinal, SqliteTransaction transaction, CancellationToken token)
    {
        var original = plan.Members[ordinal].Source.Protection;
        var parent = DirectoryTransferSourceParent(plan, ordinal);
        if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(original.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != original ||
            await ReadNamespaceMemberProtectionSnapshotCoreAsync(parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != parent)
            throw new InvalidOperationException("The original local retirement member or direct parent changed.");
        await RequireNamespaceMemberNameAsync(original.Context.Member, transaction, token).ConfigureAwait(false);
        await RequireNoRegisteredDescendantsAsync(original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
        return original;
    }

    internal static MirrorPulseNamespaceMemberProtectionSnapshot DirectoryTransferSourceParent(MirrorPulseDirectoryTransferAdmission plan, int ordinal) =>
        ordinal == 0 ? plan.SourceParent : plan.Members.First(member => member.Source.Protection.Context.Member.MemberId ==
            plan.Members[ordinal].Source.Protection.Context.Location.ParentMemberId).Source.Protection;

    private async Task<IReadOnlyList<MirrorPulseDirectoryTransferRetirement>> ReadDirectoryTransferRetirementsCoreAsync(
        Guid transferId, SqliteTransaction transaction, CancellationToken token)
    {
        var deletion = await ReadDirectoryTransferSourceDeletionCoreAsync(transferId, transaction, token).ConfigureAwait(false);
        if (deletion?.AcceptedAt is not { } accepted) return [];
        var (plan, _) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = """
            SELECT ordinal,effect_id,member_id,completed,payload,fingerprint
            FROM namespace_directory_transfer_retirements WHERE transfer_id=$id ORDER BY ordinal DESC;
            """;
        sql.Parameters.AddWithValue("$id", transferId.ToString("D"));
        var rows = new List<MirrorPulseDirectoryTransferRetirement>();
        await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var row = ReadNamespaceMovePayload<MirrorPulseDirectoryTransferRetirement>((byte[])reader[4], (byte[])reader[5], 131_072,
                value => ValidateDirectoryTransferRetirement(plan, accepted, value));
            if (row.Ordinal != plan.Members.Count - 1 - rows.Count || row.Ordinal != reader.GetInt64(0) ||
                row.EffectId.ToString("D") != reader.GetString(1) || row.MemberId.ToString("D") != reader.GetString(2) ||
                (row.RetiredAt is null ? 0 : 1) != reader.GetInt64(3) ||
                rows.Count > 0 && (rows[^1].RetiredAt is not { } prior || row.StartedAt < prior))
                throw new InvalidDataException("The original native retirement order or index changed.");
            rows.Add(row);
        }
        return rows.AsReadOnly();
    }

    private static void ValidateDirectoryTransferRetirement(MirrorPulseDirectoryTransferAdmission plan, DateTimeOffset accepted,
        MirrorPulseDirectoryTransferRetirement row)
    {
        if (row.TransferId != plan.Command.CommandId || row.Ordinal < 0 || row.Ordinal >= plan.Members.Count || row.Projection is null)
            throw new InvalidDataException("Native retirement lost its original transferred member.");
        var member = plan.Members[row.Ordinal];
        var original = member.Source.Protection;
        var projection = row.Projection;
        if (row.MemberId != original.Context.Member.MemberId || row.EffectId != member.DeleteOperationId ||
            projection.Version != 1 || projection.OperationId != row.EffectId || projection.ItemId != original.Identity.ItemId ||
            projection.RemoteId != original.Identity.RemoteId || projection.RemoteRevision != member.SourceRevision ||
            projection.RelativePath?.Replace('\\', '/') != original.Context.Location.RelativePath ||
            projection.LocalObject != original.Context.Member.LocalObject ||
            projection.CapturedAt < accepted || row.StartedAt < projection.CapturedAt ||
            row.RetiredAt < row.StartedAt ||
            (row.RetiredAt is null) != (row.Parent is null) || (row.RetiredAt is null) != (row.NameEpoch is null))
            throw new InvalidDataException("Native retirement lost its original SDK projection or chronology.");
        if (row.Parent is { } parent)
        {
            var originalParent = DirectoryTransferSourceParent(plan, row.Ordinal);
            if (row.NameEpoch is < 2 || parent.ObservedAt < row.StartedAt || parent.ObservedAt > row.RetiredAt ||
                parent.LocalObject != originalParent.Context.Member.LocalObject || parent.OwnerSid != originalParent.Context.Member.OwnerSid ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(originalParent.ExpectedDacl, parent.Dacl))
                throw new InvalidDataException("Native retirement lost its original direct parent and settled name.");
        }
    }

    private async Task WriteDirectoryTransferRetirementAsync(MirrorPulseDirectoryTransferRetirement row, bool insert,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(row, TopologyJsonOptions);
        if (payload.Length > 131_072) throw new ArgumentException("The original directory native retirement must be bounded.", nameof(row));
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = insert ? """
            INSERT INTO namespace_directory_transfer_retirements(transfer_id,ordinal,effect_id,member_id,completed,payload,fingerprint)
            VALUES($id,$ordinal,$effect,$member,$completed,$payload,$hash);
            """ : """
            UPDATE namespace_directory_transfer_retirements SET completed=$completed,payload=$payload,fingerprint=$hash
            WHERE transfer_id=$id AND ordinal=$ordinal AND effect_id=$effect AND member_id=$member AND completed=0;
            """;
        sql.Parameters.AddWithValue("$id", row.TransferId.ToString("D"));
        sql.Parameters.AddWithValue("$ordinal", row.Ordinal);
        sql.Parameters.AddWithValue("$effect", row.EffectId.ToString("D"));
        sql.Parameters.AddWithValue("$member", row.MemberId.ToString("D"));
        sql.Parameters.AddWithValue("$completed", row.RetiredAt is null ? 0 : 1);
        sql.Parameters.AddWithValue("$payload", payload);
        sql.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The original directory transfer native retirement could not be retained.");
    }
}
