using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A before-write intent for an already registered born object at its committed position.</summary>
/// <remarks>Descriptors are owned targets, not proof of a native write. First birth facts and verified protection remain separate.</remarks>
public sealed record MirrorPulseNamespaceBornMemberRecoveryAction(int Version, Guid ActionId, Guid InitializationOperationId,
    Guid? PreviousActionId, MirrorPulseNamespaceMemberRecoveryContext Child,
    MirrorPulseNamespaceMemberProtectionSnapshot Parent, string TargetDacl, DateTimeOffset PreparedAt)
{
    /// <summary>The exact original unsealed Label and initializer for a version 2 action.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceBornRootLabelRecoveryOwner? RootLabelRecoveryOwner { get; init; }
}

/// <summary>References verified existing birth protection after a drained native scope; no root fence is released.</summary>
public sealed record MirrorPulseNamespaceBornMemberRecoveryCompletion(int Version, Guid ActionId, Guid ProtectionId,
    bool Drained, DateTimeOffset CompletedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceBornMemberRecoveryAction> PrepareNamespaceBornMemberRecoveryAsync(
        MirrorPulseNamespaceBornMemberRecoveryAction action, CancellationToken cancellationToken = default)
    {
        ValidateBornMemberRecoveryAction(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var history = await ReadBornMemberRecoverySummaryCoreAsync(action.Child.Member.MemberId, transaction, cancellationToken,
                selectedActionId: action.ActionId).ConfigureAwait(false);
            var replay = history.Selected;
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay, action)) throw new InvalidOperationException("The born recovery action is immutable.");
                transaction.Commit(); return replay;
            }
            if (history.Count >= 4096 || action.PreviousActionId != history.Latest?.ActionId ||
                history.Count != 0 && action.PreparedAt <= history.Latest!.PreparedAt)
                throw new InvalidOperationException("Born recovery must extend its bounded immutable action chain.");
            await RequireBornMemberRecoveryCurrentAsync(action, transaction, cancellationToken).ConfigureAwait(false);
            if (await ReadNamespaceBirthProtectionRowAsync(action.ActionId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A new recovery action cannot adopt an already observed protection epoch.");
            validation.Dispose();
            byte[] payload = NamespaceReplacementPayload(action);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_born_member_recovery_actions(action_id,member_id,initialization_id,previous_id,payload,fingerprint)
                VALUES($id,$member,$initialization,$previous,$payload,$hash);
                """;
            insert.Parameters.AddWithValue("$id", action.ActionId.ToString("D"));
            insert.Parameters.AddWithValue("$member", action.Child.Member.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$initialization", action.InitializationOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", action.PreviousActionId is { } previous ? previous.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var head = _connection.CreateCommand(); head.Transaction = transaction;
            head.CommandText = history.Count == 0 ? "INSERT INTO namespace_born_member_recovery_heads(member_id,action_id) VALUES($member,$id);" :
                "UPDATE namespace_born_member_recovery_heads SET action_id=$id WHERE member_id=$member AND action_id=$previous;";
            head.Parameters.AddWithValue("$member", action.Child.Member.MemberId.ToString("D")); head.Parameters.AddWithValue("$id", action.ActionId.ToString("D"));
            head.Parameters.AddWithValue("$previous", action.PreviousActionId is { } predecessor ? predecessor.ToString("D") : DBNull.Value);
            if (await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The born recovery action lost its current head.");
            await using var pending = _connection.CreateCommand(); pending.Transaction = transaction;
            pending.CommandText = """
                INSERT INTO namespace_born_member_recovery_pending(member_id,root_id,action_id) VALUES($member,$root,$id)
                ON CONFLICT(member_id) DO UPDATE SET action_id=excluded.action_id
                WHERE namespace_born_member_recovery_pending.action_id=$previous AND namespace_born_member_recovery_pending.root_id=excluded.root_id;
                """;
            pending.Parameters.AddWithValue("$member", action.Child.Member.MemberId.ToString("D"));
            pending.Parameters.AddWithValue("$root", action.Child.Member.RootId!.Value.Value.ToString("D"));
            pending.Parameters.AddWithValue("$id", action.ActionId.ToString("D"));
            pending.Parameters.AddWithValue("$previous", action.PreviousActionId is { } pendingPrevious ? pendingPrevious.ToString("D") : DBNull.Value);
            if (await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The born recovery action lost its pending member ownership.");
            transaction.Commit(); return action;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads retained actions without granting current native authorization.</summary>
    public async Task<IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction>> ReadNamespaceBornMemberRecoveryActionsAsync(
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadBornMemberRecoveryHistoryCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBornMemberRecoveryAction> ValidateNamespaceBornMemberRecoveryExecutionAsync(Guid actionId,
        CancellationToken cancellationToken = default)
    {
        if (actionId == Guid.Empty) throw new ArgumentException("An action ID is required.", nameof(actionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var action = await ValidateBornMemberRecoveryExecutionCoreAsync(actionId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return action;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBornMemberRecoveryAction> ValidateBornMemberRecoveryExecutionCoreAsync(Guid actionId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var action = await RequireBornMemberRecoveryActionCoreAsync(actionId, transaction, cancellationToken).ConfigureAwait(false);
        var history = await ReadBornMemberRecoverySummaryCoreAsync(action.Child.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
        if (history.Latest?.ActionId != actionId || await ReadBornMemberRecoveryCompletionCoreAsync(actionId, action, transaction, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Native execution requires the latest unfinished born recovery action.");
        await RequireBornMemberRecoveryCurrentAsync(action, transaction, cancellationToken).ConfigureAwait(false);
        return action;
    }

    private async Task RequireBornMemberRecoveryCurrentAsync(MirrorPulseNamespaceBornMemberRecoveryAction action,
        SqliteTransaction transaction, CancellationToken token)
    {
        var current = (await ReadNamespaceMemberRecoveryLineageCoreAsync(action.InitializationOperationId, action.Child.Member.MemberId, transaction, token).ConfigureAwait(false))[0];
        var parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(action.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
        if (!SameNamespaceReplacementPayload(current, action.Child) || parent is null || !SameNamespaceReplacementPayload(parent, action.Parent))
            throw new InvalidOperationException("Born recovery lost its admitted current object, position, identity or protected parent.");
        await ValidateBornRootLabelRecoveryCurrentAsync(action, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMoveRootUnfencedAsync(action.Child.Member.RootId!.Value, null, transaction, token,
            allowedBornRecoveryInitialization: action.InitializationOperationId,
            allowedRootRename: action.RootLabelRecoveryOwner?.RootLabelOperationId).ConfigureAwait(false);
    }

    private static void ValidateBornMemberRecoveryAction(MirrorPulseNamespaceBornMemberRecoveryAction action)
    {
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(action.Child); ArgumentNullException.ThrowIfNull(action.Parent);
        if (action.Version is not (1 or 2) || action.ActionId == Guid.Empty || action.InitializationOperationId == Guid.Empty ||
            action.ActionId == action.InitializationOperationId || action.PreviousActionId == Guid.Empty || action.PreviousActionId == action.ActionId ||
            action.Child.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || action.Child.BirthProtection is null ||
            action.Child.BirthObservation is null || action.Child.Original is not null || action.Child.Permission is not null ||
            action.Child.Member.RootId is null || action.Child.Identity.ItemId is null || string.IsNullOrWhiteSpace(action.Child.Identity.RemoteId) ||
            action.Child.Location.ParentMemberId != action.Parent.Context.Member.MemberId || !action.Parent.Context.Member.IsDirectory ||
            action.Child.Member.OwnerSid != action.Parent.Context.Member.OwnerSid ||
            action.PreparedAt <= action.Child.Member.RegisteredAt || action.PreparedAt <= action.Child.BirthProtection.Verification.ObservedAt ||
            action.PreparedAt <= action.Parent.Context.Protection.VerifiedAt || NamespaceReplacementPayload(action).Length > 524_288)
            throw new ArgumentException("Born recovery requires complete registered provenance, current parent and ordered before-write admission.", nameof(action));
        ValidateBornRootLabelRecoveryOwner(action);
        ValidatePermissionDacl(action.TargetDacl);
    }

    private static async Task EnsureBornMemberRecoverySchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_born_member_recovery_actions (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, action_id TEXT NOT NULL UNIQUE,
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                initialization_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
                previous_id TEXT UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE INDEX IF NOT EXISTS namespace_born_member_recovery_member ON namespace_born_member_recovery_actions(member_id,sequence);
            CREATE TABLE IF NOT EXISTS namespace_born_member_recovery_heads (
                member_id TEXT PRIMARY KEY REFERENCES namespace_members(member_id),
                action_id TEXT NOT NULL UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id));
            CREATE TABLE IF NOT EXISTS namespace_born_member_recovery_completions (
                action_id TEXT PRIMARY KEY REFERENCES namespace_born_member_recovery_actions(action_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE TABLE IF NOT EXISTS namespace_born_member_recovery_pending (
                member_id TEXT PRIMARY KEY REFERENCES namespace_members(member_id), root_id TEXT NOT NULL,
                action_id TEXT NOT NULL UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id));
            CREATE INDEX IF NOT EXISTS namespace_born_member_recovery_root ON namespace_born_member_recovery_pending(root_id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
