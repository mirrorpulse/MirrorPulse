using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseAtomicDirectoryBatch(AdapterAtomicDirectoryDeletionRequest Request,
    AdapterAtomicDirectoryDeletionReceipt? Receipt);

public sealed partial class MirrorPulseProductCatalog
{
    private const string AtomicDirectoryBatchSchema = """
        CREATE TABLE original_atomic_directory_batches(
            admission_id TEXT NOT NULL PRIMARY KEY REFERENCES original_directory_plans(admission_id),
            request BLOB NOT NULL CHECK(length(request) BETWEEN 1 AND 1048576),
            request_hash BLOB NOT NULL CHECK(length(request_hash)=32),
            receipt BLOB NULL CHECK(length(receipt) BETWEEN 1 AND 1048576),
            receipt_hash BLOB NULL CHECK(length(receipt_hash)=32),
            CHECK((receipt IS NULL)=(receipt_hash IS NULL)))
        """;

    private static async Task EnsureAtomicDirectoryBatchSchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='original_atomic_directory_batches';";
        var existing = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (version >= 107) throw new InvalidDataException("Schema107 lost its atomic directory batch ledger.");
            command.CommandText = AtomicDirectoryBatchSchema;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(AtomicDirectoryBatchSchema))
            throw new InvalidDataException("The atomic directory batch ledger has an unexpected schema.");
        if (version < 107)
        {
            command.CommandText = "SELECT 1 FROM original_atomic_directory_batches LIMIT 1;";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply atomic directory batch evidence.");
            command.CommandText = "PRAGMA user_version=107;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task<MirrorPulseAtomicDirectoryBatch> StartAtomicDirectoryBatchAsync(
        AdapterDirectoryAdmissionRequest original, AdapterDirectoryAdmission admission, CancellationToken token,
        IReadOnlyList<MirrorPulseWorkerChangeCommand>? originals = null)
    {
        original = AdapterProtocolJson.Decode<AdapterDirectoryAdmissionRequest>(EncodeDirectoryRetirementEvidence(original));
        admission = AdapterProtocolJson.Decode<AdapterDirectoryAdmission>(EncodeDirectoryRetirementEvidence(admission));
        admission.RequireRequest(original);
        var plan = BuildAtomicDirectoryPlan(original, admission);
        byte[] manifest = EncodeDirectoryRetirementEvidence(plan);
        byte[] manifestHash = SHA256.HashData(manifest);
        var request = new AdapterAtomicDirectoryDeletionRequest(1, original, admission, Convert.ToHexString(manifestHash), string.Empty);
        request = request with { RequestFingerprint = request.CalculateFingerprint() };
        byte[] payload = EncodeDirectoryRetirementEvidence(request);
        var journal = originals is null ? null : AdapterProtocolJson.Decode<MirrorPulseAtomicDirectoryJournal>(
            EncodeDirectoryRetirementEvidence(new MirrorPulseAtomicDirectoryJournal(original.AdmissionId, original.RequestFingerprint, originals)));
        if (journal is not null && (journal.Originals.Count != plan.Steps.Count ||
            journal.Originals.Select(value => value.Sequence).Distinct().Count() != plan.Steps.Count ||
            journal.Originals.Select(value => value.ItemId).Distinct().Count() != plan.Steps.Count))
            throw new InvalidDataException("The original journal batch requires distinct ordered observations.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var existing = await ReadAtomicDirectoryBatchCoreAsync(original.AdmissionId, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Request.RequestFingerprint != request.RequestFingerprint)
                    throw new InvalidDataException("The original atomic directory batch authorization changed.");
                if (journal is not null)
                {
                    var retained = await ReadAtomicDirectoryJournalCoreAsync(original.AdmissionId, transaction, token).ConfigureAwait(false);
                    if (retained is null || !retained.Originals.SequenceEqual(journal.Originals))
                        throw new InvalidDataException("The original atomic journal observations changed.");
                }
                transaction.Commit();
                return existing;
            }
            await SealDirectoryPlanCoreAsync(plan, manifest, manifestHash, transaction, token).ConfigureAwait(false);
            for (int ordinal = 0; ordinal < plan.Steps.Count; ordinal++)
            {
                var member = await ReadDirectoryPlanMemberAsync(plan, request.ManifestFingerprint, ordinal, transaction, token).ConfigureAwait(false);
                if (member.Receipt is not null || member.Owner.State != MirrorPulseMutationState.Prepared ||
                    member.Owner.Intent.Origin != MirrorPulseMutationOrigin.Journal ||
                    journal is not null && !MatchesAtomicJournalOriginal(journal.Originals[ordinal], member.Owner.Intent))
                    throw new InvalidOperationException("A started directory operation cannot join an atomic batch.");
                await SetDirectoryRetirementStateAsync(member.Step.Operation.OperationId, MirrorPulseMutationState.Prepared,
                    MirrorPulseMutationState.Executing, null, transaction, token).ConfigureAwait(false);
            }
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO original_atomic_directory_batches(admission_id,request,request_hash) VALUES($id,$request,$hash);
                """;
            command.Parameters.AddWithValue("$id", original.AdmissionId.ToString("D"));
            command.Parameters.AddWithValue("$request", payload);
            command.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (journal is not null)
                await WriteAtomicDirectoryJournalAsync(journal, insert: true, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return new(request, null);
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseAtomicDirectoryBatch?> ReadAtomicDirectoryBatchAsync(Guid admissionId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadAtomicDirectoryBatchCoreAsync(admissionId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return retained;
        }
        finally { _gate.Release(); }
    }

    internal async Task<AdapterAtomicDirectoryDeletionReceipt> AcceptAtomicDirectoryBatchAsync(Guid admissionId,
        AdapterAtomicDirectoryDeletionReceipt receipt, CancellationToken token)
    {
        byte[] payload = EncodeDirectoryRetirementEvidence(receipt);
        var frozen = AdapterProtocolJson.Decode<AdapterAtomicDirectoryDeletionReceipt>(payload);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var batch = await ReadAtomicDirectoryBatchCoreAsync(admissionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original atomic directory batch has not started.");
            frozen.RequireRequest(batch.Request);
            if (batch.Receipt is { } original)
            {
                if (original.Fingerprint() != frozen.Fingerprint())
                    throw new InvalidDataException("The original atomic directory batch receipt changed.");
                transaction.Commit();
                return original;
            }

            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE original_atomic_directory_batches SET receipt=$receipt,receipt_hash=$hash
                WHERE admission_id=$id AND receipt IS NULL;
                """;
            command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
            command.Parameters.AddWithValue("$receipt", payload);
            command.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The atomic directory batch changed while accepting its receipt.");
            foreach (var operation in batch.Request.OriginalRequest.Operations)
                await SetDirectoryRetirementStateAsync(operation.Operation.OperationId, MirrorPulseMutationState.Executing,
                    MirrorPulseMutationState.RemoteAccepted, null, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return frozen;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseAtomicDirectoryBatch?> ReadAtomicDirectoryBatchCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT b.request,b.request_hash,b.receipt,b.receipt_hash,p.manifest,p.manifest_hash
            FROM original_atomic_directory_batches b JOIN original_directory_plans p USING(admission_id) WHERE admission_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        AdapterAtomicDirectoryDeletionRequest request;
        AdapterAtomicDirectoryDeletionReceipt? receipt;
        MirrorPulseOriginalDirectoryPlan plan;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            request = DecodeDirectoryRetirementEvidence<AdapterAtomicDirectoryDeletionRequest>((byte[])reader[0], (byte[])reader[1]);
            receipt = reader.IsDBNull(2) ? null :
                DecodeDirectoryRetirementEvidence<AdapterAtomicDirectoryDeletionReceipt>((byte[])reader[2], (byte[])reader[3]);
            byte[] manifest = (byte[])reader[4];
            byte[] hash = (byte[])reader[5];
            request.Validate();
            plan = DecodeDirectoryRetirementEvidence<MirrorPulseOriginalDirectoryPlan>(manifest, hash);
            if (request.OriginalRequest.AdmissionId != id || request.ManifestFingerprint != Convert.ToHexString(hash) ||
                !manifest.AsSpan().SequenceEqual(EncodeDirectoryRetirementEvidence(BuildAtomicDirectoryPlan(request.OriginalRequest, request.Admission))))
                throw new InvalidDataException("The atomic directory batch lost its original plan.");
        }
        receipt?.RequireRequest(request);
        var journal = await ReadAtomicDirectoryJournalCoreAsync(id, transaction, token).ConfigureAwait(false);
        if (journal is not null && journal.AdmissionRequestFingerprint != request.OriginalRequest.RequestFingerprint)
            throw new InvalidDataException("The journal acknowledgement lost its original request.");
        if (journal?.ReceiptFingerprint is { } fingerprint &&
            (receipt is null || receipt.Fingerprint() != fingerprint))
            throw new InvalidDataException("The journal acknowledgement lost its original Worker receipt.");
        var state = receipt is null ? MirrorPulseMutationState.Executing : journal?.Completed == true
            ? MirrorPulseMutationState.Acknowledged : MirrorPulseMutationState.RemoteAccepted;
        for (int ordinal = 0; ordinal < plan.Steps.Count; ordinal++)
        {
            var member = await ReadDirectoryPlanMemberAsync(plan, request.ManifestFingerprint, ordinal, transaction, token, state).ConfigureAwait(false);
            if (member.Owner.Intent.Origin != MirrorPulseMutationOrigin.Journal ||
                journal is not null && (journal.Originals.Count != plan.Steps.Count ||
                    !MatchesAtomicJournalOriginal(journal.Originals[ordinal], member.Owner.Intent)))
                throw new InvalidDataException("The atomic batch lost its original journal owner.");
        }
        return new(request, receipt);
    }

    private async Task RequireNoAtomicDirectoryBatchAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM original_atomic_directory_batches WHERE admission_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("The original directory operations belong to an atomic batch.");
    }

    private static MirrorPulseOriginalDirectoryPlan BuildAtomicDirectoryPlan(AdapterDirectoryAdmissionRequest original, AdapterDirectoryAdmission admission)
    {
        var steps = original.Operations.Select((step, ordinal) =>
        {
            var member = admission.Operations[ordinal];
            return new MirrorPulseOriginalDirectoryStep(step.OperationKind, step.Operation, member.OriginalGeneration,
                member.Directory, member.OriginalParents);
        }).ToArray();
        var plan = new MirrorPulseOriginalDirectoryPlan(new InstanceId(original.InstanceId), original.AdmissionId, steps);
        plan.Validate();
        return plan;
    }
}
