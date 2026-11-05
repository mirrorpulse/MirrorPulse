using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Records the next role and the original observations before a KeepRemote metadata handoff.</summary>
public sealed record MirrorPulseConflictKeepRemoteRoleStart(int Version, Guid CommandId, Guid FirstActionId,
    Guid? PreviousRecoveryId, MirrorPulseConflictKeepRemoteFileAction NextAction, string PlaceholderIdentity,
    MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification Parent, uint LinkCount);

/// <summary>Records independent same-object readback after the retained role handoff.</summary>
public sealed record MirrorPulseConflictKeepRemoteRoleResult(int Version, Guid CommandId, Guid FirstActionId,
    Guid RecoveryId, string PlaceholderIdentity, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification Parent, uint LinkCount, DateTimeOffset RecordedAt);

public sealed record MirrorPulseConflictKeepRemoteRoleAttempt(MirrorPulseConflictKeepRemoteRoleStart Start,
    MirrorPulseConflictKeepRemoteRoleResult? Result);

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseConflictKeepRemoteRoleStart> RecordConflictKeepRemoteRoleStartAsync(
        MirrorPulseConflictKeepRemoteRoleStart start, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(start.NextAction);
        return RecordKeepRemoteRoleFactAsync(start.CommandId, start.FirstActionId, start.NextAction.ActionId, 1, start, token);
    }

    public Task<MirrorPulseConflictKeepRemoteRoleResult> RecordConflictKeepRemoteRoleResultAsync(
        MirrorPulseConflictKeepRemoteRoleResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RecordKeepRemoteRoleFactAsync(result.CommandId, result.FirstActionId, result.RecoveryId, 2, result, token);
    }

    public async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt>> ReadConflictKeepRemoteRoleAttemptsAsync(
        Guid commandId, CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var attempts = await ReadKeepRemoteRoleAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return attempts;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RecordKeepRemoteRoleFactAsync<T>(Guid commandId, Guid firstActionId, Guid recoveryId,
        int kind, T value, CancellationToken token) where T : class
    {
        if (commandId == Guid.Empty || firstActionId == Guid.Empty || recoveryId == Guid.Empty ||
            commandId == firstActionId || recoveryId == commandId || recoveryId == firstActionId)
            throw new ArgumentException("Role recovery requires distinct command, first action and recovery IDs.");
        byte[] payload = KeepRemoteContentFactPayload(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var attempts = await ReadKeepRemoteRoleAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var existing = attempts.SingleOrDefault(attempt => attempt.Start.NextAction.ActionId == recoveryId);
            object? retained = kind == 1 ? existing?.Start : existing?.Result;
            if (retained is not null)
            {
                if (!KeepRemoteContentFactPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("A retained role recovery fact is immutable.");
                transaction.Commit();
                return (T)retained;
            }

            var authority = await ReadKeepRemoteRoleAuthorityCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            _ = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            _ = await ReadKeepRemoteNativeAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var restore = await ReadKeepRemoteRestoreAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (restore.Any(attempt => attempt.Result?.ProductVerified == true) ||
                restore.Count > 0 && restore[^1].Result is { Receipt.Drained: false })
                throw new InvalidOperationException("A restored or undrained file cannot enter a new role handoff.");
            await RequireKeepRemoteFileStartRootAsync(authority.First, transaction, token).ConfigureAwait(false);
            if (!SameKeepRemoteSnapshot(authority.First.Original, await ReadNamespaceMemberProtectionSnapshotCoreAsync(
                    authority.First.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false)) ||
                !SameKeepRemoteSnapshot(authority.First.Parent, await ReadNamespaceMemberProtectionSnapshotCoreAsync(
                    authority.First.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false)))
                throw new InvalidOperationException("The original member or direct parent changed before role recovery.");

            if (kind == 1)
            {
                var start = (MirrorPulseConflictKeepRemoteRoleStart)(object)value;
                var at = start.NextAction.PreparedAt;
                if (authority.Content.WriteStart?.StartedAt >= at || authority.Content.Completion?.VerifiedAt >= at ||
                    authority.Native.Any(attempt => attempt.Start.StartedAt >= at || attempt.Result?.RecordedAt >= at) ||
                    restore.Any(attempt => attempt.Start.StartedAt >= at || attempt.Result?.RecordedAt >= at))
                    throw new InvalidOperationException("A new handoff must follow already recorded KeepRemote work.");
                RequireKeepRemoteRoleStart(start, authority, attempts);
            }
            else
            {
                if (existing is null || attempts[^1] != existing)
                    throw new InvalidOperationException("A handoff result requires the latest original before-handoff record.");
                RequireKeepRemoteRoleResult((MirrorPulseConflictKeepRemoteRoleResult)(object)value, existing.Start, authority);
            }

            int ordinal = attempts.Sum(attempt => attempt.Result is null ? 1 : 2) + 1;
            if (ordinal > 512) throw new InvalidOperationException("Role recovery exceeded the retained history bound.");
            await using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_role_facts(command_id,ordinal,recovery_id,kind,action_id,payload,fingerprint)
                VALUES($command,$ordinal,$recovery,$kind,$action,$payload,$hash);
                INSERT INTO conflict_keep_remote_role_owners(command_id,ordinal,recovery_id,kind,action_id)
                VALUES($command,$ordinal,$recovery,$kind,$action);
                """;
            insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$recovery", recoveryId.ToString("D"));
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$action", firstActionId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt>> ReadKeepRemoteRoleAttemptsCoreAsync(
        Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        var attempts = new List<MirrorPulseConflictKeepRemoteRoleAttempt>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT f.ordinal,f.recovery_id,f.kind,f.action_id,f.payload,f.fingerprint,o.recovery_id,o.kind,o.action_id
                FROM conflict_keep_remote_role_facts f LEFT JOIN conflict_keep_remote_role_owners o
                ON o.command_id=f.command_id AND o.ordinal=f.ordinal WHERE f.command_id=$command
                UNION ALL
                SELECT o.ordinal,f.recovery_id,f.kind,f.action_id,f.payload,f.fingerprint,o.recovery_id,o.kind,o.action_id
                FROM conflict_keep_remote_role_owners o LEFT JOIN conflict_keep_remote_role_facts f
                ON f.command_id=o.command_id AND f.ordinal=o.ordinal WHERE o.command_id=$command AND f.command_id IS NULL
                ORDER BY 1 LIMIT 513;
                """;
            query.Parameters.AddWithValue("$command", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            int ordinal = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (++ordinal > 512 || Enumerable.Range(0, 9).Any(reader.IsDBNull) || reader.GetInt32(0) != ordinal ||
                    reader.GetString(1) != reader.GetString(6) || reader.GetInt32(2) != reader.GetInt32(7) ||
                    reader.GetString(3) != reader.GetString(8))
                    throw new InvalidDataException("Role recovery lost its ordered facts or original ownership.");
                byte[] payload = (byte[])reader[4];
                if (payload.Length > 262_144 || !SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[5]))
                    throw new InvalidDataException("The retained role recovery fingerprint changed.");
                try
                {
                    if (reader.GetInt32(2) == 1)
                    {
                        var start = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteRoleStart>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The original role recovery intent is empty.");
                        RequireOwner(start.CommandId, start.FirstActionId, start.NextAction?.ActionId, start);
                        attempts.Add(new(start, null));
                    }
                    else if (reader.GetInt32(2) == 2 && attempts.Count > 0 && attempts[^1].Result is null)
                    {
                        var result = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteRoleResult>(payload, TopologyJsonOptions)
                            ?? throw new InvalidDataException("The original role handoff result is empty.");
                        RequireOwner(result.CommandId, result.FirstActionId, result.RecoveryId, result);
                        if (result.RecoveryId != attempts[^1].Start.NextAction.ActionId)
                            throw new InvalidDataException("The role handoff result lost its preceding intent.");
                        attempts[^1] = attempts[^1] with { Result = result };
                    }
                    else throw new InvalidDataException("A role handoff result has no original intent.");

                    void RequireOwner<TFact>(Guid command, Guid action, Guid? recovery, TFact fact)
                    {
                        if (command != commandId || action.ToString("D") != reader.GetString(3) ||
                            recovery?.ToString("D") != reader.GetString(1) || !KeepRemoteContentFactPayload(fact).AsSpan().SequenceEqual(payload))
                            throw new InvalidDataException("The role recovery payload and its canonical owners disagree.");
                    }
                }
                catch (Exception error) when (error is ArgumentException or JsonException)
                { throw new InvalidDataException("The retained role recovery payload is invalid.", error); }
            }
        }
        if (attempts.Count == 0) return attempts;
        try
        {
            var authority = await ReadKeepRemoteRoleAuthorityCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            var previous = new List<MirrorPulseConflictKeepRemoteRoleAttempt>();
            foreach (var attempt in attempts)
            {
                RequireKeepRemoteRoleStart(attempt.Start, authority, previous);
                if (attempt.Result is { } result) RequireKeepRemoteRoleResult(result, attempt.Start, authority);
                previous.Add(attempt);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("The retained role recovery history lost its original file authority.", error); }
        return attempts.AsReadOnly();
    }

    private sealed record KeepRemoteRoleAuthority(MirrorPulseConflictKeepRemoteFileStart First,
        MirrorPulseConflictKeepRemoteFileContent Content, string OriginalIdentity,
        IReadOnlyList<MirrorPulseConflictKeepRemoteNativeAttempt> Native,
        IReadOnlyList<MirrorPulseConflictKeepRemoteRestoreAttempt> Restore);

    private async Task<KeepRemoteRoleAuthority> ReadKeepRemoteRoleAuthorityCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Role recovery requires the original resident file start.");
        // The phase readers validate against the role history. Here use their ordered,
        // owner-bound facts to select the phase that was present at each handoff.
        var content = await ReadKeepRemoteFileContentFactsCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Role recovery requires the original permission action.");
        var preparation = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        var original = preparation?.Intent.LocalContent?.UploadBinding.PreviousPlaceholderIdentity
            ?? throw new InvalidOperationException("Role recovery requires the original placeholder identity.");
        var native = await ReadKeepRemoteNativeFactsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        var restore = await ReadKeepRemoteRestoreFactsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        return new(first, content, original, native, restore);
    }

    private static void RequireKeepRemoteRoleStart(MirrorPulseConflictKeepRemoteRoleStart value, KeepRemoteRoleAuthority authority,
        IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> previous)
    {
        var first = authority.Content.Action;
        var next = value.NextAction;
        ArgumentNullException.ThrowIfNull(value.Child);
        ValidateKeepRemoteFileAction(next);
        var head = previous.Count == 0 ? null : previous[^1];
        if (value.Version != 1 || value.CommandId != first.CommandId || value.FirstActionId != first.ActionId ||
            next.CommandId != value.CommandId || next.ActionId == first.ActionId || next.LocalObject != first.LocalObject ||
            next.OwnerSid != first.OwnerSid || value.LinkCount != 1 || value.PreviousRecoveryId != head?.Start.NextAction.ActionId ||
            next.PreparedAt < (head?.Result?.RecordedAt ?? head?.Start.NextAction.PreparedAt ?? first.PreparedAt) ||
            next.RoleSid == first.RoleSid || previous.Any(attempt => attempt.Start.NextAction.RoleSid == next.RoleSid ||
                attempt.Start.NextAction.ActionId == next.ActionId))
            throw new InvalidOperationException("A role handoff must extend the original object's current recovery chain with a new role.");

        var nativeBefore = authority.Native.Where(attempt => attempt.Start.StartedAt <= next.PreparedAt).ToArray();
        var confirmed = nativeBefore.SingleOrDefault(attempt => attempt.Result is { Receipt.MayAcknowledge: true } result && result.RecordedAt <= next.PreparedAt);
        bool selectedBefore = nativeBefore.Any(attempt => attempt.Start.SelectedPlaceholderIdentity == value.PlaceholderIdentity);
        RequireKeepRemoteNativeIdentity(value.PlaceholderIdentity);
        if (confirmed is not null ? value.PlaceholderIdentity != confirmed.Start.SelectedPlaceholderIdentity :
            value.PlaceholderIdentity != authority.OriginalIdentity && !selectedBefore)
            throw new InvalidOperationException("Role recovery requires the original or durably selected native identity.");
        var restoreBefore = authority.Restore.Where(attempt => attempt.Start.StartedAt <= next.PreparedAt).ToArray();
        if (restoreBefore.Any(attempt => attempt.Result is { ProductVerified: true } result && result.RecordedAt <= next.PreparedAt) ||
            restoreBefore.Length > 0 && restoreBefore[^1].Result is { Receipt.Drained: false } undrained && undrained.RecordedAt <= next.PreparedAt)
            throw new InvalidOperationException("Role recovery cannot reseal completed or undrained access restoration.");

        bool beforeFirstWrite = authority.Content.WriteStart is null || authority.Content.WriteStart.StartedAt > next.PreparedAt;
        var known = previous.Select(attempt => attempt.Start.NextAction).Prepend(first);
        var owned = known.FirstOrDefault(action => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, value.Child.Dacl) ||
            (beforeFirstWrite || restoreBefore.Length > 0) && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, value.Child.Dacl));
        if (owned is null) throw new InvalidOperationException("Role recovery requires a descriptor retained by the original action chain.");
        string descriptor = MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(owned.RoleOnlyDacl, value.Child.Dacl)
            ? owned.RoleOnlyDacl : owned.RestoreDacl;
        RequireKeepRemoteSealedVerification(value.Child, first.LocalObject, first.OwnerSid, descriptor, first.PreparedAt, next.PreparedAt);
        RequireKeepRemoteRoleParent(value.Parent, authority, first.PreparedAt, next.PreparedAt);
    }

    private static void RequireKeepRemoteRoleResult(MirrorPulseConflictKeepRemoteRoleResult value, MirrorPulseConflictKeepRemoteRoleStart start,
        KeepRemoteRoleAuthority authority)
    {
        var next = start.NextAction;
        if (value.Version != 1 || value.CommandId != start.CommandId || value.FirstActionId != start.FirstActionId ||
            value.RecoveryId != next.ActionId || value.PlaceholderIdentity != start.PlaceholderIdentity || value.LinkCount != 1 ||
            value.RecordedAt < next.PreparedAt)
            throw new InvalidOperationException("A role handoff must retain the original object and exact before-handoff identity.");
        RequireKeepRemoteSealedVerification(value.Child, next.LocalObject, next.OwnerSid, next.RoleOnlyDacl, next.PreparedAt, value.RecordedAt);
        RequireKeepRemoteRoleParent(value.Parent, authority, next.PreparedAt, value.RecordedAt);
    }

    private static void RequireKeepRemoteRoleParent(MirrorPulseNamespacePermissionVerification parent, KeepRemoteRoleAuthority authority,
        DateTimeOffset earliest, DateTimeOffset latest) =>
        RequireKeepRemoteSealedVerification(parent, authority.First.Parent.Context.Member.LocalObject,
            authority.First.Parent.Context.Member.OwnerSid, authority.First.Parent.ExpectedDacl, earliest, latest);

    private static (MirrorPulseConflictKeepRemoteFileAction Action, MirrorPulseConflictKeepRemoteRoleResult? Handoff) RequireKeepRemoteRoleAt(
        MirrorPulseConflictKeepRemoteFileAction first, IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> roles,
        Guid? recoveryId, DateTimeOffset at)
    {
        var role = roles.LastOrDefault(attempt => attempt.Start.NextAction.PreparedAt <= at);
        if (role is null && recoveryId is null) return (first, null);
        if (role is { Result: { } result } && result.RecordedAt <= at && result.RecoveryId == recoveryId)
            return (role.Start.NextAction, result);
        throw new InvalidOperationException("The phase requires the verified KeepRemote role at the time of the observation.");
    }

    private static void RequireCurrentKeepRemoteRole(IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> roles, Guid? recoveryId)
    {
        if (roles.Count == 0 ? recoveryId is not null : roles[^1].Result is not { } current || current.RecoveryId != recoveryId)
            throw new InvalidOperationException("New KeepRemote facts require the current verified role handoff.");
    }
}
