using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Before-scope authority to restore access after the original successful native confirmation.</summary>
public sealed record MirrorPulseConflictKeepRemoteRestoreStart(int Version, Guid CommandId, Guid ActionId, Guid AttemptId,
    Guid NativeAttemptId, string ObservedPlaceholderIdentity, bool ObservedInSync,
    MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification Parent, DateTimeOffset StartedAt);

/// <summary>Actual public exclusive-file access-operation facts, separate from product permission verification.</summary>
public sealed record MirrorPulseConflictKeepRemoteRestoreReceipt(string Outcome, string Stage, MirrorPulseLocalFileBinding ExpectedObject,
    bool CallbackStarted, bool CallbackCompleted, bool AccessDescriptorApplied, bool AccessDescriptorReadBack, bool Drained,
    bool NativeIdentityPrepared, bool NativeConverted, bool DurableProjectionCommitted, int? PreparationHResult, long? PreparationUsn,
    int? ErrorHResult, bool CancellationRequested, bool BudgetExpired, DateTimeOffset ObservedAt);

/// <summary>A historical access result; it never accepts later content, acknowledges originals or releases a fence.</summary>
public sealed record MirrorPulseConflictKeepRemoteRestoreResult(int Version, Guid CommandId, Guid ActionId, Guid AttemptId,
    Guid NativeAttemptId, MirrorPulseConflictKeepRemoteRestoreReceipt Receipt, MirrorPulseNamespacePermissionVerification? Child,
    MirrorPulseNamespacePermissionVerification? Parent, uint? LinkCount, string? ObservedPlaceholderIdentity,
    bool? ObservedInSync, bool ProductVerified, DateTimeOffset RecordedAt);

