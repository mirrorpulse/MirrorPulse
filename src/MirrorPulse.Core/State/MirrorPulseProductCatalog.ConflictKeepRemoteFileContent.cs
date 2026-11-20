using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The original object and parent observations retained before resident bytes can change.</summary>
public sealed record MirrorPulseConflictKeepRemoteFileWriteStart(int Version, Guid CommandId, Guid ActionId,
    MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification Parent,
    uint LinkCount, string OriginalPlaceholderIdentity, DateTimeOffset StartedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleRecoveryId { get; init; }
}

/// <summary>Complete flushed private-version bytes independently read back under the original content seal.</summary>
public sealed record MirrorPulseConflictKeepRemoteFileContentCompletion(int Version, Guid CommandId, Guid ActionId,
    MirrorPulseConflictKeepBothCopy Copy, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification Parent, uint LinkCount, string OriginalPlaceholderIdentity, DateTimeOffset VerifiedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleRecoveryId { get; init; }
}

/// <summary>Retained content boundaries for one original file application.</summary>
public sealed record MirrorPulseConflictKeepRemoteFileContent(MirrorPulseConflictKeepRemoteFileAction Action,
    MirrorPulseConflictKeepRemoteFileWriteStart? WriteStart, MirrorPulseConflictKeepRemoteFileContentCompletion? Completion);

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseConflictKeepRemoteFileWriteStart> RecordConflictKeepRemoteFileWriteStartAsync(
        MirrorPulseConflictKeepRemoteFileWriteStart start, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return RecordKeepRemoteContentFactAsync(start.CommandId, start.ActionId, 1, start, token);
    }

    public Task<MirrorPulseConflictKeepRemoteFileContentCompletion> RecordConflictKeepRemoteFileContentCompletionAsync(
        MirrorPulseConflictKeepRemoteFileContentCompletion completion, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return RecordKeepRemoteContentFactAsync(completion.CommandId, completion.ActionId, 2, completion, token);
    }

    public async Task<MirrorPulseConflictKeepRemoteFileContent?> ReadConflictKeepRemoteFileContentAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RecordKeepRemoteContentFactAsync<T>(Guid commandId, Guid actionId, int kind, T value, CancellationToken token)
        where T : class
    {
        if (commandId == Guid.Empty || actionId == Guid.Empty)
            throw new ArgumentException("The original command and permission action are required.");
        byte[] payload = KeepRemoteContentFactPayload(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var facts = await ReadKeepRemoteFileContentCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original KeepRemote permission action must precede content work.");
            object? retained = kind == 1 ? facts.WriteStart : facts.Completion;
            if (retained is not null)
            {
                if (!KeepRemoteContentFactPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("The first KeepRemote content boundary is immutable.");
                transaction.Commit(); return (T)retained;
            }
            var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original KeepRemote file start disappeared.");
            var preparation = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original selected KeepRemote copies disappeared.");
            var roles = await ReadKeepRemoteRoleAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (kind == 1)
            {
                var start = (MirrorPulseConflictKeepRemoteFileWriteStart)(object)value;
                RequireCurrentKeepRemoteRole(roles, start.RoleRecoveryId);
                RequireKeepRemoteWriteStart(start, first, facts.Action, preparation, roles);
            }
            else
            {
                var completion = (MirrorPulseConflictKeepRemoteFileContentCompletion)(object)value;
                RequireCurrentKeepRemoteRole(roles, completion.RoleRecoveryId);
                RequireKeepRemoteContentCompletion(completion, first, facts, preparation, roles);
            }
            await RequireKeepRemoteFileStartRootAsync(first, transaction, token).ConfigureAwait(false);
            var member = await ReadNamespaceMemberProtectionSnapshotCoreAsync(first.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            var parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(first.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            if (!SameKeepRemoteSnapshot(first.Original, member) || !SameKeepRemoteSnapshot(first.Parent, parent))
                throw new InvalidOperationException("The original member or direct parent changed during resident content work.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_content_facts(command_id,kind,action_id,payload,fingerprint) VALUES($command,$kind,$action,$payload,$hash);
                INSERT INTO conflict_keep_remote_content_owners(command_id,kind,action_id) VALUES($command,$kind,$action);
                """;
            insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$action", actionId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemoteFileContent?> ReadKeepRemoteFileContentCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var facts = await ReadKeepRemoteFileContentFactsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        if (facts?.WriteStart is not { } start) return facts;
        var first = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("KeepRemote content history lost its original file start.");
        var preparation = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("KeepRemote content history lost its original selected copies.");
        var roles = await ReadKeepRemoteRoleAttemptsCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        try
        {
            RequireKeepRemoteWriteStart(start, first, facts.Action, preparation, roles);
            if (facts.Completion is { } completion) RequireKeepRemoteContentCompletion(completion, first, facts, preparation, roles);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The retained KeepRemote content boundaries disagree.", exception);
        }
        return facts;
    }

    private async Task<MirrorPulseConflictKeepRemoteFileContent?> ReadKeepRemoteFileContentFactsCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var action = await ReadKeepRemoteFileActionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        var start = await ReadKeepRemoteContentFactCoreAsync<MirrorPulseConflictKeepRemoteFileWriteStart>(commandId, 1, transaction, token).ConfigureAwait(false);
        var completion = await ReadKeepRemoteContentFactCoreAsync<MirrorPulseConflictKeepRemoteFileContentCompletion>(commandId, 2, transaction, token).ConfigureAwait(false);
        if (action is null)
        {
            if (start is not null || completion is not null) throw new InvalidDataException("KeepRemote content history lost its original permission action.");
            return null;
        }
        if (start is not null && start.ActionId != action.ActionId ||
            completion is not null && (start is null || completion.ActionId != action.ActionId))
            throw new InvalidDataException("The retained KeepRemote content boundaries lost their first action or write start.");
        return new(action, start, completion);
    }

    private async Task<T?> ReadKeepRemoteContentFactCoreAsync<T>(Guid commandId, int kind, SqliteTransaction transaction, CancellationToken token)
        where T : class
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT f.action_id,f.payload,f.fingerprint,o.action_id FROM conflict_keep_remote_content_facts f
            LEFT JOIN conflict_keep_remote_content_owners o ON o.command_id=f.command_id AND o.kind=f.kind
            WHERE f.command_id=$command AND f.kind=$kind
            UNION ALL
            SELECT f.action_id,f.payload,f.fingerprint,o.action_id FROM conflict_keep_remote_content_owners o
            LEFT JOIN conflict_keep_remote_content_facts f ON f.command_id=o.command_id AND f.kind=o.kind
            WHERE o.command_id=$command AND o.kind=$kind AND f.command_id IS NULL;
            """;
        query.Parameters.AddWithValue("$command", commandId.ToString("D")); query.Parameters.AddWithValue("$kind", kind);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (Enumerable.Range(0, 4).Any(reader.IsDBNull) || reader.GetString(0) != reader.GetString(3))
            throw new InvalidDataException("The original KeepRemote content boundary lost its ownership row.");
        byte[] payload = (byte[])reader[1]; byte[] hash = (byte[])reader[2];
        if (payload.Length > 262_144 || !SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("The original KeepRemote content boundary fingerprint changed.");
        try
        {
            T value = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original KeepRemote content boundary is empty.");
            Guid owner = value switch
            {
                MirrorPulseConflictKeepRemoteFileWriteStart write when write.CommandId == commandId => write.ActionId,
                MirrorPulseConflictKeepRemoteFileContentCompletion complete when complete.CommandId == commandId => complete.ActionId,
                _ => throw new InvalidDataException("The original KeepRemote content boundary changed its command."),
            };
            if (owner.ToString("D") != reader.GetString(0) || !KeepRemoteContentFactPayload(value).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original KeepRemote content boundary changed its canonical owner.");
            return value;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The original KeepRemote content boundary is invalid.", exception);
        }
    }

    private static void RequireKeepRemoteWriteStart(MirrorPulseConflictKeepRemoteFileWriteStart value,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteFileAction firstAction,
        MirrorPulseConflictKeepRemotePreparation preparation, IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> roles)
    {
        var (action, handoff) = RequireKeepRemoteRoleAt(firstAction, roles, value.RoleRecoveryId, value.StartedAt);
        var earliest = handoff?.RecordedAt ?? firstAction.PreparedAt;
        if (value.Version != 1 || value.CommandId != first.CommandId || value.ActionId != firstAction.ActionId ||
            value.LinkCount != 1 || value.StartedAt < earliest || preparation.LocalPreservation?.Copy is null ||
            preparation.RemoteCopy is null || value.OriginalPlaceholderIdentity is not { Length: > 0 and <= 5464 } ||
            handoff is not null && handoff.PlaceholderIdentity != value.OriginalPlaceholderIdentity ||
            value.OriginalPlaceholderIdentity != preparation.Intent.LocalContent?.UploadBinding.PreviousPlaceholderIdentity)
            throw new InvalidOperationException("Resident writing requires the first action, both original copies and unchanged placeholder identity.");
        RequireKeepRemoteSealedVerification(value.Child, action.LocalObject, action.OwnerSid, action.RoleOnlyDacl, earliest, value.StartedAt);
        RequireKeepRemoteSealedVerification(value.Parent, first.Parent.Context.Member.LocalObject,
            first.Parent.Context.Member.OwnerSid, first.Parent.ExpectedDacl, earliest, value.StartedAt);
    }

    private static void RequireKeepRemoteContentCompletion(MirrorPulseConflictKeepRemoteFileContentCompletion value,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteFileContent facts,
        MirrorPulseConflictKeepRemotePreparation preparation, IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> roles)
    {
        var write = facts.WriteStart ?? throw new InvalidOperationException("Complete resident bytes require the original before-write fact.");
        var (action, handoff) = RequireKeepRemoteRoleAt(facts.Action, roles, value.RoleRecoveryId, value.VerifiedAt);
        var earliest = handoff is not null && handoff.RecordedAt > write.StartedAt ? handoff.RecordedAt : write.StartedAt;
        if (value.Version != 1 || value.CommandId != first.CommandId || value.ActionId != facts.Action.ActionId ||
            value.Copy is null || value.Copy != preparation.RemoteCopy || value.LinkCount != 1 ||
            value.OriginalPlaceholderIdentity != write.OriginalPlaceholderIdentity || value.VerifiedAt < earliest ||
            handoff is not null && handoff.PlaceholderIdentity != value.OriginalPlaceholderIdentity)
            throw new InvalidOperationException("Complete resident bytes require the original selected copy, file and permission action.");
        RequireKeepRemoteSealedVerification(value.Child, action.LocalObject, action.OwnerSid,
            action.RoleOnlyDacl, earliest, value.VerifiedAt);
        RequireKeepRemoteSealedVerification(value.Parent, first.Parent.Context.Member.LocalObject,
            first.Parent.Context.Member.OwnerSid, first.Parent.ExpectedDacl, earliest, value.VerifiedAt);
    }

    private static void RequireKeepRemoteSealedVerification(MirrorPulseNamespacePermissionVerification verification,
        MirrorPulseLocalFileBinding binding, string owner, string descriptor, DateTimeOffset earliest, DateTimeOffset latest)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ValidatePermissionBinding(verification.LocalObject);
        ValidatePermissionSid(verification.OwnerSid, role: false);
        ValidatePermissionDacl(verification.Dacl);
        if (verification.LocalObject != binding || verification.OwnerSid != owner ||
            verification.ObservedAt < earliest || verification.ObservedAt > latest ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(descriptor, verification.Dacl))
            throw new InvalidOperationException("Resident KeepRemote content requires its freshly observed original object, owner and permissions.");
    }

    private static byte[] KeepRemoteContentFactPayload<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 262_144) throw new ArgumentException("The KeepRemote content boundary exceeds its retained bound.", nameof(value));
        return payload;
    }
}
