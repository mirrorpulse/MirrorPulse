using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Authorizes one immutable, replayable acknowledgement of the complete original journal claim.</summary>
public sealed record MirrorPulseConflictKeepRemoteAcknowledgementStart(Guid CommandId, Guid InvocationId,
    ulong StoreVolumeSerialNumber, Guid StoreFileId, string JournalBindingSha256,
    Guid NativeAttemptId, Guid RestoreAttemptId, DateTimeOffset StartedAt);

/// <summary>Records a successful public SDK return and subsequent readback of every original journal ID.</summary>
public sealed record MirrorPulseConflictKeepRemoteAcknowledgementResult(Guid CommandId, Guid InvocationId,
    IReadOnlyList<Guid> VerifiedMissingOriginals, DateTimeOffset SdkReturnedAt, DateTimeOffset ReadBackAt);

public sealed record MirrorPulseConflictKeepRemoteAcknowledgement(MirrorPulseConflictKeepRemoteAcknowledgementStart Start,
    MirrorPulseConflictKeepRemoteAcknowledgementResult? Result);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseConflictKeepRemoteAcknowledgement?> ReadConflictKeepRemoteAcknowledgementAsync(Guid commandId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadKeepRemoteAcknowledgementFactsAsync(commandId, transaction, token).ConfigureAwait(false);
            if (retained is not null)
                await RequireKeepRemoteAcknowledgementAuthorityAsync(retained, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return retained;
        }
        finally { _gate.Release(); }
    }

    /// <remarks>The Host has verified every exact original in the official store and holds its original physical lease.</remarks>
    public async Task<MirrorPulseConflictKeepRemoteAcknowledgementStart> StartConflictKeepRemoteAcknowledgementAsync(Guid commandId,
        ulong storeVolumeSerialNumber, Guid storeFileId, DateTimeOffset startedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var retained = await ReadKeepRemoteAcknowledgementFactsAsync(commandId, transaction, token).ConfigureAwait(false);
            var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("KeepRemote acknowledgement requires the original file start.");
            if (first.StoreVolumeSerialNumber != storeVolumeSerialNumber || first.StoreFileId != storeFileId)
                throw new InvalidOperationException("KeepRemote acknowledgement requires the original physical journal store.");

            var restored = await ReadVerifiedKeepRemoteAccessAsync(commandId, transaction, token).ConfigureAwait(false);
            var start = retained?.Start ?? new(commandId, Guid.NewGuid(), storeVolumeSerialNumber, storeFileId,
                first.JournalBindingSha256, restored.NativeAttemptId, restored.AttemptId, startedAt);
            RequireKeepRemoteAcknowledgementStart(start, first, restored);
            if (retained is null)
            {
                await RequireKeepRemoteFileStartRootAsync(first, transaction, token).ConfigureAwait(false);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
                await using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO conflict_keep_remote_acknowledgements(command_id,invocation_id,start_payload,start_fingerprint)
                    VALUES($command,$invocation,$payload,$fingerprint);
                    """;
                insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
                insert.Parameters.AddWithValue("$invocation", start.InvocationId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (retained.Result is { } completed)
                await RequireKeepRemoteAcknowledgementResultAsync(completed, start, transaction, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return start;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Commits the verified acknowledgement, original conflict resolution and owned fence release together.</summary>
    public async Task<MirrorPulseConflictKeepRemoteAcknowledgementResult> CompleteConflictKeepRemoteAcknowledgementAsync(
        MirrorPulseConflictKeepRemoteAcknowledgementResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.VerifiedMissingOriginals is not { Count: >= 2 and <= 65_536 })
            throw new ArgumentException("The complete ordered original journal readback is required.", nameof(result));
        result = result with { VerifiedMissingOriginals = Array.AsReadOnly(result.VerifiedMissingOriginals.ToArray()) };
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var retained = await ReadKeepRemoteAcknowledgementFactsAsync(result.CommandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("KeepRemote acknowledgement has no retained invocation.");
            var first = await RequireKeepRemoteAcknowledgementAuthorityAsync(retained, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result, TopologyJsonOptions);
            if (retained.Result is { } historical)
            {
                if (!JsonSerializer.SerializeToUtf8Bytes(historical, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("The original KeepRemote acknowledgement result is immutable.");
                transaction.Commit();
                return historical;
            }
            await RequireKeepRemoteAcknowledgementResultAsync(result, retained.Start, transaction, token).ConfigureAwait(false);
            await RequireKeepRemoteFileStartRootAsync(first, transaction, token).ConfigureAwait(false);
            var preparation = (await ReadConflictKeepRemoteCoreAsync(result.CommandId, transaction, token).ConfigureAwait(false))!;

            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.Parameters.AddWithValue("$command", result.CommandId.ToString("D"));
            update.CommandText = """
                UPDATE upload_conflicts SET status=1 WHERE status=0 AND EXISTS(
                    SELECT 1 FROM conflict_keep_remote_members m WHERE m.command_id=$command
                    AND m.conflict_id=upload_conflicts.conflict_id AND m.original_id=upload_conflicts.change_id);
                """;
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != preparation.Intent.ConflictIds.Count)
                throw new InvalidDataException("An original KeepRemote conflict epoch changed before settlement.");
            update.CommandText = """
                UPDATE user_commands SET state='resolved' WHERE command_id=$command AND action='keepremote'
                AND target_id=$target AND state IN ('pending','failed');
                """;
            update.Parameters.AddWithValue("$target", preparation.Intent.ConflictId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("KeepRemote lost its original user command before settlement.");
            update.CommandText = """
                UPDATE user_commands SET state='superseded' WHERE state IN ('pending','failed')
                AND command_id IN (SELECT command_id FROM journal_coalescing_retries WHERE plan_id=$plan);
                """;
            update.Parameters.AddWithValue("$plan", preparation.Intent.PlanId.ToString("D"));
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            update.CommandText = "DELETE FROM conflict_keep_remote_file_pending WHERE command_id=$command AND root_id=$root;";
            update.Parameters.AddWithValue("$root", first.Original.Context.Member.RootId!.Value.ToString());
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("KeepRemote lost its owned root fence before settlement.");
            update.CommandText = """
                UPDATE conflict_keep_remote_acknowledgements SET result_payload=$payload,result_fingerprint=$fingerprint
                WHERE command_id=$command AND invocation_id=$invocation AND result_payload IS NULL;
                """;
            update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$invocation", result.InvocationId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("KeepRemote lost its original acknowledgement invocation before settlement.");
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemoteAcknowledgement?> ReadKeepRemoteAcknowledgementFactsAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT invocation_id,start_payload,start_fingerprint,result_payload,result_fingerprint
            FROM conflict_keep_remote_acknowledgements WHERE command_id=$command;
            """;
        query.Parameters.AddWithValue("$command", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var start = DecodeKeepRemoteAcknowledgement<MirrorPulseConflictKeepRemoteAcknowledgementStart>((byte[])reader[1], (byte[])reader[2], 4096);
        if (start.CommandId != commandId || start.InvocationId == Guid.Empty || start.InvocationId.ToString("D") != reader.GetString(0))
            throw new InvalidDataException("The retained KeepRemote acknowledgement lost its original owner.");
        var result = reader.IsDBNull(3) ? null :
            DecodeKeepRemoteAcknowledgement<MirrorPulseConflictKeepRemoteAcknowledgementResult>((byte[])reader[3], (byte[])reader[4], 3_145_728);
        if (result?.VerifiedMissingOriginals is { } ids)
            result = result with { VerifiedMissingOriginals = Array.AsReadOnly(ids.ToArray()) };
        return new(start, result);
    }

    private static T DecodeKeepRemoteAcknowledgement<T>(byte[] payload, byte[] fingerprint, int maximumBytes) where T : class
    {
        if (payload.Length > maximumBytes || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("A KeepRemote acknowledgement fingerprint changed.");
        try
        {
            var fact = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions);
            if (fact is not null && JsonSerializer.SerializeToUtf8Bytes(fact, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                return fact;
        }
        catch (JsonException error) { throw new InvalidDataException("A KeepRemote acknowledgement is invalid.", error); }
        throw new InvalidDataException("A KeepRemote acknowledgement lost its canonical payload.");
    }

    private async Task<MirrorPulseConflictKeepRemoteFileStart> RequireKeepRemoteAcknowledgementAuthorityAsync(
        MirrorPulseConflictKeepRemoteAcknowledgement retained, SqliteTransaction transaction, CancellationToken token)
    {
        var first = await ReadKeepRemoteFileStartCoreAsync(retained.Start.CommandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The KeepRemote acknowledgement lost its original file start.");
        var restored = await ReadVerifiedKeepRemoteAccessAsync(first.CommandId, transaction, token).ConfigureAwait(false);
        RequireKeepRemoteAcknowledgementStart(retained.Start, first, restored);
        if (retained.Result is { } result)
            await RequireKeepRemoteAcknowledgementResultAsync(result, retained.Start, transaction, token).ConfigureAwait(false);
        return first;
    }

    private async Task<MirrorPulseConflictKeepRemoteRestoreResult> ReadVerifiedKeepRemoteAccessAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token) =>
        (await ReadKeepRemoteRestoreAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false))
            .SingleOrDefault(attempt => attempt.Result?.ProductVerified == true)?.Result
            ?? throw new InvalidOperationException("KeepRemote acknowledgement requires verified native confirmation and access restoration.");

    private static void RequireKeepRemoteAcknowledgementStart(MirrorPulseConflictKeepRemoteAcknowledgementStart value,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteRestoreResult restored)
    {
        if (value.CommandId != first.CommandId || value.InvocationId == Guid.Empty || value.StoreFileId != first.StoreFileId ||
            value.StoreVolumeSerialNumber != first.StoreVolumeSerialNumber || value.JournalBindingSha256 != first.JournalBindingSha256 ||
            value.NativeAttemptId != restored.NativeAttemptId || value.RestoreAttemptId != restored.AttemptId || value.StartedAt < restored.RecordedAt)
            throw new InvalidOperationException("KeepRemote acknowledgement requires its original store, journal and verified access receipt.");
    }

    private async Task RequireKeepRemoteAcknowledgementResultAsync(MirrorPulseConflictKeepRemoteAcknowledgementResult value,
        MirrorPulseConflictKeepRemoteAcknowledgementStart start, SqliteTransaction transaction, CancellationToken token)
    {
        var journal = await ReadKeepRemoteJournalBindingCoreAsync(start.CommandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote journal claim is missing.");
        if (value.CommandId != start.CommandId || value.InvocationId != start.InvocationId ||
            value.SdkReturnedAt < start.StartedAt || value.ReadBackAt < value.SdkReturnedAt ||
            value.VerifiedMissingOriginals is null || !value.VerifiedMissingOriginals.SequenceEqual(journal.Originals.Select(row => row.OperationId)))
            throw new InvalidOperationException("KeepRemote requires a successful SDK return followed by readback of every exact original ID.");
    }

    private async Task<bool> HasKeepRemoteAcknowledgementResultAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT 1 FROM conflict_keep_remote_acknowledgements WHERE command_id=$command AND result_payload IS NOT NULL;
            """;
        query.Parameters.AddWithValue("$command", commandId.ToString("D"));
        return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }
}