public sealed record MirrorPulseConflictKeepRemoteRestoreAttempt(MirrorPulseConflictKeepRemoteRestoreStart Start,
    MirrorPulseConflictKeepRemoteRestoreResult? Result);

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseConflictKeepRemoteRestoreStart> RecordConflictKeepRemoteRestoreStartAsync(
        MirrorPulseConflictKeepRemoteRestoreStart start, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return RecordKeepRemoteRestoreFactAsync(start.CommandId, start.ActionId, start.AttemptId, start.NativeAttemptId, 1, start, token);
    }

    public Task<MirrorPulseConflictKeepRemoteRestoreResult> RecordConflictKeepRemoteRestoreResultAsync(
        MirrorPulseConflictKeepRemoteRestoreResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RecordKeepRemoteRestoreFactAsync(result.CommandId, result.ActionId, result.AttemptId, result.NativeAttemptId, 2, result, token);
    }

    public async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteRestoreAttempt>> ReadConflictKeepRemoteRestoreAttemptsAsync(
        Guid commandId, CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadKeepRemoteRestoreAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RecordKeepRemoteRestoreFactAsync<T>(Guid commandId, Guid actionId, Guid attemptId,
        Guid nativeAttemptId, int kind, T value, CancellationToken token) where T : class
    {
        if (commandId == Guid.Empty || actionId == Guid.Empty || attemptId == Guid.Empty || nativeAttemptId == Guid.Empty ||
            new[] { commandId, actionId, attemptId, nativeAttemptId }.Distinct().Count() != 4)
            throw new ArgumentException("Access restoration requires distinct original command, action, native and restoration IDs.");
        byte[] payload = KeepRemoteContentFactPayload(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var attempts = await ReadKeepRemoteRestoreAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var authority = await ReadKeepRemoteRestoreAuthorityCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var existing = attempts.SingleOrDefault(attempt => attempt.Start.AttemptId == attemptId);
            object? retained = kind == 1 ? existing?.Start : existing?.Result;
            if (retained is not null)
            {
                if (!KeepRemoteContentFactPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("The original restoration boundary is immutable.");
                transaction.Commit(); return (T)retained;
            }
            if (kind == 1)
                RequireKeepRemoteRestoreStart((MirrorPulseConflictKeepRemoteRestoreStart)(object)value, authority, attempts);
            else
            {
                if (existing is null || attempts[^1] != existing)
                    throw new InvalidOperationException("An access result requires its latest retained before-scope attempt.");
                RequireKeepRemoteRestoreResult((MirrorPulseConflictKeepRemoteRestoreResult)(object)value, existing.Start, authority);
            }
            await RequireKeepRemoteFileStartRootAsync(authority.First, transaction, token).ConfigureAwait(false);
            if (!SameKeepRemoteSnapshot(authority.First.Original, await ReadNamespaceMemberProtectionSnapshotCoreAsync(
                    authority.First.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false)) ||
                !SameKeepRemoteSnapshot(authority.First.Parent, await ReadNamespaceMemberProtectionSnapshotCoreAsync(
                    authority.First.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false)))
                throw new InvalidOperationException("The original member or parent changed before recording access restoration.");
            int ordinal = attempts.Sum(attempt => attempt.Result is null ? 1 : 2) + 1;
            if (ordinal > 512) throw new InvalidOperationException("Access restoration exceeded its retained attempt bound.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_restore_facts(command_id,ordinal,attempt_id,kind,action_id,native_attempt_id,payload,fingerprint)
                VALUES($command,$ordinal,$attempt,$kind,$action,$native,$payload,$hash);
                INSERT INTO conflict_keep_remote_restore_owners(command_id,ordinal,attempt_id,kind,action_id,native_attempt_id)
                VALUES($command,$ordinal,$attempt,$kind,$action,$native);
                """;
            insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$attempt", attemptId.ToString("D")); insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$action", actionId.ToString("D")); insert.Parameters.AddWithValue("$native", nativeAttemptId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteRestoreAttempt>> ReadKeepRemoteRestoreAttemptsCoreAsync(
        Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        var attempts = new List<MirrorPulseConflictKeepRemoteRestoreAttempt>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT f.ordinal,f.attempt_id,f.kind,f.action_id,f.native_attempt_id,f.payload,f.fingerprint,
                    o.attempt_id,o.kind,o.action_id,o.native_attempt_id
                FROM conflict_keep_remote_restore_facts f LEFT JOIN conflict_keep_remote_restore_owners o
                ON o.command_id=f.command_id AND o.ordinal=f.ordinal WHERE f.command_id=$command
                UNION ALL
                SELECT o.ordinal,f.attempt_id,f.kind,f.action_id,f.native_attempt_id,f.payload,f.fingerprint,
                    o.attempt_id,o.kind,o.action_id,o.native_attempt_id
                FROM conflict_keep_remote_restore_owners o LEFT JOIN conflict_keep_remote_restore_facts f
                ON f.command_id=o.command_id AND f.ordinal=o.ordinal WHERE o.command_id=$command AND f.command_id IS NULL
                ORDER BY 1 LIMIT 513;
                """;
            query.Parameters.AddWithValue("$command", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            int ordinal = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (++ordinal > 512 || Enumerable.Range(0, 11).Any(reader.IsDBNull) || reader.GetInt32(0) != ordinal ||
                    reader.GetString(1) != reader.GetString(7) || reader.GetInt32(2) != reader.GetInt32(8) ||
                    reader.GetString(3) != reader.GetString(9) || reader.GetString(4) != reader.GetString(10))
                    throw new InvalidDataException("Restoration lost its ordered facts or original permission/native ownership.");
                byte[] payload = (byte[])reader[5]; byte[] fingerprint = (byte[])reader[6];
                if (payload.Length > 262_144 || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
                    throw new InvalidDataException("The retained restoration fingerprint changed.");
                try
                {
                    if (reader.GetInt32(2) == 1)
                    {
                        var start = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteRestoreStart>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The original restoration intent is empty.");
                        RequireOwner(start.CommandId, start.ActionId, start.AttemptId, start.NativeAttemptId, start);
                        if (attempts.Any(attempt => attempt.Start.AttemptId == start.AttemptId))
                            throw new InvalidDataException("The original restoration attempt was repeated.");
                        attempts.Add(new(start, null));
                    }
                    else if (reader.GetInt32(2) == 2 && attempts.Count > 0 && attempts[^1].Result is null)
                    {
                        var result = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteRestoreResult>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The actual restoration result is empty.");
                        RequireOwner(result.CommandId, result.ActionId, result.AttemptId, result.NativeAttemptId, result);
                        if (result.AttemptId != attempts[^1].Start.AttemptId)
                            throw new InvalidDataException("The access result lost its preceding before-scope attempt.");
                        attempts[^1] = attempts[^1] with { Result = result };
                    }
                    else throw new InvalidDataException("The access result has no original before-scope attempt.");

                    void RequireOwner<T>(Guid command, Guid action, Guid attempt, Guid native, T value)
                    {
                        if (command != commandId || action.ToString("D") != reader.GetString(3) || attempt.ToString("D") != reader.GetString(1) ||
                            native.ToString("D") != reader.GetString(4) || !KeepRemoteContentFactPayload(value).AsSpan().SequenceEqual(payload))
                            throw new InvalidDataException("The restoration payload and its canonical owners disagree.");
                    }
                }
                catch (Exception error) when (error is ArgumentException or JsonException)
                { throw new InvalidDataException("The retained restoration payload is invalid.", error); }
            }
        }
        if (attempts.Count == 0) return attempts;
        try
        {
            var authority = await ReadKeepRemoteRestoreAuthorityCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            for (int index = 0; index < attempts.Count; index++)
            {
                RequireKeepRemoteRestoreStart(attempts[index].Start, authority, attempts.Take(index).ToArray());
                if (attempts[index].Result is { } result) RequireKeepRemoteRestoreResult(result, attempts[index].Start, authority);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("The restoration history lost its original successful native authority.", error); }
        return attempts;
    }

    private sealed record KeepRemoteRestoreAuthority(MirrorPulseConflictKeepRemoteFileStart First,
        MirrorPulseConflictKeepRemoteFileContent Content, MirrorPulseConflictKeepRemoteNativeAttempt Native);

    private async Task<KeepRemoteRestoreAuthority> ReadKeepRemoteRestoreAuthorityCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Restoring access requires the original file start.");
        var content = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Restoring access requires the original complete content.");
        var native = (await ReadKeepRemoteNativeAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false))
            .SingleOrDefault(attempt => attempt.Result?.Receipt.MayAcknowledge == true)
            ?? throw new InvalidOperationException("Restoring access requires the original successful native confirmation.");
        return new(first, content, native);
    }

    private static void RequireKeepRemoteRestoreStart(MirrorPulseConflictKeepRemoteRestoreStart value,
        KeepRemoteRestoreAuthority authority, IReadOnlyList<MirrorPulseConflictKeepRemoteRestoreAttempt> previous)
    {
        var action = authority.Content.Action;
        var native = authority.Native;
        ArgumentNullException.ThrowIfNull(value.Child);
        ArgumentNullException.ThrowIfNull(value.Parent);
        if (value.Version != 1 || value.CommandId != authority.First.CommandId || value.ActionId != action.ActionId ||
            value.NativeAttemptId != native.Start.AttemptId || value.AttemptId == Guid.Empty ||
            new[] { value.CommandId, value.ActionId, value.AttemptId, value.NativeAttemptId }.Distinct().Count() != 4 ||
            value.ObservedPlaceholderIdentity != native.Start.SelectedPlaceholderIdentity || value.StartedAt < native.Result!.RecordedAt ||
            previous.Count == 0 && !value.ObservedInSync || previous.Any(attempt => attempt.Result?.ProductVerified == true) ||
            previous.Count > 0 && (previous[^1].Result is { Receipt.Drained: false } ||
                value.StartedAt < (previous[^1].Result?.RecordedAt ?? previous[^1].Start.StartedAt)))
            throw new InvalidOperationException("Restoration requires its original successful native result and ordered, drained attempt history.");
        // A previously restored descriptor can be adopted only after this command retained a
        // before-scope attempt. Later ordinary edits are preserved, never reconfirmed here.
        string descriptor = previous.Count > 0 && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, value.Child.Dacl)
            ? action.RestoreDacl : action.RoleOnlyDacl;
        RequireKeepRemoteSealedVerification(value.Child, action.LocalObject, action.OwnerSid, descriptor,
            native.Result.RecordedAt, value.StartedAt);
        RequireKeepRemoteSealedVerification(value.Parent, authority.First.Parent.Context.Member.LocalObject,
            authority.First.Parent.Context.Member.OwnerSid, authority.First.Parent.ExpectedDacl, native.Result.RecordedAt, value.StartedAt);
    }

    private static void RequireKeepRemoteRestoreResult(MirrorPulseConflictKeepRemoteRestoreResult value,
        MirrorPulseConflictKeepRemoteRestoreStart start, KeepRemoteRestoreAuthority authority)
    {
        var receipt = value.Receipt ?? throw new ArgumentException("Retain the actual public access-operation receipt.");
        var action = authority.Content.Action;
        if (value.Version != 1 || value.CommandId != start.CommandId || value.ActionId != start.ActionId ||
            value.AttemptId != start.AttemptId || value.NativeAttemptId != start.NativeAttemptId ||
            receipt.ExpectedObject != action.LocalObject || value.RecordedAt < receipt.ObservedAt || receipt.ObservedAt < start.StartedAt ||
            receipt.Outcome is not ("Completed" or "Busy" or "Unsupported" or "LocalObjectMismatch" or "NotApplicable" or "ProtectionLost" or
                "NativeAppliedProjectionPending" or "CallbackFailed" or "Canceled" or "DeadlineExceeded" or "Failed") ||
            receipt.Stage is not ("Admission" or "Open" or "Inspection" or "NativePreparation" or "Projection" or "Callback" or "AccessDescriptor" or "Draining" or "Released") ||
            receipt.CallbackCompleted && !receipt.CallbackStarted || receipt.AccessDescriptorReadBack && !receipt.AccessDescriptorApplied)
            throw new InvalidOperationException("The access result must retain its actual original request, receipt and observation order.");
        if (value.ObservedPlaceholderIdentity is { } identity) RequireKeepRemoteNativeIdentity(identity);
        foreach (var observation in new[] { value.Child, value.Parent })
        {
            if (observation is null) continue;
            ValidatePermissionBinding(observation.LocalObject); ValidatePermissionSid(observation.OwnerSid, role: false);
            ValidatePermissionDacl(observation.Dacl);
            if (observation.ObservedAt < start.StartedAt || observation.ObservedAt > value.RecordedAt)
                throw new InvalidOperationException("A retained permission observation must belong to this actual access attempt.");
        }
        if (!value.ProductVerified) return;
        if (receipt.Outcome != "Completed" || receipt.Stage != "Released" || !receipt.Drained || !receipt.CallbackStarted ||
            !receipt.CallbackCompleted || !receipt.AccessDescriptorApplied || !receipt.AccessDescriptorReadBack || receipt.ErrorHResult is not null ||
            receipt.NativeIdentityPrepared || receipt.NativeConverted || receipt.DurableProjectionCommitted ||
            value.Child is null || value.Parent is null || value.LinkCount != 1 || value.ObservedInSync is null ||
            value.ObservedPlaceholderIdentity != start.ObservedPlaceholderIdentity)
            throw new InvalidOperationException("Verified access restoration requires the drained descriptor operation and separate same-object observations.");
        RequireKeepRemoteSealedVerification(value.Child, action.LocalObject, action.OwnerSid, action.RestoreDacl, start.StartedAt, value.RecordedAt);
        RequireKeepRemoteSealedVerification(value.Parent, authority.First.Parent.Context.Member.LocalObject,
            authority.First.Parent.Context.Member.OwnerSid, authority.First.Parent.ExpectedDacl, start.StartedAt, value.RecordedAt);
    }
}
