using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferAcknowledgement(Guid TransferId,
    MirrorPulseDirectoryTransferTargetVerification Target, string RetirementsSha256, DateTimeOffset StartedAt,
    DateTimeOffset? AcknowledgedAt = null);

internal sealed record MirrorPulseRetiredDirectoryTransferSource(MirrorPulseDirectoryTransferAdmission Admission,
    MirrorPulseDirectoryTransferRetirement Retirement, DateTimeOffset? AcknowledgedAt);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferFenceTable = """
        CREATE TABLE namespace_directory_transfer_fences(
            root_id TEXT NOT NULL PRIMARY KEY,
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_admissions(operation_id))
        """;
    private const string DirectoryTransferAcknowledgementTable = """
        CREATE TABLE namespace_directory_transfer_acknowledgements(
            transfer_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_directory_transfer_admissions(operation_id),
            acknowledged INTEGER NOT NULL CHECK(acknowledged IN (0,1)),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 8388608),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;
    private const string DirectoryTransferFenceByTransfer = """
        CREATE INDEX namespace_directory_transfer_fences_by_transfer ON namespace_directory_transfer_fences(transfer_id)
        """;
    private const string DirectoryTransferRetiredItemIndex = """
        CREATE INDEX namespace_directory_transfer_retired_item ON namespace_directory_transfer_retirements(
            json_extract(CAST(payload AS TEXT),'$.Projection.ItemId'))
        """;
    private const string DirectoryTransferLateAcknowledgementTable = """
        CREATE TABLE namespace_directory_transfer_late_acknowledgements(
            operation_id TEXT NOT NULL PRIMARY KEY,
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_acknowledgements(transfer_id),
            source_root_id TEXT NOT NULL,
            acknowledged INTEGER NOT NULL CHECK(acknowledged IN (0,1)),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 131072),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;
    private const string DirectoryTransferLateAcknowledgementIndex = """
        CREATE INDEX namespace_directory_transfer_late_pending ON namespace_directory_transfer_late_acknowledgements(source_root_id,operation_id)
        WHERE acknowledged=0
        """;
    private const string HistoricalDirectorySourceIndex = """
        CREATE INDEX namespace_directory_transfer_source_root ON namespace_directory_transfer_admissions(source_root_id)
        """;
    private const string HistoricalDirectoryTargetIndex = """
        CREATE INDEX namespace_directory_transfer_target_root ON namespace_directory_transfer_admissions(target_root_id)
        """;

    private static async Task EnsureDirectoryTransferSettlementSchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        foreach (var (name, definition, oldDefinition) in new (string, string, string?)[]
        {
            ("namespace_directory_transfer_fences", DirectoryTransferFenceTable, null),
            ("namespace_directory_transfer_acknowledgements", DirectoryTransferAcknowledgementTable, null),
            ("namespace_directory_transfer_fences_by_transfer", DirectoryTransferFenceByTransfer, null),
            ("namespace_directory_transfer_retired_item", DirectoryTransferRetiredItemIndex, null),
            ("namespace_directory_transfer_late_acknowledgements", DirectoryTransferLateAcknowledgementTable, null),
            ("namespace_directory_transfer_late_pending", DirectoryTransferLateAcknowledgementIndex, null),
            ("namespace_directory_transfer_source_root", HistoricalDirectorySourceIndex, DirectoryTransferRootIndex),
            ("namespace_directory_transfer_target_root", HistoricalDirectoryTargetIndex, DirectoryTransferTargetIndex)
        })
        {
            sql.Parameters.Clear();
            sql.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name;";
            sql.Parameters.AddWithValue("$name", name);
            var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (version < 115 && oldDefinition is not null && existing is not null &&
                NormalizeRetiredTransferIndexSql(existing) == NormalizeRetiredTransferIndexSql(oldDefinition))
            {
                sql.CommandText = $"DROP INDEX {name};";
                await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                existing = null;
            }
            if (existing is null && version < 115)
            {
                sql.CommandText = definition;
                await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is null || NormalizeRetiredTransferIndexSql(existing) != NormalizeRetiredTransferIndexSql(definition))
                throw new InvalidDataException($"The original directory transfer settlement schema object {name} is missing or changed.");
        }
        if (version < 115)
        {
            sql.CommandText = """
                SELECT 1 FROM namespace_directory_transfer_fences
                UNION ALL SELECT 1 FROM namespace_directory_transfer_acknowledgements
                UNION ALL SELECT 1 FROM namespace_directory_transfer_late_acknowledgements LIMIT 1;
                """;
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply directory transfer settlement evidence.");
            sql.CommandText = """
                INSERT INTO namespace_directory_transfer_fences(root_id,transfer_id)
                SELECT source_root_id,operation_id FROM namespace_directory_transfer_admissions
                UNION ALL SELECT target_root_id,operation_id FROM namespace_directory_transfer_admissions;
                PRAGMA user_version=115;
                """;
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task RequireDirectoryTransferFenceIndexAsync(SqliteTransaction transaction, CancellationToken token, RootId? root = null)
    {
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = """
            SELECT a.operation_id
            FROM namespace_directory_transfer_admissions a
            LEFT JOIN namespace_directory_transfer_acknowledgements k ON k.transfer_id=a.operation_id
            WHERE ($root IS NULL OR a.source_root_id=$root OR a.target_root_id=$root)
                AND ((COALESCE(k.acknowledged,0)=0 AND
                    (NOT EXISTS (SELECT 1 FROM namespace_directory_transfer_fences f WHERE f.root_id=a.source_root_id AND f.transfer_id=a.operation_id)
                     OR NOT EXISTS (SELECT 1 FROM namespace_directory_transfer_fences f WHERE f.root_id=a.target_root_id AND f.transfer_id=a.operation_id)
                     OR (SELECT COUNT(*) FROM namespace_directory_transfer_fences f WHERE f.transfer_id=a.operation_id)<>2))
                    OR (k.acknowledged=1 AND EXISTS (SELECT 1 FROM namespace_directory_transfer_fences f WHERE f.transfer_id=a.operation_id))
                    OR (k.transfer_id IS NOT NULL AND k.acknowledged<>
                        CASE WHEN json_type(CAST(k.payload AS TEXT),'$.AcknowledgedAt')='text' THEN 1 ELSE 0 END))
            UNION ALL
            SELECT f.transfer_id FROM namespace_directory_transfer_fences f
            LEFT JOIN namespace_directory_transfer_admissions a ON a.operation_id=f.transfer_id
            WHERE ($root IS NULL OR f.root_id=$root) AND (a.operation_id IS NULL OR f.root_id NOT IN (a.source_root_id,a.target_root_id))
            LIMIT 1;
            """;
        sql.Parameters.AddWithValue("$root", (object?)root?.ToString() ?? DBNull.Value);
        if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("The directory transfer root fences disagree with original acknowledgement state.");
    }

    internal async Task<bool> HasDirectoryTransferRetiredItemAsync(Guid itemId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var sql = _connection.CreateCommand();
            sql.CommandText = """
                SELECT 1 FROM namespace_directory_transfer_retirements
                WHERE json_extract(CAST(payload AS TEXT),'$.Projection.ItemId')=$item LIMIT 1;
                """;
            sql.Parameters.AddWithValue("$item", itemId.ToString("D"));
            return await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseRetiredDirectoryTransferSource?> ReadRetiredDirectoryTransferSourceAsync(Guid itemId,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            Guid? transferId;
            await using (var sql = _connection.CreateCommand())
            {
                sql.Transaction = transaction;
                sql.CommandText = """
                    SELECT transfer_id FROM namespace_directory_transfer_retirements
                    WHERE json_extract(CAST(payload AS TEXT),'$.Projection.ItemId')=$item LIMIT 2;
                    """;
                sql.Parameters.AddWithValue("$item", itemId.ToString("D"));
                await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
                transferId = await reader.ReadAsync(token).ConfigureAwait(false) ? ParseNamespaceMemberId(reader.GetString(0)) : null;
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw new InvalidDataException("The original public item has competing directory transfer retirements.");
            }
            if (transferId is not { } id) { transaction.Commit(); return null; }
            var (plan, _) = await ReadDirectoryTransferBirthEvidenceAsync(id, transaction, token).ConfigureAwait(false);
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(id, transaction, token).ConfigureAwait(false);
            var row = rows.SingleOrDefault(value => value.Projection.ItemId == itemId)
                ?? throw new InvalidDataException("The original directory retirement item no longer matches its index.");
            var completion = await ReadDirectoryTransferAcknowledgementCoreAsync(id, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return new(plan, row, completion?.AcknowledgedAt);
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<Guid>> ReadStartedDirectoryTransferAcknowledgementsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            await RequireDirectoryTransferFenceIndexAsync(transaction, token).ConfigureAwait(false);
            await using var query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT a.transfer_id FROM namespace_directory_transfer_acknowledgements a
                JOIN namespace_directory_transfer_fences f ON f.transfer_id=a.transfer_id
                WHERE a.acknowledged=0 GROUP BY a.transfer_id ORDER BY a.transfer_id LIMIT 2049;
                """;
            var pending = new List<Guid>();
            await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) pending.Add(ParseNamespaceMemberId(reader.GetString(0)));
            if (pending.Count > 2048) throw new InvalidDataException("Pending directory acknowledgements exceed the Host root bound.");
            transaction.Commit();
            return pending.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferAcknowledgement?> ReadNamespaceDirectoryTransferAcknowledgementAsync(
        Guid transferId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadDirectoryTransferAcknowledgementCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferAcknowledgement> StartNamespaceDirectoryTransferAcknowledgementAsync(Guid transferId,
        MirrorPulseDirectoryTransferTargetVerification fresh, DateTimeOffset startedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            if (await ReadDirectoryTransferAcknowledgementCoreAsync(transferId, transaction, token).ConfigureAwait(false) is { } prior)
            {
                transaction.Commit();
                return prior;
            }
            var (plan, verified) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            var result = new MirrorPulseDirectoryTransferAcknowledgement(transferId, fresh, DirectoryTransferRetirementHash(rows), startedAt);
            ValidateDirectoryTransferAcknowledgement(plan, verified, rows, result);
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token, requireSingleRoot: true)
                .ConfigureAwait(false);
            await RequireCurrentDirectoryTransferPublicationAsync(plan, verified, transaction, requireUnretiredSource: false, token)
                .ConfigureAwait(false);
            await RequireDirectoryTransferSettledSourceNamesAsync(plan, rows, transaction, token).ConfigureAwait(false);
            validation.Dispose();
            await WriteDirectoryTransferAcknowledgementAsync(result, true, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferAcknowledgement> CompleteNamespaceDirectoryTransferAcknowledgementAsync(
        MirrorPulseDirectoryTransferAcknowledgement start, DateTimeOffset acknowledgedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var prior = await ReadDirectoryTransferAcknowledgementCoreAsync(start.TransferId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The directory transfer lost its first acknowledgement invocation.");
            if (start.AcknowledgedAt is not null || !JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(prior with { AcknowledgedAt = null }, TopologyJsonOptions)))
                throw new InvalidOperationException("The SDK acknowledgement must complete its original directory transfer start.");
            if (prior.AcknowledgedAt is not null) { transaction.Commit(); return prior; }
            if (acknowledgedAt < prior.StartedAt) throw new ArgumentException("Acknowledgement cannot predate its original invocation.");
            var (plan, _) = await ReadDirectoryTransferBirthEvidenceAsync(start.TransferId, transaction, token).ConfigureAwait(false);
            var rows = await ReadDirectoryTransferRetirementsCoreAsync(start.TransferId, transaction, token).ConfigureAwait(false);
            await RequireDirectoryTransferSettledSourceNamesAsync(plan, rows, transaction, token).ConfigureAwait(false);
            var result = prior with { AcknowledgedAt = acknowledgedAt };
            validation.Dispose();
            await WriteDirectoryTransferAcknowledgementAsync(result, false, transaction, token).ConfigureAwait(false);
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = "DELETE FROM namespace_directory_transfer_fences WHERE transfer_id=$id AND root_id IN ($source,$target);";
            sql.Parameters.AddWithValue("$id", start.TransferId.ToString("D"));
            sql.Parameters.AddWithValue("$source", plan.SourceRoute.RootId.ToString());
            sql.Parameters.AddWithValue("$target", plan.TargetRoute.RootId.ToString());
            if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2)
                throw new InvalidDataException("Acknowledgement cannot release the original two directory transfer fences.");
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireDirectoryTransferSettledSourceNamesAsync(MirrorPulseDirectoryTransferAdmission plan,
        IReadOnlyList<MirrorPulseDirectoryTransferRetirement> rows, SqliteTransaction transaction, CancellationToken token,
        Guid? replacementRootMemberId = null)
    {
        if (replacementRootMemberId is { } replacementId)
        {
            var original = plan.Members[0].Source.Protection;
            var replacement = await ReadNamespaceMemberProtectionSnapshotCoreAsync(replacementId, transaction, token).ConfigureAwait(false);
            if (replacement is null || replacement.Context.Member.MemberId == original.Context.Member.MemberId ||
                replacement.Context.Member.LocalObject == original.Context.Member.LocalObject ||
                replacement.Context.Member.RootId != plan.SourceRoute.RootId ||
                replacement.Context.Location.ParentMemberId != plan.SourceParent.Context.Member.MemberId ||
                replacement.Context.Location.RelativePath != original.Context.Location.RelativePath ||
                replacement.Identity.ItemId is null || rows.Any(row => row.Projection.ItemId == replacement.Identity.ItemId))
                throw new InvalidOperationException("The source replacement requires its own registered binding and public identity.");
            await RequireNamespaceMemberNameAsync(replacement.Context.Member, transaction, token).ConfigureAwait(false);
        }
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT member_id FROM namespace_member_names WHERE parent_member_id=$parent AND child_name_key=$name;";
        var parent = sql.Parameters.Add("$parent", SqliteType.Text);
        var name = sql.Parameters.Add("$name", SqliteType.Text);
        foreach (var row in rows)
        {
            var source = plan.Members[row.Ordinal].Source.Protection.Context.Location;
            Guid parentId = source.ParentMemberId!.Value;
            parent.Value = parentId.ToString("D");
            name.Value = NamespaceMemberNameKey(source.RelativePath);
            bool replaced = row.Ordinal == 0 && replacementRootMemberId is not null;
            string? nameOwner = (await sql.ExecuteScalarAsync(token).ConfigureAwait(false)) as string;
            long? expectedEpoch = replaced ? checked(row.NameEpoch + 1) : row.NameEpoch;
            if (nameOwner != (replaced ? replacementRootMemberId?.ToString("D") : null) ||
                await ReadNamespaceNameEpochCoreAsync(parentId, source.RelativePath, transaction, token).ConfigureAwait(false) != expectedEpoch)
                throw new InvalidOperationException("An original retired source name changed before directory transfer acknowledgement.");
        }
    }

    private async Task<MirrorPulseDirectoryTransferAcknowledgement?> ReadDirectoryTransferAcknowledgementCoreAsync(Guid transferId,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload, hash;
        long indexed;
        await using (var sql = _connection.CreateCommand())
        {
            sql.Transaction = transaction;
            sql.CommandText = "SELECT acknowledged,payload,fingerprint FROM namespace_directory_transfer_acknowledgements WHERE transfer_id=$id;";
            sql.Parameters.AddWithValue("$id", transferId.ToString("D"));
            await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            indexed = reader.GetInt64(0);
            payload = (byte[])reader[1];
            hash = (byte[])reader[2];
        }
        var (plan, verified) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
        var rows = await ReadDirectoryTransferRetirementsCoreAsync(transferId, transaction, token).ConfigureAwait(false);
        return ReadNamespaceMovePayload<MirrorPulseDirectoryTransferAcknowledgement>(payload, hash, DirectoryTransferAdmissionLimit, value =>
        {
            ValidateDirectoryTransferAcknowledgement(plan, verified, rows, value);
            if (indexed != (value.AcknowledgedAt is null ? 0 : 1))
                throw new InvalidDataException("The directory transfer acknowledgement index changed.");
        });
    }

    private static string DirectoryTransferRetirementHash(IReadOnlyList<MirrorPulseDirectoryTransferRetirement> rows) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows, TopologyJsonOptions)));

    private static void ValidateDirectoryTransferAcknowledgement(MirrorPulseDirectoryTransferAdmission plan,
        MirrorPulseDirectoryTransferTargetVerification verified, IReadOnlyList<MirrorPulseDirectoryTransferRetirement> rows,
        MirrorPulseDirectoryTransferAcknowledgement value)
    {
        if (value.TransferId != plan.Command.CommandId || rows.Count != plan.Members.Count || value.Target is null ||
            value.Target.OperationId != value.TransferId || value.Target.Version != verified.Version || value.Target.Members is null ||
            !value.Target.Members.SequenceEqual(verified.Members) ||
            rows.Any(row => row.RetiredAt is null || row.RetiredAt > value.Target.VerifiedAt) ||
            value.Target.VerifiedAt < verified.VerifiedAt || value.StartedAt < value.Target.VerifiedAt ||
            value.AcknowledgedAt < value.StartedAt || value.RetirementsSha256 != DirectoryTransferRetirementHash(rows))
            throw new InvalidOperationException("Directory transfer acknowledgement requires all original native receipts and fresh whole-target verification.");
    }

    private async Task WriteDirectoryTransferAcknowledgementAsync(MirrorPulseDirectoryTransferAcknowledgement value, bool insert,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > DirectoryTransferAdmissionLimit)
            throw new ArgumentException("The directory transfer acknowledgement is not bounded.");
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = insert
            ? "INSERT INTO namespace_directory_transfer_acknowledgements(transfer_id,acknowledged,payload,fingerprint) VALUES($id,$ack,$payload,$hash);"
            : "UPDATE namespace_directory_transfer_acknowledgements SET acknowledged=$ack,payload=$payload,fingerprint=$hash WHERE transfer_id=$id AND acknowledged=0;";
        sql.Parameters.AddWithValue("$id", value.TransferId.ToString("D"));
        sql.Parameters.AddWithValue("$ack", value.AcknowledgedAt is null ? 0 : 1);
        sql.Parameters.AddWithValue("$payload", payload);
        sql.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The directory transfer could not retain acknowledgement evidence.");
    }
}
