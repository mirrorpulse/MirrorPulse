using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A before-handoff current-role action retaining the immutable first replacement action.</summary>
/// <remarks>Known prior permissions come from the complete owned action chain; this record proves no native write or process exclusion.</remarks>
public sealed record MirrorPulseNamespaceReplacementRoleAction(int Version, Guid OperationId, Guid RecoveryId,
    Guid? PreviousRecoveryId, Guid FirstActionId, Guid ExpectedPermissionActionId, MirrorPulseNamespaceReplacementAction Action,
    MirrorPulseNamespaceMemberProtectionSnapshot Parent, string ExpectedOwnedDacl);

/// <summary>An independently read original-object observation after metadata-only role handoff.</summary>
/// <remarks>The receipt grants no content-copy completion, placeholder conversion, tree readiness or synchronization acceptance.</remarks>
public sealed record MirrorPulseNamespaceReplacementRoleHandoff(int Version, Guid OperationId, Guid RecoveryId,
    MirrorPulseNamespacePermissionVerification Verification, uint LinkCount, bool IsPlaceholder, string? PlaceholderIdentity);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceReplacementRoleAction> PrepareNamespaceReplacementRoleActionAsync(
        MirrorPulseNamespaceReplacementRoleAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action); ValidateReplacementRoleAction(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(action.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            var replay = execution.RoleActions.SingleOrDefault(value => value.RecoveryId == action.RecoveryId);
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay, action)) throw new InvalidOperationException("The original role action is immutable.");
                transaction.Commit(); return replay;
            }
            if (execution.RestorationResult is { Drained: false } || execution.RoleActions.Count >= 4096 ||
                execution.RestorationResult is { } result && action.Action.PreparedAt < result.RecordedAt ||
                execution.RoleHandoff is { } handoff && action.Action.PreparedAt < handoff.Verification.ObservedAt ||
                execution.Backup is { } backup && action.Action.PreparedAt < backup.Verification.ObservedAt ||
                execution.ContentCompletion is { } copy && action.Action.PreparedAt < copy.Verification.ObservedAt ||
                execution.WriteStart is { } write && action.Action.PreparedAt < write.StartedAt)
                throw new InvalidOperationException("Role recovery cannot supersede undrained native work or exceed its retained bound.");
            var known = execution.RoleActions.ToDictionary(value => value.RecoveryId, value => value.Action);
            if (execution.Action is { } first) known.Add(first.ActionId, first);
            RequireReplacementRoleReferences(action, execution.Intent, execution.Action, execution.RoleActions, known);
            await RequireReplacementIdAvailableCoreAsync(action.RecoveryId, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(execution.Intent.RootId, null, transaction, cancellationToken,
                allowedReplacement: action.OperationId).ConfigureAwait(false);
            var currentParent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(action.Parent.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (currentParent is null || !SameNamespaceReplacementPayload(currentParent, action.Parent))
                throw new InvalidOperationException("The current protected parent changed before role recovery admission.");
            byte[] payload = NamespaceReplacementPayload(action);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_replacement_role_actions(recovery_id,operation_id,previous_id,payload,fingerprint) VALUES($id,$operation,$previous,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", action.RecoveryId.ToString("D")); insert.Parameters.AddWithValue("$operation", action.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", action.PreviousRecoveryId is { } previous ? previous.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); transaction.Commit(); return action;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceReplacementRoleHandoff> RecordNamespaceReplacementRoleHandoffAsync(
        MirrorPulseNamespaceReplacementRoleHandoff handoff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handoff); ValidateReplacementRoleHandoff(handoff);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(handoff.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            var action = execution.RoleActions.SingleOrDefault(value => value.RecoveryId == handoff.RecoveryId)
                ?? throw new FileNotFoundException("The before-handoff role action is missing.");
            RequireReplacementRoleHandoffReferences(handoff, action, execution.Intent);
            var replay = await ReadReplacementRoleHandoffCoreAsync(handoff.RecoveryId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay, handoff)) throw new InvalidOperationException("The first role handoff observation is immutable.");
                transaction.Commit(); return replay;
            }
            if (execution.RoleActions[^1].RecoveryId != handoff.RecoveryId)
                throw new InvalidOperationException("A superseded role action cannot acquire a late observation.");
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = NamespaceReplacementPayload(handoff);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_replacement_role_handoffs(recovery_id,operation_id,payload,fingerprint) VALUES($id,$operation,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", handoff.RecoveryId.ToString("D")); insert.Parameters.AddWithValue("$operation", handoff.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); transaction.Commit(); return handoff;
        }
        finally { _gate.Release(); }
    }

    private async Task<(IReadOnlyList<MirrorPulseNamespaceReplacementRoleAction> Actions, MirrorPulseNamespaceReplacementRoleHandoff? Handoff,
        IReadOnlyList<MirrorPulseNamespaceReplacementRoleHandoff> History)>
        ReadReplacementRoleChainCoreAsync(MirrorPulseNamespaceReplacementIntent intent, MirrorPulseNamespaceReplacementAction? first,
            SqliteTransaction transaction, CancellationToken token)
    {
        var actions = new List<MirrorPulseNamespaceReplacementRoleAction>();
        var known = new Dictionary<Guid, MirrorPulseNamespaceReplacementAction>();
        if (first is not null) known.Add(first.ActionId, first);
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT recovery_id,previous_id,payload,fingerprint FROM namespace_replacement_role_actions WHERE operation_id=$operation ORDER BY sequence LIMIT 4097;";
            query.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var action = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementRoleAction>((byte[])reader[2], (byte[])reader[3], ValidateReplacementRoleAction);
                if (reader.GetString(0) != action.RecoveryId.ToString("D") ||
                    (reader.IsDBNull(1) ? null : reader.GetString(1)) != action.PreviousRecoveryId?.ToString("D"))
                    throw new InvalidDataException("The role action and its original indexes disagree.");
                try { RequireReplacementRoleReferences(action, intent, first, actions, known); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { throw new InvalidDataException("The complete replacement role chain is inconsistent.", error); }
                actions.Add(action);
                if (!known.TryAdd(action.RecoveryId, action.Action)) throw new InvalidDataException("A role action reused an original action identity.");
            }
        }
        if (actions.Count > 4096) throw new InvalidDataException("The retained role chain exceeds its bound.");
        var observations = new Dictionary<Guid, MirrorPulseNamespaceReplacementRoleHandoff>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT recovery_id,payload,fingerprint FROM namespace_replacement_role_handoffs WHERE operation_id=$operation LIMIT 4097;";
            query.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var value = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementRoleHandoff>((byte[])reader[1], (byte[])reader[2], ValidateReplacementRoleHandoff);
                if (reader.GetString(0) != value.RecoveryId.ToString("D") || !observations.TryAdd(value.RecoveryId, value))
                    throw new InvalidDataException("The role observation and its original index disagree.");
            }
        }
        if (observations.Count > 4096) throw new InvalidDataException("The retained handoff observations exceed their bound.");
        var history = observations.Values.ToArray();
        for (int index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (!observations.Remove(action.RecoveryId, out var observation)) continue;
            try
            {
                RequireReplacementRoleHandoffReferences(observation, action, intent);
                if (index + 1 < actions.Count && observation.Verification.ObservedAt > actions[index + 1].Action.PreparedAt)
                    throw new InvalidOperationException("A role observation cannot appear after its successor admission.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("The role observation lost its original action or order.", error); }
        }
        if (observations.Count != 0) throw new InvalidDataException("A role observation lost its original before-handoff action.");
        var head = actions.Count == 0 ? null : await ReadReplacementRoleHandoffCoreAsync(actions[^1].RecoveryId, transaction, token).ConfigureAwait(false);
        return (actions.Count == 0 ? Array.Empty<MirrorPulseNamespaceReplacementRoleAction>() : actions.AsReadOnly(), head,
            history.Length == 0 ? Array.Empty<MirrorPulseNamespaceReplacementRoleHandoff>() : Array.AsReadOnly(history));
    }

    private async Task<MirrorPulseNamespaceReplacementRoleHandoff?> ReadReplacementRoleHandoffCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,payload,fingerprint FROM namespace_replacement_role_handoffs WHERE recovery_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementRoleHandoff>((byte[])reader[1], (byte[])reader[2], ValidateReplacementRoleHandoff);
        if (value.RecoveryId != id || reader.GetString(0) != value.OperationId.ToString("D")) throw new InvalidDataException("The role observation index changed.");
        return value;
    }

    private static void RequireReplacementRoleReferences(MirrorPulseNamespaceReplacementRoleAction action, MirrorPulseNamespaceReplacementIntent intent,
        MirrorPulseNamespaceReplacementAction? first, IReadOnlyList<MirrorPulseNamespaceReplacementRoleAction> predecessors,
        Dictionary<Guid, MirrorPulseNamespaceReplacementAction> known)
    {
        var previous = predecessors.Count == 0 ? first : predecessors[^1].Action;
        if (first is null || previous is null || action.FirstActionId != first.ActionId || action.RecoveryId == first.ActionId ||
            action.OperationId != intent.OperationId || action.Action.OperationId != intent.OperationId || action.Action.ActionId != action.RecoveryId ||
            action.PreviousRecoveryId != (predecessors.Count == 0 ? null : predecessors[^1].RecoveryId) ||
            action.Action.LocalObject != first.LocalObject || action.Action.OwnerSid != first.OwnerSid || action.Action.RoleSid == previous.RoleSid ||
            action.Action.PreparedAt < previous.PreparedAt || action.Parent.Context.Member.MemberId != intent.Original.Context.Location.ParentMemberId ||
            action.Parent.Context.Member.RootId != intent.RootId || action.Action.PreparedAt < action.Parent.Context.Verification.ObservedAt ||
            !known.TryGetValue(action.ExpectedPermissionActionId, out var expected) ||
            !(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expected.RoleOnlyDacl, action.ExpectedOwnedDacl) ||
                MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expected.RestoreDacl, action.ExpectedOwnedDacl)))
            throw new InvalidOperationException("Role recovery requires its original object, complete predecessor chain, current protected parent and previously owned permissions.");
    }

    private static void RequireReplacementRoleHandoffReferences(MirrorPulseNamespaceReplacementRoleHandoff handoff,
        MirrorPulseNamespaceReplacementRoleAction action, MirrorPulseNamespaceReplacementIntent intent)
    {
        if (handoff.OperationId != action.OperationId || handoff.RecoveryId != action.RecoveryId ||
            handoff.Verification.LocalObject != action.Action.LocalObject || handoff.Verification.OwnerSid != action.Action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.Action.RoleOnlyDacl, handoff.Verification.Dacl) ||
            handoff.Verification.ObservedAt < action.Action.PreparedAt ||
            (handoff.IsPlaceholder ? handoff.PlaceholderIdentity != intent.OriginalPlaceholderIdentity : handoff.PlaceholderIdentity is not null))
            throw new InvalidOperationException("Role handoff requires an independent observation of the original object, current role-only access and admitted identity.");
    }

    private static void ValidateReplacementRoleAction(MirrorPulseNamespaceReplacementRoleAction action)
    {
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(action.Action); ArgumentNullException.ThrowIfNull(action.Parent);
        ArgumentNullException.ThrowIfNull(action.Parent.Context); ArgumentNullException.ThrowIfNull(action.Parent.Context.Member);
        ArgumentNullException.ThrowIfNull(action.Parent.Context.Verification); ArgumentNullException.ThrowIfNull(action.Parent.Context.Location);
        ArgumentNullException.ThrowIfNull(action.Parent.Context.Protection); ValidateReplacementAction(action.Action); ValidatePermissionDacl(action.ExpectedOwnedDacl);
        if (action.Version != 1 || action.OperationId == Guid.Empty || action.RecoveryId == Guid.Empty || action.FirstActionId == Guid.Empty || action.ExpectedPermissionActionId == Guid.Empty ||
            action.OperationId == action.RecoveryId || action.PreviousRecoveryId == Guid.Empty || action.PreviousRecoveryId == action.RecoveryId ||
            !action.Parent.Context.Member.IsDirectory || NamespaceReplacementPayload(action).Length > 262_144)
            throw new ArgumentException("Role recovery requires distinct stable IDs, a coherent directory parent and bounded first-action evidence.", nameof(action));
    }

    private static void ValidateReplacementRoleHandoff(MirrorPulseNamespaceReplacementRoleHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff); ArgumentNullException.ThrowIfNull(handoff.Verification);
        ValidatePermissionBinding(handoff.Verification.LocalObject); ValidatePermissionSid(handoff.Verification.OwnerSid, role: false); ValidatePermissionDacl(handoff.Verification.Dacl);
        if (handoff.Version != 1 || handoff.OperationId == Guid.Empty || handoff.RecoveryId == Guid.Empty || handoff.OperationId == handoff.RecoveryId ||
            handoff.LinkCount != 1 || handoff.Verification.ObservedAt == default ||
            (handoff.IsPlaceholder ? handoff.PlaceholderIdentity is not { Length: > 0 and <= 5464 } : handoff.PlaceholderIdentity is not null) ||
            NamespaceReplacementPayload(handoff).Length > 262_144)
            throw new ArgumentException("Role handoff requires its independent single-link original observation and native kind.", nameof(handoff));
    }

    private static async Task EnsureNamespaceReplacementRoleSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacement_role_actions (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                recovery_id TEXT NOT NULL UNIQUE,
                operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
                previous_id TEXT NULL REFERENCES namespace_replacement_role_actions(recovery_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE INDEX IF NOT EXISTS namespace_replacement_role_actions_owner ON namespace_replacement_role_actions(operation_id,sequence);
            CREATE TABLE IF NOT EXISTS namespace_replacement_role_handoffs (
                recovery_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacement_role_actions(recovery_id),
                operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
