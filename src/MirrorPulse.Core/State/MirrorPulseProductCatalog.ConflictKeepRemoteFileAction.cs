using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Immutable before-write permission intent for the original resident KeepRemote file.</summary>
/// <remarks>
/// This is not a native application, content, restoration or journal acknowledgement receipt.
/// The Host must independently admit the exact object and parent before changing access.
/// A new execution role cannot replace this first action with a new permission policy.
/// </remarks>
public sealed record MirrorPulseConflictKeepRemoteFileAction(int Version, Guid CommandId, Guid ActionId,
    MirrorPulseLocalFileBinding LocalObject, string OwnerSid, string RoleSid, string RoleOnlyDacl,
    string RestoreDacl, DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseConflictKeepRemoteFileAction> RetainConflictKeepRemoteFileActionAsync(
        MirrorPulseConflictKeepRemoteFileAction action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateKeepRemoteFileAction(action);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var start = await ReadKeepRemoteFileStartCoreAsync(action.CommandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original KeepRemote file start is required before a permission action.");
            RequireKeepRemoteFileActionReferences(action, start);
            var previous = await ReadKeepRemoteFileActionCoreAsync(action.CommandId, transaction, token).ConfigureAwait(false);
            if (previous is not null)
            {
                if (!KeepRemoteFileActionPayload(previous).AsSpan().SequenceEqual(KeepRemoteFileActionPayload(action)))
                    throw new InvalidOperationException("The first KeepRemote permission action is immutable.");
                transaction.Commit(); return previous;
            }
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(start.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            var parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(start.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            if (!SameKeepRemoteSnapshot(start.Original, current) || !SameKeepRemoteSnapshot(start.Parent, parent))
                throw new InvalidOperationException("The original member or direct parent changed before permission action admission.");
            await RequireKeepRemoteFileStartRootAsync(start, transaction, token).ConfigureAwait(false);
            byte[] payload = KeepRemoteFileActionPayload(action);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_file_actions(command_id,action_id,payload,fingerprint) VALUES($command,$action,$payload,$hash);
                INSERT INTO conflict_keep_remote_file_action_owners(command_id,action_id) VALUES($command,$action);
                """;
            insert.Parameters.AddWithValue("$command", action.CommandId.ToString("D"));
            insert.Parameters.AddWithValue("$action", action.ActionId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return action;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the original permission intent without granting authority over today's file.</summary>
    public async Task<MirrorPulseConflictKeepRemoteFileAction?> ReadConflictKeepRemoteFileActionAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadKeepRemoteFileActionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemoteFileAction?> ReadKeepRemoteFileActionCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[]? payload = null; byte[] fingerprint = []; string? retainedAction = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT a.command_id,a.action_id,a.payload,a.fingerprint,o.command_id,o.action_id
                FROM conflict_keep_remote_file_actions a LEFT JOIN conflict_keep_remote_file_action_owners o ON o.command_id=a.command_id
                WHERE a.command_id=$id
                UNION ALL
                SELECT a.command_id,a.action_id,a.payload,a.fingerprint,o.command_id,o.action_id
                FROM conflict_keep_remote_file_action_owners o LEFT JOIN conflict_keep_remote_file_actions a ON a.command_id=o.command_id
                WHERE o.command_id=$id AND a.command_id IS NULL;
                """;
            query.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (payload is not null || Enumerable.Range(0, 6).Any(reader.IsDBNull) ||
                    reader.GetString(0) != commandId.ToString("D") || reader.GetString(4) != commandId.ToString("D") ||
                    reader.GetString(1) != reader.GetString(5))
                    throw new InvalidDataException("KeepRemote lost its original permission action ownership.");
                payload = (byte[])reader[2]; fingerprint = (byte[])reader[3]; retainedAction = reader.GetString(1);
            }
        }
        if (payload is null) return null;
        if (payload.Length > 262_144 || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The original KeepRemote permission action fingerprint changed.");
        MirrorPulseConflictKeepRemoteFileAction action;
        try
        {
            action = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteFileAction>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original KeepRemote permission action is empty.");
            ValidateKeepRemoteFileAction(action);
            if (action.CommandId != commandId || action.ActionId.ToString("D") != retainedAction ||
                !KeepRemoteFileActionPayload(action).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original KeepRemote permission action lost its canonical owner.");
            var start = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original KeepRemote permission action lost its file start.");
            RequireKeepRemoteFileActionReferences(action, start);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The original KeepRemote permission action is inconsistent.", exception);
        }
        return action;
    }

    private static void ValidateKeepRemoteFileAction(MirrorPulseConflictKeepRemoteFileAction action)
    {
        if (action.Version != 1 || action.CommandId == Guid.Empty || action.ActionId == Guid.Empty ||
            action.CommandId == action.ActionId || action.PreparedAt <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("A versioned KeepRemote permission action requires distinct command and action IDs and its first time.", nameof(action));
        ValidatePermissionBinding(action.LocalObject); ValidatePermissionSid(action.OwnerSid, role: false);
        ValidatePermissionSid(action.RoleSid, role: true); ValidatePermissionDacl(action.RoleOnlyDacl); ValidatePermissionDacl(action.RestoreDacl);
        if (action.RoleOnlyDacl == action.RestoreDacl || KeepRemoteFileActionPayload(action).Length > 262_144)
            throw new ArgumentException("KeepRemote requires distinct bounded role-only and restore descriptors.", nameof(action));
    }

    private static void RequireKeepRemoteFileActionReferences(MirrorPulseConflictKeepRemoteFileAction action,
        MirrorPulseConflictKeepRemoteFileStart start)
    {
        var original = start.Original.Context;
        if (action.CommandId != start.CommandId || action.LocalObject != original.Member.LocalObject ||
            action.OwnerSid != original.Member.OwnerSid || action.OwnerSid != original.Verification.OwnerSid ||
            action.RoleSid != original.Protection.RoleSid || action.RestoreDacl != start.Original.ExpectedDacl ||
            action.PreparedAt < start.StartedAt)
            throw new InvalidOperationException("KeepRemote permission intent requires the first object, owner, role, restore descriptor and file start.");
    }

    private static byte[] KeepRemoteFileActionPayload(MirrorPulseConflictKeepRemoteFileAction action) =>
        JsonSerializer.SerializeToUtf8Bytes(action, TopologyJsonOptions);
}
