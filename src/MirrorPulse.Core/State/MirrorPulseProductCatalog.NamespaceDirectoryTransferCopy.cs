using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferCopyStep(Guid EffectId, DateTimeOffset? StartedAt,
    string? AcceptedRevision, DateTimeOffset? AcceptedAt, bool Durable = false);

internal sealed record MirrorPulseDirectoryTransferCopy(MirrorPulseDirectoryTransferAdmission Admission,
    IReadOnlyList<MirrorPulseDirectoryTransferCopyStep> Steps);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferCopyStepTable = """
        CREATE TABLE namespace_directory_transfer_copy_steps(
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_admissions(operation_id),
            ordinal INTEGER NOT NULL CHECK(ordinal>=0),
            effect_id TEXT NOT NULL UNIQUE REFERENCES namespace_directory_transfer_ids(operation_id),
            effect_fingerprint BLOB NOT NULL CHECK(length(effect_fingerprint)=32),
            started_at INTEGER CHECK(started_at>0),
            accepted_revision TEXT CHECK(length(accepted_revision) BETWEEN 1 AND 4096),
            accepted_at INTEGER,
            PRIMARY KEY(transfer_id,ordinal),
            CHECK((accepted_at IS NULL AND accepted_revision IS NULL) OR
                  (started_at IS NOT NULL AND accepted_at IS NOT NULL AND accepted_at>=started_at AND accepted_revision IS NOT NULL)))
        """;

    private const string DirectoryTransferDurableCopyTable = """
        CREATE TABLE namespace_directory_transfer_durable_copies(
            effect_id TEXT PRIMARY KEY NOT NULL REFERENCES namespace_directory_transfer_copy_steps(effect_id),
            started_at INTEGER NOT NULL CHECK(started_at>0))
        """;

    private static async Task EnsureDirectoryTransferDurableCopySchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='namespace_directory_transfer_durable_copies';";
        var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null && version < 117)
        {
            sql.CommandText = DirectoryTransferDurableCopyTable;
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string text ||
            NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(DirectoryTransferDurableCopyTable))
            throw new InvalidDataException("The directory transfer durable copy schema is missing or changed.");
        if (version < 117)
        {
            sql.CommandText = "SELECT 1 FROM namespace_directory_transfer_durable_copies LIMIT 1;";
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply durable copy dispatch choices.");
            sql.CommandText = "PRAGMA user_version=117;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task InsertDirectoryTransferCopyStepsAsync(MirrorPulseDirectoryTransferAdmission plan,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = """
            INSERT INTO namespace_directory_transfer_copy_steps(transfer_id,ordinal,effect_id,effect_fingerprint)
            VALUES($transfer,$ordinal,$effect,$fingerprint);
            """;
        sql.Parameters.AddWithValue("$transfer", plan.Command.CommandId.ToString("D"));
        var ordinal = sql.Parameters.Add("$ordinal", SqliteType.Integer);
        var effect = sql.Parameters.Add("$effect", SqliteType.Text);
        var fingerprint = sql.Parameters.Add("$fingerprint", SqliteType.Blob);
        for (int index = 0; index < plan.Members.Count; index++)
        {
            ordinal.Value = index;
            effect.Value = plan.Members[index].CopyOperationId.ToString("D");
            fingerprint.Value = DirectoryTransferCopyFingerprint(plan.Members[index]);
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    internal async Task<MirrorPulseDirectoryTransferCopy> ReadNamespaceDirectoryTransferCopyAsync(Guid transferId,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadDirectoryTransferCopyCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseDirectoryTransferCopy> ReadDirectoryTransferCopyCoreAsync(Guid transferId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var plan = await ReadDirectoryTransferAdmissionCoreAsync(transferId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original directory transfer is missing.");
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = """
            SELECT s.ordinal,s.effect_id,s.started_at,s.accepted_revision,s.accepted_at,s.effect_fingerprint,d.started_at
            FROM namespace_directory_transfer_copy_steps s
            LEFT JOIN namespace_directory_transfer_durable_copies d ON d.effect_id=s.effect_id
            WHERE s.transfer_id=$transfer ORDER BY s.ordinal;
            """;
        sql.Parameters.AddWithValue("$transfer", transferId.ToString("D"));
        var steps = new List<MirrorPulseDirectoryTransferCopyStep>(plan.Members.Count);
        DateTimeOffset? previous = plan.Originals.CapturedAt;
        await using (var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (steps.Count == plan.Members.Count || reader.GetInt64(0) != steps.Count ||
                    reader.GetString(1) != plan.Members[steps.Count].CopyOperationId.ToString("D") ||
                    !DirectoryTransferCopyFingerprint(plan.Members[steps.Count]).AsSpan().SequenceEqual((byte[])reader[5]))
                    throw new InvalidDataException("The directory transfer lost its original copy order or effect identity.");
                var started = ReadDirectoryTransferTime(reader, 2);
                string? revision = reader.IsDBNull(3) ? null : reader.GetString(3);
                var accepted = ReadDirectoryTransferTime(reader, 4);
                var durableStart = ReadDirectoryTransferTime(reader, 6);
                if ((started is { } start && (previous is null || start < previous.Value)) ||
                    (durableStart is not null && durableStart != started) ||
                    (accepted is { } acceptance && (started is null || acceptance < started.Value ||
                        InvalidRemoteUpdateText(revision, 4096))) || (accepted is null && revision is not null))
                    throw new InvalidDataException("The directory transfer contains inconsistent original copy receipts.");
                steps.Add(new(plan.Members[steps.Count].CopyOperationId, started, revision, accepted, durableStart is not null));
                previous = accepted;
            }
        }
        if (steps.Count != plan.Members.Count)
            throw new InvalidDataException("One or more original directory transfer copy steps are missing.");
        return new(plan, steps.AsReadOnly());
    }

    private static DateTimeOffset? ReadDirectoryTransferTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        long ticks = reader.GetInt64(ordinal);
        if (ticks <= 0 || ticks > DateTimeOffset.MaxValue.Ticks)
            throw new InvalidDataException("The directory transfer copy timestamp is invalid.");
        return new(ticks, TimeSpan.Zero);
    }

    private static byte[] DirectoryTransferCopyFingerprint(MirrorPulseDirectoryTransferMember member) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(member, TopologyJsonOptions));

    /// <summary>Claims an original effect immediately before its first dispatch.</summary>
    internal async Task StartNamespaceDirectoryTransferCopyAsync(MirrorPulseDirectoryTransferAdmission plan,
        int ordinal, DateTimeOffset startedAt, CancellationToken token, bool durable = false)
    {
        if (startedAt < plan.Originals.CapturedAt)
            throw new ArgumentException("Copy start must follow the original directory transfer admission.", nameof(startedAt));
        var member = plan.Members[ordinal];
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = """
                UPDATE namespace_directory_transfer_copy_steps SET started_at=$started
                WHERE transfer_id=$transfer AND ordinal=$ordinal AND effect_id=$effect AND effect_fingerprint=$fingerprint
                  AND started_at IS NULL
                  AND EXISTS (
                      SELECT 1 FROM namespace_directory_transfer_admissions admission
                      WHERE admission.operation_id=$transfer AND admission.source_root_id=$source AND admission.target_root_id=$target)
                  AND ($ordinal=0 OR EXISTS (
                      SELECT 1 FROM namespace_directory_transfer_copy_steps prior
                      WHERE prior.transfer_id=$transfer AND prior.ordinal=$ordinal-1 AND prior.accepted_at<=$started));
                """;
            sql.Parameters.AddWithValue("$transfer", plan.Command.CommandId.ToString("D"));
            sql.Parameters.AddWithValue("$ordinal", ordinal);
            sql.Parameters.AddWithValue("$effect", member.CopyOperationId.ToString("D"));
            sql.Parameters.AddWithValue("$fingerprint", DirectoryTransferCopyFingerprint(member));
            sql.Parameters.AddWithValue("$source", plan.SourceRoute.RootId.ToString());
            sql.Parameters.AddWithValue("$target", plan.TargetRoute.RootId.ToString());
            sql.Parameters.AddWithValue("$started", startedAt.UtcTicks);
            if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new MirrorPulseMutationAmbiguousException("The directory copy effect needs its original acceptance or an earlier member.");
            if (durable)
            {
                sql.CommandText = "INSERT INTO namespace_directory_transfer_durable_copies(effect_id,started_at) VALUES($effect,$started);";
                await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains the first correlated Worker acceptance. Remote readback cannot supply this receipt.</summary>
    internal async Task RecordNamespaceDirectoryTransferCopyAcceptedAsync(Guid transferId, int ordinal, Guid effectId,
        string revision, DateTimeOffset acceptedAt, CancellationToken token)
    {
        if (InvalidRemoteUpdateText(revision, 4096))
            throw new ArgumentException("An original directory copy revision is required.", nameof(revision));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var sql = _connection.CreateCommand();
            sql.CommandText = """
                UPDATE namespace_directory_transfer_copy_steps SET accepted_revision=$revision, accepted_at=$accepted
                WHERE transfer_id=$transfer AND ordinal=$ordinal AND effect_id=$effect AND started_at<=$accepted
                  AND (accepted_at IS NULL OR (accepted_at=$accepted AND accepted_revision=$revision));
                """;
            sql.Parameters.AddWithValue("$transfer", transferId.ToString("D"));
            sql.Parameters.AddWithValue("$ordinal", ordinal);
            sql.Parameters.AddWithValue("$effect", effectId.ToString("D"));
            sql.Parameters.AddWithValue("$revision", revision);
            sql.Parameters.AddWithValue("$accepted", acceptedAt.UtcTicks);
            if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Directory copy acceptance requires its original start and immutable receipt.");
        }
        finally { _gate.Release(); }
    }
}
