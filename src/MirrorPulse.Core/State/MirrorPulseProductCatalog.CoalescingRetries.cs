using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>A distinct, explicitly authorized attempt; the rejected effect and original execution remain immutable.</summary>
public sealed record MirrorPulseJournalCoalescingRetry(int Version, Guid CommandId, Guid PlanId, int Ordinal,
    Guid PreviousOperationId, Guid ConflictId, Guid OriginalOperationId, bool AcceptObservedRemoteRevision,
    MirrorPulseMutationIntent Intent, DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Creates one new effect attempt and resolves the entire group's conflict projection atomically.</summary>
    public async Task<MirrorPulseJournalCoalescingRetry> AuthorizeJournalCoalescingRetryAsync(Guid commandId,
        Guid conflictId, bool acceptObservedRemoteRevision, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty) throw new ArgumentException("A user command and group conflict ID are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            Guid originalOperationId;
            await using (var selected = _connection.CreateCommand())
            {
                selected.Transaction = transaction;
                selected.CommandText = "SELECT change_id FROM upload_conflicts WHERE conflict_id=$conflict;";
                selected.Parameters.AddWithValue("$conflict", conflictId.ToString("D"));
                if (await selected.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string original ||
                    !Guid.TryParseExact(original, "D", out originalOperationId))
                    throw new InvalidOperationException("A retained upload conflict must identify its original operation.");
            }
            Guid planId;
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction;
                owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$operation;";
                owner.Parameters.AddWithValue("$operation", originalOperationId.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value || !Guid.TryParseExact(value, "D", out planId))
                    throw new InvalidOperationException("The original journal operation has no coalescing owner.");
            }
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            if (!plan.Members.Any(member => member.OperationId == originalOperationId))
                throw new InvalidDataException("The selected conflict lost its original journal group membership.");
            var execution = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The group lost its immutable execution.");
            await RequireCoalescingRetryCommandAsync(commandId, conflictId, acceptObservedRemoteRevision, transaction, cancellationToken).ConfigureAwait(false);
            await using (var replay = _connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = "SELECT operation_id,payload,fingerprint FROM journal_coalescing_retries WHERE command_id=$command;";
                replay.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var retained = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0), plan, execution);
                    if (retained.CommandId != commandId || retained.ConflictId != conflictId || retained.OriginalOperationId != originalOperationId ||
                        retained.AcceptObservedRemoteRevision != acceptObservedRemoteRevision)
                        throw new InvalidDataException("The retry authorization changed during replay.");
                    transaction.Commit(); return retained;
                }
            }
            string? baseline = execution.ExpectedRevision;
            for (int ordinal = 0; ordinal < execution.Steps.Count; ordinal++)
            {
                var intent = await ReadCurrentCoalescingIntentAsync(plan, execution, ordinal, baseline, transaction, cancellationToken).ConfigureAwait(false);
                var previous = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (previous is { State: MirrorPulseMutationState.Acknowledged })
                {
                    if (execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Move) baseline = previous.AcceptedRevision;
                    continue;
                }
                if (previous is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(previous.Intent, intent))
                    throw new InvalidOperationException("Only an explicitly rejected effect may receive a distinct retry; unknown outcomes require readback first.");
                string? observed = null;
                var conflicts = new List<Guid>();
                foreach (var member in plan.Members)
                {
                    await using var conflict = _connection.CreateCommand(); conflict.Transaction = transaction;
                    conflict.CommandText = "SELECT conflict_id,instance_id,change_id,remote_revision FROM upload_conflicts WHERE change_id=$id AND status=0;";
                    conflict.Parameters.AddWithValue("$id", member.OperationId.ToString("D"));
                    await using var reader = await conflict.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(1) != plan.InstanceId.ToString() ||
                        reader.GetString(2) != member.OperationId.ToString("D") || !Guid.TryParseExact(reader.GetString(0), "D", out Guid groupConflict))
                        throw new InvalidOperationException("The complete group must have pending conflict evidence before retry authorization.");
                    string? revision = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("A group member has multiple pending conflict epochs.");
                    conflicts.Add(groupConflict);
                    if (member != plan.Members[0] && observed != revision)
                        throw new InvalidDataException("The group's observed remote revisions disagree.");
                    observed = revision;
                }
                if (!conflicts.Contains(conflictId)) throw new InvalidOperationException("The selected conflict is not the current pending group epoch.");
                if (acceptObservedRemoteRevision && execution.Steps[ordinal].Kind is MirrorPulseCoalescingStepKind.VerifyAbsence or MirrorPulseCoalescingStepKind.VerifyUnchanged ||
                    acceptObservedRemoteRevision && execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Move && execution.Content is null)
                    throw new NotSupportedException("Keeping local content requires a complete authorized content or deletion effect; a verification or move-only baseline cannot establish it.");
                byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.Retry.v1/" + commandId.ToString("D")));
                var nextIntent = intent with { OperationId = new Guid(identity.AsSpan(0, 16)), ExpectedRevision = acceptObservedRemoteRevision ? observed : intent.ExpectedRevision };
                if (nextIntent.OperationId == Guid.Empty || nextIntent.OperationId == plan.PlanId || plan.Members.Any(member => member.OperationId == nextIntent.OperationId) ||
                    await ReadMutationCoreAsync(nextIntent.OperationId, cancellationToken, transaction).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The new retry identity is already owned.");
                await RequireUnownedCoalescingEffectAsync(nextIntent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                var retry = new MirrorPulseJournalCoalescingRetry(1, commandId, planId, ordinal, intent.OperationId,
                    conflictId, originalOperationId, acceptObservedRemoteRevision, nextIntent, DateTimeOffset.UtcNow);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retry, TopologyJsonOptions);
                await using (var insert = _connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO journal_coalescing_retries(command_id,operation_id,plan_id,ordinal,payload,fingerprint) VALUES($command,$operation,$plan,$ordinal,$payload,$fingerprint);";
                    insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$operation", nextIntent.OperationId.ToString("D"));
                    insert.Parameters.AddWithValue("$plan", planId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
                    insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                _ = await PrepareMutationCoreAsync(nextIntent, JsonSerializer.SerializeToUtf8Bytes(nextIntent, TopologyJsonOptions), cancellationToken, transaction).ConfigureAwait(false);
                foreach (Guid groupConflict in conflicts)
                {
                    await using var resolve = _connection.CreateCommand(); resolve.Transaction = transaction;
                    resolve.CommandText = "UPDATE upload_conflicts SET status=1 WHERE conflict_id=$id AND status=0;";
                    resolve.Parameters.AddWithValue("$id", groupConflict.ToString("D"));
                    if (await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("The group's conflict status changed before authorization.");
                }
                transaction.Commit(); return retry;
            }
            throw new InvalidOperationException("The complete group has no rejected effect to retry.");
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseMutationIntent> ReadCurrentCoalescingIntentAsync(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseJournalCoalescingExecution execution, int ordinal, string? baseline, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseJournalCoalescingRetry? retry = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint FROM journal_coalescing_retries WHERE plan_id=$plan AND ordinal=$ordinal ORDER BY sequence DESC LIMIT 1;";
            query.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D")); query.Parameters.AddWithValue("$ordinal", ordinal);
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) retry = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0), plan, execution);
        }
        if (retry is null) return CoalescingIntent(plan, execution, ordinal, baseline);
        var previous = await ReadMutationCoreAsync(retry.PreviousOperationId, token, transaction).ConfigureAwait(false);
        if (previous is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(previous.Intent with
        { OperationId = execution.Steps[ordinal].OperationId }, CoalescingIntent(plan, execution, ordinal, previous.Intent.ExpectedRevision)) ||
            !retry.AcceptObservedRemoteRevision && retry.Intent.ExpectedRevision != previous.Intent.ExpectedRevision)
            throw new InvalidDataException("The retry lost its original rejected effect evidence.");
        await using (var authorization = _connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = "SELECT action,target_id FROM user_commands WHERE command_id=$command;";
            authorization.Parameters.AddWithValue("$command", retry.CommandId.ToString("D"));
            await using var reader = await authorization.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != (retry.AcceptObservedRemoteRevision ? "keeplocal" : "retry") ||
                reader.GetString(1) != retry.ConflictId.ToString("D"))
                throw new InvalidDataException("The retry lost its original explicit user authorization.");
        }
        await using (var conflict = _connection.CreateCommand())
        {
            conflict.Transaction = transaction;
            conflict.CommandText = "SELECT instance_id,change_id,remote_revision FROM upload_conflicts WHERE conflict_id=$conflict;";
            conflict.Parameters.AddWithValue("$conflict", retry.ConflictId.ToString("D"));
            await using var reader = await conflict.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != plan.InstanceId.ToString() ||
                reader.GetString(1) != retry.OriginalOperationId.ToString("D") || retry.AcceptObservedRemoteRevision &&
                retry.Intent.ExpectedRevision != (reader.IsDBNull(2) ? null : reader.GetString(2)))
                throw new InvalidDataException("The retry lost its retained group conflict and observed baseline.");
        }
        return retry.Intent;
    }

    /// <summary>Lists retained user-command groups for Host recovery before waiting on an empty official feed.</summary>
    public async Task<IReadOnlyList<Guid>> ReadPendingJournalCoalescingRetryGroupsAsync(Guid? afterPlanId = null,
        CancellationToken cancellationToken = default)
    {
        if (afterPlanId == Guid.Empty) throw new ArgumentException("A group cursor cannot be empty.", nameof(afterPlanId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT DISTINCT retries.plan_id FROM journal_coalescing_retries retries JOIN user_commands commands USING(command_id) WHERE commands.state IN ('pending','failed') AND ($after IS NULL OR retries.plan_id>$after) ORDER BY retries.plan_id LIMIT 64;";
            query.Parameters.AddWithValue("$after", (object?)afterPlanId?.ToString("D") ?? DBNull.Value);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var groups = new List<Guid>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                groups.Add(Guid.TryParseExact(reader.GetString(0), "D", out Guid value) && value != Guid.Empty ? value :
                    throw new InvalidDataException("A pending retry command lost its group identity."));
            return groups.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Projects completed user commands after the Host verifies all original official IDs are absent.</summary>
    public async Task CompleteJournalCoalescingRetryCommandsAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty) throw new ArgumentException("A group ID is required.", nameof(planId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            var execution = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The completed group lost its execution.");
            string? baseline = execution.ExpectedRevision;
            for (int ordinal = 0; ordinal < execution.Steps.Count; ordinal++)
            {
                var intent = await ReadCurrentCoalescingIntentAsync(plan, execution, ordinal, baseline, transaction, cancellationToken).ConfigureAwait(false);
                var accepted = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (accepted is not { State: MirrorPulseMutationState.Acknowledged } || !SameIntent(accepted.Intent, intent))
                    throw new InvalidOperationException("Every current authorized effect must complete before its user command can resolve.");
                if (execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Move) baseline = accepted.AcceptedRevision;
            }
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE user_commands SET state='resolved' WHERE state IN ('pending','failed') AND command_id IN (SELECT command_id FROM journal_coalescing_retries WHERE plan_id=$plan);";
            update.Parameters.AddWithValue("$plan", planId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private static MirrorPulseJournalCoalescingRetry DecodeCoalescingRetry(byte[] payload, byte[] fingerprint, string operationId,
        MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingExecution execution)
    {
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint)) throw new InvalidDataException("The retry fingerprint changed.");
        var retry = JsonSerializer.Deserialize<MirrorPulseJournalCoalescingRetry>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The retry authorization is empty.");
        byte[] derived = SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.Retry.v1/" + retry.CommandId.ToString("D")));
        if (retry.Version != 1 || retry.PlanId != plan.PlanId || retry.CommandId == Guid.Empty || retry.PreviousOperationId == Guid.Empty ||
            retry.ConflictId == Guid.Empty || !plan.Members.Any(member => member.OperationId == retry.OriginalOperationId) ||
            retry.Ordinal < 0 || retry.Ordinal >= execution.Steps.Count || retry.Intent.OperationId.ToString("D") != operationId || retry.PreparedAt == default ||
            retry.Intent.OperationId == retry.PreviousOperationId || retry.Intent.OperationId != new Guid(derived.AsSpan(0, 16)) ||
            !SameIntent(retry.Intent with { OperationId = execution.Steps[retry.Ordinal].OperationId },
                CoalescingIntent(plan, execution, retry.Ordinal, retry.Intent.ExpectedRevision)))
            throw new InvalidDataException("The retry cannot change the original effect, content or upload-time binding.");
        return retry;
    }

    private async Task RequireCoalescingRetryCommandAsync(Guid commandId, Guid conflictId, bool acceptObserved,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$command;";
        query.Parameters.AddWithValue("$command", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != (acceptObserved ? "keeplocal" : "retry") ||
            reader.GetString(1) != conflictId.ToString("D") || reader.GetString(2) != "pending")
            throw new InvalidOperationException("A matching pending user command must authorize the complete group retry.");
    }
}
