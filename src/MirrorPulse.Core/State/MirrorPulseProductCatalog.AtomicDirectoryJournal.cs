#if HOST_ATOMIC_DIRECTORY_CANDIDATE
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseAtomicDirectoryJournal(Guid AdmissionId, string AdmissionRequestFingerprint,
    IReadOnlyList<MirrorPulseWorkerChangeCommand> Originals, string? ReceiptFingerprint = null, bool Completed = false);

public sealed partial class MirrorPulseProductCatalog
{
    private const string AtomicDirectoryJournalSchema = """
        CREATE TABLE original_atomic_directory_journal(
            admission_id TEXT NOT NULL PRIMARY KEY REFERENCES original_atomic_directory_batches(admission_id),
            evidence BLOB NOT NULL CHECK(length(evidence) BETWEEN 1 AND 1048576),
            evidence_hash BLOB NOT NULL CHECK(length(evidence_hash)=32),
            completed INTEGER NOT NULL CHECK(completed IN (0,1)))
        """;
    private const string PendingAtomicDirectoryJournalIndex = """
        CREATE INDEX ix_original_atomic_directory_journal_pending
        ON original_atomic_directory_journal(admission_id) WHERE completed=0
        """;

    private static async Task EnsureAtomicDirectoryJournalSchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='original_atomic_directory_journal';";
        var existing = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (version >= 108) throw new InvalidDataException("Schema108 lost its atomic directory journal ledger.");
            command.CommandText = AtomicDirectoryJournalSchema;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(AtomicDirectoryJournalSchema))
            throw new InvalidDataException("The atomic directory journal ledger has an unexpected schema.");
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_original_atomic_directory_journal_pending';";
        var index = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (index is null)
        {
            command.CommandText = PendingAtomicDirectoryJournalIndex;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (index is not string indexSql ||
            NormalizeRetiredTransferIndexSql(indexSql) != NormalizeRetiredTransferIndexSql(PendingAtomicDirectoryJournalIndex))
            throw new InvalidDataException("The atomic directory journal recovery index has an unexpected schema.");
        if (version < 108)
        {
            command.CommandText = "SELECT 1 FROM original_atomic_directory_journal LIMIT 1;";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply atomic directory acknowledgement evidence.");
            command.CommandText = "PRAGMA user_version=108;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task<MirrorPulseAtomicDirectoryBatch?> ReadAtomicDirectoryBatchForOperationAsync(Guid operation, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT m.admission_id FROM original_directory_plan_members m
                JOIN original_atomic_directory_batches b USING(admission_id) WHERE m.operation_id=$id;
                """;
            command.Parameters.AddWithValue("$id", operation.ToString("D"));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string id
                ? await ReadAtomicDirectoryBatchCoreAsync(Guid.Parse(id), transaction, token).ConfigureAwait(false) : null;
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<Guid>> ReadPendingAtomicDirectoryJournalAsync(Guid? after, int limit, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT admission_id FROM original_atomic_directory_journal
                WHERE completed=0 AND admission_id>$after ORDER BY admission_id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$after", after?.ToString("D") ?? string.Empty);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlySet<Guid>> ReadUnsettledAtomicDirectoryOperationIdsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT m.operation_id FROM original_directory_plan_members m
                JOIN original_atomic_directory_batches b USING(admission_id)
                LEFT JOIN original_atomic_directory_journal j USING(admission_id)
                WHERE j.completed IS NULL OR j.completed=0;
                """;
            var result = new HashSet<Guid>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseAtomicDirectoryJournal> BeginAtomicDirectoryJournalAcknowledgementAsync(Guid admissionId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var batch = await ReadAtomicDirectoryBatchCoreAsync(admissionId, transaction, token).ConfigureAwait(false);
            if (batch?.Receipt is not { } receipt)
                throw new InvalidOperationException("An accepted Worker receipt is required before acknowledging the original journal.");
            var journal = await ReadAtomicDirectoryJournalCoreAsync(admissionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The atomic batch has no sealed official journal originals.");
            if (journal.ReceiptFingerprint is null)
            {
                journal = journal with { ReceiptFingerprint = receipt.Fingerprint() };
                await WriteAtomicDirectoryJournalAsync(journal, insert: false, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
            return journal;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseAtomicDirectoryJournal> ReadAcceptedAtomicDirectoryJournalAsync(Guid admissionId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await ReadAtomicDirectoryBatchCoreAsync(admissionId, transaction, token).ConfigureAwait(false);
            if (batch?.Receipt is null)
                throw new InvalidOperationException("An accepted Worker receipt is required before reading acknowledgement evidence.");
            return await ReadAtomicDirectoryJournalCoreAsync(admissionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The atomic batch has no sealed official journal originals.");
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseAtomicDirectoryJournal> ReadAtomicDirectoryJournalAsync(Guid admissionId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            return await ReadAtomicDirectoryJournalCoreAsync(admissionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The atomic batch has no sealed official journal originals.");
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteAtomicDirectoryJournalAcknowledgementAsync(Guid admissionId, string receiptFingerprint, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var batch = await ReadAtomicDirectoryBatchCoreAsync(admissionId, transaction, token).ConfigureAwait(false);
            var journal = await ReadAtomicDirectoryJournalCoreAsync(admissionId, transaction, token).ConfigureAwait(false);
            if (batch?.Receipt is null || journal?.ReceiptFingerprint != receiptFingerprint || journal is null)
                throw new InvalidOperationException("The original journal acknowledgement was not started for this receipt.");
            if (!journal.Completed)
            {
                foreach (var original in journal.Originals)
                    await SetDirectoryRetirementStateAsync(original.OperationId, MirrorPulseMutationState.RemoteAccepted,
                        MirrorPulseMutationState.Acknowledged, null, transaction, token).ConfigureAwait(false);
                await WriteAtomicDirectoryJournalAsync(journal with { Completed = true }, insert: false, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseAtomicDirectoryJournal?> ReadAtomicDirectoryJournalCoreAsync(Guid admissionId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT evidence,evidence_hash,completed FROM original_atomic_directory_journal WHERE admission_id=$id;";
        command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var journal = DecodeDirectoryRetirementEvidence<MirrorPulseAtomicDirectoryJournal>((byte[])reader[0], (byte[])reader[1]);
        if (journal.AdmissionId != admissionId || journal.Completed != (reader.GetInt32(2) == 1) ||
            journal.Completed && journal.ReceiptFingerprint is null || journal.Originals is not { Count: >= 1 and <= 2048 })
            throw new InvalidDataException("The original atomic journal evidence changed.");
        return journal;
    }

    private async Task WriteAtomicDirectoryJournalAsync(MirrorPulseAtomicDirectoryJournal journal, bool insert,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] bytes = EncodeDirectoryRetirementEvidence(journal);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? "INSERT INTO original_atomic_directory_journal(admission_id,evidence,evidence_hash,completed) VALUES($id,$bytes,$hash,$complete);"
            : "UPDATE original_atomic_directory_journal SET evidence=$bytes,evidence_hash=$hash,completed=$complete WHERE admission_id=$id AND completed=0;";
        command.Parameters.AddWithValue("$id", journal.AdmissionId.ToString("D"));
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$hash", SHA256.HashData(bytes));
        command.Parameters.AddWithValue("$complete", journal.Completed ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The original atomic journal evidence could not be saved.");
    }

    private static bool MatchesAtomicJournalOriginal(MirrorPulseWorkerChangeCommand? original, MirrorPulseMutationIntent intent) =>
        original is
        {
            Kind: MirrorPulseWorkerChangeKind.Delete, Sequence: > 0, ItemId: { } item,
            PreviousInstanceId: null, PreviousRootKey: null, PreviousRelativePath: null
        } && item != Guid.Empty &&
        original.OperationId == intent.OperationId && original.InstanceId == intent.InstanceId && original.RootKey == intent.RootKey &&
        original.ItemId == intent.JournalItemId && original.IsDirectory == intent.IsDirectory && original.ObservedAt != default &&
        original.RelativePath.Replace('\\', '/') == intent.RelativePath.Replace('\\', '/');
}
#endif
