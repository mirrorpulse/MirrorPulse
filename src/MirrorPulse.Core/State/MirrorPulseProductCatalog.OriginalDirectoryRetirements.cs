using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>The immutable request and optional verified Worker response for a journal-owned directory effect.</summary>
public sealed record MirrorPulseOriginalDirectoryRetirement(AdapterDirectoryRetirementRequest Request,
    AdapterOwnedChildEffectReceipt? Receipt);

public sealed partial class MirrorPulseProductCatalog
{
    private const string OriginalDirectoryRetirementSchema = """
        CREATE TABLE original_directory_retirements(
            operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
            owner_hash BLOB NOT NULL CHECK(length(owner_hash)=32),
            request BLOB NOT NULL CHECK(length(request) BETWEEN 1 AND 1048576),
            request_hash BLOB NOT NULL CHECK(length(request_hash)=32),
            receipt BLOB NULL CHECK(length(receipt) BETWEEN 1 AND 1048576),
            receipt_hash BLOB NULL CHECK(length(receipt_hash)=32),
            CHECK((receipt IS NULL)=(receipt_hash IS NULL)))
        """;

    private static async Task EnsureOriginalDirectoryRetirementSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='original_directory_retirements';";
        var existing = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (originalVersion >= 105) throw new InvalidDataException("Schema105 lost its original directory retirement ledger.");
            command.CommandText = OriginalDirectoryRetirementSchema;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(OriginalDirectoryRetirementSchema))
        {
            throw new InvalidDataException("The original directory retirement ledger has an unexpected schema.");
        }
        if (originalVersion < 105)
        {
            command.CommandText = "SELECT 1 FROM original_directory_retirements LIMIT 1;";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply original directory retirement evidence.");
            command.CommandText = "PRAGMA user_version=105;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public async Task<MirrorPulseOriginalDirectoryRetirement?> ReadOriginalDirectoryRetirementAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadOriginalDirectoryRetirementCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return retained;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseOriginalDirectoryRetirement> StartOriginalDirectoryRetirementAsync(
        AdapterDirectoryRetirementRequest request, CancellationToken token)
    {
        byte[] payload = EncodeDirectoryRetirementEvidence(request);
        // Validate and dispatch the frozen bytes even if the caller owns mutable dependency collections.
        var frozen = AdapterProtocolJson.Decode<AdapterDirectoryRetirementRequest>(payload);
        frozen.Validate();
        Guid operationId = frozen.Operation.OperationId;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var retained = await ReadOriginalDirectoryRetirementCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained.Request.RequestFingerprint != frozen.RequestFingerprint)
                    throw new InvalidDataException("The original directory retirement request changed.");
                transaction.Commit();
                return retained;
            }

            var owner = await RequireDirectoryRetirementOwnerAsync(frozen, transaction, token).ConfigureAwait(false);
            if (owner.State != MirrorPulseMutationState.Prepared || owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted ||
                owner.AcceptedRevision is not null)
                throw new InvalidOperationException("A previously started mutation cannot acquire new directory retirement evidence.");
            var step = await RequireDirectoryPlanStepAsync(operationId, transaction, token).ConfigureAwait(false);
            var derived = await BuildDirectoryPlanRetirementAsync(step, transaction, token).ConfigureAwait(false);
            if (frozen.RequestFingerprint != derived.RequestFingerprint)
                throw new InvalidDataException("The Worker retirement request differs from the sealed directory plan.");

            await using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO original_directory_retirements(operation_id,owner_hash,request,request_hash)
                VALUES($operation,$owner,$request,$hash);
                """;
            insert.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$owner", DirectoryRetirementOwnerHash(owner.Intent));
            insert.Parameters.AddWithValue("$request", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await SetDirectoryRetirementStateAsync(operationId, owner.State, MirrorPulseMutationState.Executing, null,
                transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return new(frozen, null);
        }
        finally { _gate.Release(); }
    }

    internal async Task<AdapterOwnedChildEffectReceipt> AcceptOriginalDirectoryRetirementAsync(Guid operationId,
        AdapterOwnedChildEffectReceipt receipt, CancellationToken token)
    {
        byte[] payload = EncodeDirectoryRetirementEvidence(receipt);
        var frozen = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(payload);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var retained = await ReadOriginalDirectoryRetirementCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Directory retirement has no original request.");
            retained.Request.RequireAcceptedReceipt(frozen);
            if (retained.Receipt is { } accepted)
            {
                if (AdapterDirectoryEffects.Fingerprint(accepted) != AdapterDirectoryEffects.Fingerprint(frozen))
                    throw new InvalidDataException("The accepted directory retirement receipt changed.");
                transaction.Commit();
                return accepted;
            }
            var step = await ReadDirectoryPlanStepCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (step is not null)
            {
                var derived = await BuildDirectoryPlanRetirementAsync(step, transaction, token).ConfigureAwait(false);
                if (retained.Request.RequestFingerprint != derived.RequestFingerprint)
                    throw new InvalidDataException("The retirement receipt lost its original directory plan request.");
            }

            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE original_directory_retirements SET receipt=$receipt,receipt_hash=$hash
                WHERE operation_id=$operation AND receipt IS NULL;
                """;
            update.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            update.Parameters.AddWithValue("$receipt", payload);
            update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The directory retirement receipt changed during acceptance.");
            await SetDirectoryRetirementStateAsync(operationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted,
                frozen.AcceptedRevision, transaction, token).ConfigureAwait(false);
            if (step is not null)
                await StoreDirectoryPlanReceiptAsync(step, frozen, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return frozen;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseOriginalDirectoryRetirement?> ReadOriginalDirectoryRetirementCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        AdapterDirectoryRetirementRequest request;
        AdapterOwnedChildEffectReceipt? receipt = null;
        byte[] ownerHash;
        await using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT request,request_hash,receipt,receipt_hash,owner_hash
                FROM original_directory_retirements WHERE operation_id=$operation;
                """;
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            request = DecodeDirectoryRetirementEvidence<AdapterDirectoryRetirementRequest>((byte[])reader[0], (byte[])reader[1]);
            if (!reader.IsDBNull(2))
                receipt = DecodeDirectoryRetirementEvidence<AdapterOwnedChildEffectReceipt>((byte[])reader[2], (byte[])reader[3]);
            ownerHash = (byte[])reader[4];
        }
        request.Validate();
        if (request.Operation.OperationId != operationId)
            throw new InvalidDataException("The directory retirement request lost its original operation.");
        var owner = await RequireDirectoryRetirementOwnerAsync(request, transaction, token).ConfigureAwait(false);
        if (!DirectoryRetirementOwnerHash(owner.Intent).AsSpan().SequenceEqual(ownerHash))
            throw new InvalidDataException("The original directory retirement owner changed.");
        if (receipt is not null) request.RequireAcceptedReceipt(receipt);
        var expectedState = receipt is null ? MirrorPulseMutationState.Executing : MirrorPulseMutationState.RemoteAccepted;
        if (owner.State != expectedState || owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.Started ||
            owner.AcceptedRevision != receipt?.AcceptedRevision)
            throw new InvalidDataException("The directory retirement receipt and its mutation state disagree.");
        return new(request, receipt);
    }

    private Task<MirrorPulseMutationRecord> RequireDirectoryRetirementOwnerAsync(AdapterDirectoryRetirementRequest request,
        SqliteTransaction transaction, CancellationToken token) =>
        RequireOriginalDirectoryOwnerAsync(request.Retirement.OriginalDirectory.InstanceId, request.OperationKind, request.Operation,
            request.Retirement.OriginalDirectory.OriginalRevision, transaction, token);

    private async Task<MirrorPulseMutationRecord> RequireOriginalDirectoryOwnerAsync(Guid instanceId, string operationKind,
        AdapterOperationRequest operation, string originalRevision, SqliteTransaction transaction, CancellationToken token)
    {
        var owner = await ReadMutationCoreAsync(operation.OperationId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The directory retirement requires an original journal mutation.");
        var intent = owner.Intent;
        bool matchesAddress = operationKind == "Delete"
            ? intent.Kind == MirrorPulseWorkerChangeKind.Delete && intent.RootKey == operation.RootKey &&
                intent.RelativePath.Replace('\\', '/') == operation.Path &&
                (intent.Origin != MirrorPulseMutationOrigin.Journal || intent.JournalItemId is not null)
            : intent.Kind == MirrorPulseWorkerChangeKind.Move && (intent.PreviousRootKey ?? intent.RootKey) == operation.RootKey &&
                intent.PreviousRelativePath?.Replace('\\', '/') == operation.Path && intent.RootKey == operation.DestinationRootKey &&
                intent.RelativePath.Replace('\\', '/') == operation.DestinationPath;
        if (intent.IsDirectory != operation.IsDirectory || intent.Origin is not (MirrorPulseMutationOrigin.Journal or MirrorPulseMutationOrigin.CoalescedJournal) ||
            intent.InstanceId.Value != instanceId || intent.ExpectedRevision != originalRevision || !matchesAddress)
            throw new InvalidDataException("The directory retirement does not match its original journal mutation.");
        return owner;
    }

    private async Task SetDirectoryRetirementStateAsync(Guid operationId, MirrorPulseMutationState from, MirrorPulseMutationState to,
        string? revision, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mutation_intents SET state=$to,accepted_revision=$revision,execution_started=1,updated_utc=$updated
            WHERE operation_id=$operation AND state=$from;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$from", (int)from);
        command.Parameters.AddWithValue("$to", (int)to);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The original directory mutation changed during retirement.");
    }

    private static byte[] EncodeDirectoryRetirementEvidence<T>(T evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        byte[] payload = AdapterProtocolJson.Encode(evidence);
        if (payload.Length > AdapterDirectoryRetirementWire.MaximumPayloadBytes)
            throw new InvalidDataException("The directory retirement evidence exceeds the Worker protocol limit.");
        return payload;
    }

    private static byte[] DirectoryRetirementOwnerHash(MirrorPulseMutationIntent intent) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions));

    private static T DecodeDirectoryRetirementEvidence<T>(byte[] payload, byte[] hash)
    {
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("The directory retirement evidence changed in the catalog.");
        return AdapterProtocolJson.Decode<T>(payload);
    }
}
