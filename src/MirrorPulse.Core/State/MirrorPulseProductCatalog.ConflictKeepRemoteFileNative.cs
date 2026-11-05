using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A retained SDK request boundary, before any native identity or synchronization change.</summary>
public sealed record MirrorPulseConflictKeepRemoteNativeStart(int Version, Guid CommandId, Guid ActionId, Guid AttemptId,
    string SelectedPlaceholderIdentity, string ObservedPlaceholderIdentity, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification Parent, DateTimeOffset StartedAt);

/// <summary>The actual public SDK result while the original object remains sealed. It grants no restoration or ACK authority.</summary>
public sealed record MirrorPulseConflictKeepRemoteNativeResult(int Version, Guid CommandId, Guid ActionId, Guid AttemptId,
    MirrorPulseContentConfirmationReceipt Receipt, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification Parent, DateTimeOffset RecordedAt);

/// <summary>A missing result retains uncertainty; a later attempt cannot replace the original request or result.</summary>
public sealed record MirrorPulseConflictKeepRemoteNativeAttempt(MirrorPulseConflictKeepRemoteNativeStart Start,
    MirrorPulseConflictKeepRemoteNativeResult? Result);

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseConflictKeepRemoteNativeStart> RecordConflictKeepRemoteNativeStartAsync(
        MirrorPulseConflictKeepRemoteNativeStart start, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return RecordKeepRemoteNativeFactAsync(start.CommandId, start.ActionId, start.AttemptId, 1, start, token);
    }

    public Task<MirrorPulseConflictKeepRemoteNativeResult> RecordConflictKeepRemoteNativeResultAsync(
        MirrorPulseConflictKeepRemoteNativeResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RecordKeepRemoteNativeFactAsync(result.CommandId, result.ActionId, result.AttemptId, 2, result, token);
    }

    public async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteNativeAttempt>> ReadConflictKeepRemoteNativeAttemptsAsync(
        Guid commandId, CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadKeepRemoteNativeAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RecordKeepRemoteNativeFactAsync<T>(Guid commandId, Guid actionId, Guid attemptId, int kind, T value, CancellationToken token)
        where T : class
    {
        if (commandId == Guid.Empty || actionId == Guid.Empty || attemptId == Guid.Empty || attemptId == commandId || attemptId == actionId)
            throw new ArgumentException("Native confirmation requires distinct original command, action and attempt IDs.");
        byte[] payload = KeepRemoteContentFactPayload(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var attempts = await ReadKeepRemoteNativeAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original file start must precede native confirmation.");
            var content = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original content action must precede native confirmation.");
            var existing = attempts.SingleOrDefault(attempt => attempt.Start.AttemptId == attemptId);
            object? retained = kind == 1 ? existing?.Start : existing?.Result;
            if (retained is not null)
            {
                if (!KeepRemoteContentFactPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("An original native confirmation boundary is immutable.");
                transaction.Commit(); return (T)retained;
            }
            if (kind == 1)
            {
                RequireKeepRemoteNativeStart((MirrorPulseConflictKeepRemoteNativeStart)(object)value, first, content, attempts);
                if (attempts.Any(attempt => attempt.Result?.Receipt.MayAcknowledge == true))
                    throw new InvalidOperationException("A completed native confirmation is historical and cannot start another byte confirmation.");
            }
            else
            {
                if (existing is null || attempts[^1] != existing)
                    throw new InvalidOperationException("A native result requires the latest original before-native attempt.");
                RequireKeepRemoteNativeResult((MirrorPulseConflictKeepRemoteNativeResult)(object)value, existing.Start, first, content);
            }
            await RequireKeepRemoteFileStartRootAsync(first, transaction, token).ConfigureAwait(false);
            if (!SameKeepRemoteSnapshot(first.Original, await ReadNamespaceMemberProtectionSnapshotCoreAsync(first.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false)) ||
                !SameKeepRemoteSnapshot(first.Parent, await ReadNamespaceMemberProtectionSnapshotCoreAsync(first.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false)))
                throw new InvalidOperationException("The original member or parent changed before recording native work.");
            int ordinal = attempts.Sum(attempt => attempt.Result is null ? 1 : 2) + 1;
            if (ordinal > 512) throw new InvalidOperationException("Native attempts exceeded the retained execution bound.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_native_facts(command_id,ordinal,attempt_id,kind,action_id,payload,fingerprint)
                VALUES($command,$ordinal,$attempt,$kind,$action,$payload,$hash);
                INSERT INTO conflict_keep_remote_native_owners(command_id,ordinal,attempt_id,kind,action_id)
                VALUES($command,$ordinal,$attempt,$kind,$action);
                """;
            insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$attempt", attemptId.ToString("D")); insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$action", actionId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteNativeAttempt>> ReadKeepRemoteNativeAttemptsCoreAsync(
        Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        var attempts = new List<MirrorPulseConflictKeepRemoteNativeAttempt>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT f.ordinal,f.attempt_id,f.kind,f.action_id,f.payload,f.fingerprint,o.attempt_id,o.kind,o.action_id
                FROM conflict_keep_remote_native_facts f LEFT JOIN conflict_keep_remote_native_owners o
                ON o.command_id=f.command_id AND o.ordinal=f.ordinal WHERE f.command_id=$command
                UNION ALL
                SELECT o.ordinal,f.attempt_id,f.kind,f.action_id,f.payload,f.fingerprint,o.attempt_id,o.kind,o.action_id
                FROM conflict_keep_remote_native_owners o LEFT JOIN conflict_keep_remote_native_facts f
                ON f.command_id=o.command_id AND f.ordinal=o.ordinal WHERE o.command_id=$command AND f.command_id IS NULL
                ORDER BY 1 LIMIT 513;
                """;
            query.Parameters.AddWithValue("$command", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            int ordinal = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (++ordinal > 512 || Enumerable.Range(0, 9).Any(reader.IsDBNull) || reader.GetInt32(0) != ordinal ||
                    reader.GetString(1) != reader.GetString(6) || reader.GetInt32(2) != reader.GetInt32(7) || reader.GetString(3) != reader.GetString(8))
                    throw new InvalidDataException("KeepRemote native history lost its order or original ownership.");
                byte[] payload = (byte[])reader[4]; byte[] hash = (byte[])reader[5];
                if (payload.Length > 262_144 || !SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("The original native confirmation fingerprint changed.");
                try
                {
                    if (reader.GetInt32(2) == 1)
                    {
                        var start = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteNativeStart>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The native start is empty.");
                        RequireOwner(start.CommandId, start.ActionId, start.AttemptId, start);
                        if (attempts.Any(attempt => attempt.Start.AttemptId == start.AttemptId))
                            throw new InvalidDataException("The original native attempt was repeated.");
                        attempts.Add(new(start, null));
                    }
                    else if (reader.GetInt32(2) == 2 && attempts.Count > 0 && attempts[^1].Result is null)
                    {
                        var result = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteNativeResult>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The native result is empty.");
                        RequireOwner(result.CommandId, result.ActionId, result.AttemptId, result);
                        if (result.AttemptId != attempts[^1].Start.AttemptId) throw new InvalidDataException("The native result lost its preceding attempt.");
                        attempts[^1] = attempts[^1] with { Result = result };
                    }
                    else throw new InvalidDataException("A native result lost its original before-native start.");

                    void RequireOwner<T>(Guid command, Guid action, Guid attempt, T value)
                    {
                        if (command != commandId || action.ToString("D") != reader.GetString(3) || attempt.ToString("D") != reader.GetString(1) ||
                            !KeepRemoteContentFactPayload(value).AsSpan().SequenceEqual(payload))
                            throw new InvalidDataException("The original native boundary changed its canonical owner.");
                    }
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    throw new InvalidDataException("The retained native boundary is invalid.", exception);
                }
            }
        }
        if (attempts.Count == 0) return attempts;
        var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Native confirmation lost its original file start.");
        var content = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Native confirmation lost its original content action.");
        try
        {
            for (int i = 0; i < attempts.Count; i++)
            {
                RequireKeepRemoteNativeStart(attempts[i].Start, first, content, attempts.Take(i).ToArray());
                if (attempts.Take(i).Any(attempt => attempt.Result?.Receipt.MayAcknowledge == true))
                    throw new InvalidOperationException("Native work continued after a completed historical confirmation.");
                if (attempts[i].Result is { } result) RequireKeepRemoteNativeResult(result, attempts[i].Start, first, content);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The retained native boundaries disagree with their original file and selected bytes.", exception);
        }
        return attempts;
    }

    private static void RequireKeepRemoteNativeStart(MirrorPulseConflictKeepRemoteNativeStart value,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteFileContent content,
        IReadOnlyList<MirrorPulseConflictKeepRemoteNativeAttempt> previous)
    {
        var complete = content.Completion ?? throw new InvalidOperationException("Native work requires complete durable selected bytes.");
        if (value.Version != 1 || value.CommandId != first.CommandId || value.ActionId != content.Action.ActionId ||
            value.AttemptId == Guid.Empty || value.AttemptId == value.CommandId || value.AttemptId == value.ActionId ||
            value.StartedAt < complete.VerifiedAt || previous.Count > 0 && value.StartedAt < (previous[^1].Result?.RecordedAt ?? previous[^1].Start.StartedAt) ||
            previous.Count > 0 && value.SelectedPlaceholderIdentity != previous[0].Start.SelectedPlaceholderIdentity ||
            value.ObservedPlaceholderIdentity != complete.OriginalPlaceholderIdentity &&
                (previous.Count == 0 || value.ObservedPlaceholderIdentity != value.SelectedPlaceholderIdentity))
            throw new InvalidOperationException("Native confirmation requires the original completed content, action and retained identity history.");
        RequireKeepRemoteNativeIdentity(value.SelectedPlaceholderIdentity);
        RequireKeepRemoteSealedVerification(value.Child, content.Action.LocalObject, content.Action.OwnerSid,
            content.Action.RoleOnlyDacl, complete.VerifiedAt, value.StartedAt);
        RequireKeepRemoteSealedVerification(value.Parent, first.Parent.Context.Member.LocalObject, first.Parent.Context.Member.OwnerSid,
            first.Parent.ExpectedDacl, complete.VerifiedAt, value.StartedAt);
    }

    private static void RequireKeepRemoteNativeResult(MirrorPulseConflictKeepRemoteNativeResult value,
        MirrorPulseConflictKeepRemoteNativeStart start, MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteFileContent content)
    {
        var receipt = value.Receipt ?? throw new ArgumentException("The actual SDK result is required.");
        if (value.Version != 1 || value.CommandId != start.CommandId || value.ActionId != start.ActionId || value.AttemptId != start.AttemptId ||
            value.RecordedAt < receipt.ObservedAt || receipt.ObservedAt < start.StartedAt || !Enum.IsDefined(receipt.Outcome) ||
            !Enum.IsDefined(receipt.Stage) || !Enum.IsDefined(receipt.NativeStage) || receipt.BytesVerified < 0 || receipt.SegmentsRead < 0 ||
            // The SDK rereads after preparing identity; BytesVerified is cumulative work,
            // rather than the selected file length. Its verified flag proves the full hash.
            receipt.MayAcknowledge && (receipt.BytesVerified < content.Completion!.Copy.Length || receipt.ObservedInSync != true ||
                receipt.Stage != MirrorPulseContentConfirmationStage.Complete || receipt.ObservedPlaceholderIdentity != start.SelectedPlaceholderIdentity))
            throw new InvalidOperationException("The native result must retain the actual request, complete selected bytes and native projection observation.");
        if (receipt.ObservedPlaceholderIdentity is { } identity) RequireKeepRemoteNativeIdentity(identity);
        RequireKeepRemoteSealedVerification(value.Child, content.Action.LocalObject, content.Action.OwnerSid,
            content.Action.RoleOnlyDacl, start.StartedAt, value.RecordedAt);
        RequireKeepRemoteSealedVerification(value.Parent, first.Parent.Context.Member.LocalObject, first.Parent.Context.Member.OwnerSid,
            first.Parent.ExpectedDacl, start.StartedAt, value.RecordedAt);
    }

    private static void RequireKeepRemoteNativeIdentity(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length is 0 or > 4096 || Convert.ToBase64String(bytes) != value)
                throw new ArgumentException("A canonical native placeholder identity is required.");
        }
        catch (FormatException exception) { throw new ArgumentException("The retained native identity is invalid.", exception); }
    }
}
