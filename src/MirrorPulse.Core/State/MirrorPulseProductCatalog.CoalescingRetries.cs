using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>A distinct, explicitly authorized attempt; the rejected effect and original execution remain immutable.</summary>
public sealed record MirrorPulseJournalCoalescingRetry(int Version, Guid CommandId, Guid PlanId, int Ordinal,
    Guid PreviousOperationId, Guid ConflictId, Guid OriginalOperationId, bool AcceptObservedRemoteRevision,
    MirrorPulseMutationIntent Intent, DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseCoalescingContent? AuthorizedContent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FollowupOperationId { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Creates one new effect attempt and resolves the entire group's conflict projection atomically.</summary>
    public Task<MirrorPulseJournalCoalescingRetry> AuthorizeJournalCoalescingRetryAsync(Guid commandId,
        Guid conflictId, bool acceptObservedRemoteRevision, CancellationToken cancellationToken = default) =>
        AuthorizeCoalescingRetryCoreAsync(commandId, conflictId, acceptObservedRemoteRevision, null, cancellationToken);

    /// <summary>Retains an explicit local-content decision for verification or a move-only program.</summary>
    public Task<MirrorPulseJournalCoalescingRetry> AuthorizeJournalCoalescingKeepLocalContentAsync(Guid commandId,
        Guid conflictId, MirrorPulseCoalescingContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return AuthorizeCoalescingRetryCoreAsync(commandId, conflictId, true, content, cancellationToken);
    }

    private async Task<MirrorPulseJournalCoalescingRetry> AuthorizeCoalescingRetryCoreAsync(Guid commandId,
        Guid conflictId, bool acceptObservedRemoteRevision, MirrorPulseCoalescingContent? content, CancellationToken cancellationToken)
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
            await using (var deletion = _connection.CreateCommand())
            {
                deletion.Transaction = transaction;
                deletion.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$operation UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$operation UNION ALL SELECT 1 FROM conflict_keep_both_owners WHERE original_id=$operation AND command_id<>$command LIMIT 1;";
                deletion.Parameters.AddWithValue("$operation", originalOperationId.ToString("D"));
                deletion.Parameters.AddWithValue("$command", commandId.ToString("D"));
                if (await deletion.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The original group belongs to an explicit deletion decision.");
            }
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
            var originalExecution = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The group lost its immutable execution.");
            var execution = await ReadCoalescingDispatchCoreAsync(plan, originalExecution, transaction, cancellationToken).ConfigureAwait(false);
            await RequireCoalescingRetryCommandAsync(commandId, conflictId, acceptObservedRemoteRevision, transaction, cancellationToken).ConfigureAwait(false);
            var keepBoth = await ReadConflictKeepBothCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            if (keepBoth is not null && keepBoth.Intent.AuthorizedContent != content)
                throw new InvalidDataException("KeepBoth must retry the original explicitly retained local-content choice.");
            await using (var replay = _connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = "SELECT operation_id,payload,fingerprint,followup_operation_id FROM journal_coalescing_retries WHERE command_id=$command;";
                replay.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var retained = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0),
                        reader.IsDBNull(3) ? null : reader.GetString(3), plan, execution);
                    if (retained.CommandId != commandId || retained.ConflictId != conflictId || retained.OriginalOperationId != originalOperationId ||
                        retained.AcceptObservedRemoteRevision != acceptObservedRemoteRevision ||
                        content is not null && retained.AuthorizedContent != content)
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
                    if (execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Move || execution.HasMoveContent && ordinal == 0)
                        baseline = previous.AcceptedRevision;
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
                await RequireCurrentObservedConflictEpochsAsync(conflicts, plan.PlanId, intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                var kind = execution.Steps[ordinal].Kind;
                bool moveOnly = originalExecution.IsMoveOnly && ordinal == 0;
                bool moveContent = moveOnly && (content is not null || execution.HasMoveContent);
                bool followupContent = execution.HasMoveContent && ordinal == 1 && kind == MirrorPulseCoalescingStepKind.Upload;
                if (content is not null && (!acceptObservedRemoteRevision ||
                    kind != MirrorPulseCoalescingStepKind.VerifyUnchanged && !moveOnly && !followupContent))
                    throw new InvalidOperationException("New content requires an explicit supported keep-local content decision.");
                if (acceptObservedRemoteRevision && moveOnly && !moveContent)
                    throw new NotSupportedException("A move-only keep-local decision requires a complete move and content program.");
                bool replacesVerification = !moveOnly && (kind is MirrorPulseCoalescingStepKind.VerifyAbsence or MirrorPulseCoalescingStepKind.VerifyUnchanged) &&
                    (acceptObservedRemoteRevision || intent.Kind != MirrorPulseWorkerChangeKind.MetadataUpdate);
                MirrorPulseCoalescingContent? authorizedContent = moveContent ? content ?? execution.Content :
                    (replacesVerification && kind == MirrorPulseCoalescingStepKind.VerifyUnchanged || followupContent)
                    ? content ?? (intent.UploadBinding is not null && intent.ContentLength is { } length && intent.ContentSha256 is { } hash
                        ? new(length, hash, intent.UploadBinding) : throw new NotSupportedException("Keeping local content requires its newly captured object and byte proof."))
                    : null;
                if (authorizedContent is not null) ValidateCoalescingDecisionContent(authorizedContent);
                if (replacesVerification && kind == MirrorPulseCoalescingStepKind.VerifyAbsence && string.IsNullOrEmpty(acceptObservedRemoteRevision ? observed : intent.ExpectedRevision))
                    throw new InvalidDataException("A keep-local deletion requires the explicitly observed remote revision.");
                byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.Retry.v1/" + commandId.ToString("D")));
                int retryVersion = moveContent ? 3 : replacesVerification || followupContent ? 2 : 1;
                var nextIntent = RetryIntent(plan, execution, ordinal, acceptObservedRemoteRevision ? observed : intent.ExpectedRevision,
                    retryVersion, authorizedContent) with
                { OperationId = new Guid(identity.AsSpan(0, 16)) };
                if (nextIntent.OperationId == Guid.Empty || nextIntent.OperationId == plan.PlanId || plan.Members.Any(member => member.OperationId == nextIntent.OperationId) ||
                    await ReadMutationCoreAsync(nextIntent.OperationId, cancellationToken, transaction).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The new retry identity is already owned.");
                await RequireUnownedCoalescingEffectAsync(nextIntent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                Guid? followup = moveContent ? CoalescingMoveContentId(commandId) : null;
                if (followup is { } followupId)
                {
                    if (followupId == Guid.Empty || followupId == nextIntent.OperationId) throw new InvalidDataException("The move and content identities must be distinct.");
                    await RequireUnownedCoalescingEffectAsync(followupId, transaction, cancellationToken).ConfigureAwait(false);
                }
                var retry = new MirrorPulseJournalCoalescingRetry(retryVersion, commandId, planId, ordinal, intent.OperationId,
                    conflictId, originalOperationId, acceptObservedRemoteRevision, nextIntent, DateTimeOffset.UtcNow)
                { AuthorizedContent = authorizedContent, FollowupOperationId = followup };
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retry, TopologyJsonOptions);
                await using (var insert = _connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO journal_coalescing_retries(command_id,operation_id,plan_id,ordinal,payload,fingerprint,followup_operation_id) VALUES($command,$operation,$plan,$ordinal,$payload,$fingerprint,$followup);";
                    insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$operation", nextIntent.OperationId.ToString("D"));
                    insert.Parameters.AddWithValue("$plan", planId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
                    insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                    insert.Parameters.AddWithValue("$followup", (object?)followup?.ToString("D") ?? DBNull.Value);
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
                if (keepBoth is not null)
                {
                    await using var release = _connection.CreateCommand(); release.Transaction = transaction;
                    release.CommandText = "DELETE FROM conflict_keep_both_owners WHERE command_id=$id;";
                    release.Parameters.AddWithValue("$id", commandId.ToString("D"));
                    if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != keepBoth.Intent.OriginalOperationIds.Count)
                        throw new InvalidDataException("KeepBoth lost its original group ownership before retry authorization.");
                }
                transaction.Commit(); return retry;
            }
            throw new InvalidOperationException("The complete group has no rejected effect to retry.");
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseMutationIntent> ReadCurrentCoalescingIntentAsync(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseJournalCoalescingDispatch execution, int ordinal, string? baseline, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseJournalCoalescingRetry? retry = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint,followup_operation_id FROM journal_coalescing_retries WHERE plan_id=$plan AND ordinal=$ordinal ORDER BY sequence DESC LIMIT 1;";
            query.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D")); query.Parameters.AddWithValue("$ordinal", ordinal);
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) retry = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0),
                reader.IsDBNull(3) ? null : reader.GetString(3), plan, execution);
        }
        if (retry is null) return CoalescingIntent(plan, execution, ordinal, baseline);
        await ValidateCoalescingRetryHistoryAsync(retry, plan, execution, baseline, transaction, token).ConfigureAwait(false);
        return retry.Intent;
    }

    private async Task ValidateCoalescingRetryHistoryAsync(MirrorPulseJournalCoalescingRetry retry,
        MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingDispatch execution, string? baseline,
        SqliteTransaction transaction, CancellationToken token)
    {
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(retry.Intent.OperationId)) throw new InvalidDataException("The retained retry history contains a cycle.");
            var previous = await ReadMutationCoreAsync(retry.PreviousOperationId, token, transaction).ConfigureAwait(false);
            if (previous is not { State: MirrorPulseMutationState.Conflict } ||
                !retry.AcceptObservedRemoteRevision && !SameIntent(retry.Intent with { OperationId = previous.Intent.OperationId }, previous.Intent))
                throw new InvalidDataException("The retry lost its original rejected effect evidence.");
            await RequireRetainedCoalescingRetryAuthorizationAsync(retry, plan, transaction, token).ConfigureAwait(false);
            if (previous.Intent.OperationId == execution.Steps[retry.Ordinal].OperationId)
            {
                if (retry.Version == 3 && !retry.AcceptObservedRemoteRevision)
                    throw new InvalidDataException("A move-content suffix requires its original explicit keep-local authorization.");
                var original = retry.Ordinal == 0 && execution.Original.IsMoveOnly
                    ? new MirrorPulseJournalCoalescingDispatch(execution.Original, execution.Original.Content, execution.Original.Steps) : execution;
                if (!SameIntent(previous.Intent, CoalescingIntent(plan, original, retry.Ordinal, baseline)))
                    throw new InvalidDataException("The first rejected effect lost its original execution baseline or proof.");
                return;
            }
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint,followup_operation_id FROM journal_coalescing_retries WHERE operation_id=$operation AND plan_id=$plan AND ordinal=$ordinal;";
            query.Parameters.AddWithValue("$operation", previous.Intent.OperationId.ToString("D"));
            query.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D")); query.Parameters.AddWithValue("$ordinal", retry.Ordinal);
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("A prior retry authorization disappeared.");
            var prior = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0),
                reader.IsDBNull(3) ? null : reader.GetString(3), plan, execution);
            if (!SameIntent(prior.Intent, previous.Intent)) throw new InvalidDataException("The previous retry intent changed.");
            if (prior.Version == 3 && (retry.Version != 3 ||
                !retry.AcceptObservedRemoteRevision && retry.AuthorizedContent != prior.AuthorizedContent))
                throw new InvalidDataException("A retry cannot drop its authorized upload suffix or replace its bytes without another explicit decision.");
            retry = prior;
        }
    }

    private async Task RequireRetainedCoalescingRetryAuthorizationAsync(MirrorPulseJournalCoalescingRetry retry,
        MirrorPulseJournalCoalescingPlan plan, SqliteTransaction transaction, CancellationToken token)
    {
        await using (var authorization = _connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = "SELECT action,target_id FROM user_commands WHERE command_id=$command;";
            authorization.Parameters.AddWithValue("$command", retry.CommandId.ToString("D"));
            await using var reader = await authorization.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) ||
                reader.GetString(0) != (retry.AcceptObservedRemoteRevision ? "keeplocal" : "retry") && !(retry.AcceptObservedRemoteRevision && reader.GetString(0) == "keepboth") ||
                reader.GetString(1) != retry.ConflictId.ToString("D"))
                throw new InvalidDataException("The retry lost its original explicit user authorization.");
            bool keepBoth = reader.GetString(0) == "keepboth";
            await reader.DisposeAsync().ConfigureAwait(false);
            if (keepBoth)
            {
                var decision = await ReadConflictKeepBothCoreAsync(retry.CommandId, transaction, token).ConfigureAwait(false);
                if (decision?.Copy is null || !decision.RetryAuthorized || decision.Intent.PlanId != plan.PlanId || decision.Intent.ConflictId != retry.ConflictId ||
                    decision.Intent.AuthorizedContent != retry.AuthorizedContent || decision.Intent.RemoteSource.RemoteRevision != retry.Intent.ExpectedRevision)
                    throw new InvalidDataException("The retained KeepBoth retry lost its original preservation and local-content authorization.");
            }
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
            var original = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The completed group lost its execution.");
            var execution = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, cancellationToken).ConfigureAwait(false);
            string? baseline = execution.ExpectedRevision;
            for (int ordinal = 0; ordinal < execution.Steps.Count; ordinal++)
            {
                var intent = await ReadCurrentCoalescingIntentAsync(plan, execution, ordinal, baseline, transaction, cancellationToken).ConfigureAwait(false);
                var accepted = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (accepted is not { State: MirrorPulseMutationState.Acknowledged } || !SameIntent(accepted.Intent, intent))
                    throw new InvalidOperationException("Every current authorized effect must complete before its user command can resolve.");
                if (execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Move || execution.HasMoveContent && ordinal == 0)
                    baseline = accepted.AcceptedRevision;
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
        string? followupOperationId, MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingDispatch execution)
    {
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint)) throw new InvalidDataException("The retry fingerprint changed.");
        var retry = JsonSerializer.Deserialize<MirrorPulseJournalCoalescingRetry>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The retry authorization is empty.");
        byte[] derived = SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.Retry.v1/" + retry.CommandId.ToString("D")));
        if (retry.Intent is null || retry.Version is not (1 or 2 or 3) || retry.PlanId != plan.PlanId || retry.CommandId == Guid.Empty || retry.PreviousOperationId == Guid.Empty ||
            retry.ConflictId == Guid.Empty || !plan.Members.Any(member => member.OperationId == retry.OriginalOperationId) ||
            retry.Ordinal < 0 || retry.Ordinal >= execution.Steps.Count || retry.Intent.OperationId.ToString("D") != operationId || retry.PreparedAt == default ||
            retry.Intent.OperationId == retry.PreviousOperationId || retry.Intent.OperationId != new Guid(derived.AsSpan(0, 16)) ||
            !SameIntent(retry.Intent with { OperationId = execution.Steps[retry.Ordinal].OperationId },
                RetryIntent(plan, execution, retry.Ordinal, retry.Intent.ExpectedRevision, retry.Version, retry.AuthorizedContent)))
            throw new InvalidDataException("The retry cannot change its original program or explicitly authorized replacement proof.");
        if (retry.Version == 3 ? retry.FollowupOperationId is not { } followup || followup == Guid.Empty ||
            followup == retry.Intent.OperationId || followup != CoalescingMoveContentId(retry.CommandId) || followup.ToString("D") != followupOperationId :
            retry.FollowupOperationId is not null || followupOperationId is not null)
            throw new InvalidDataException("The authorized content suffix lost its distinct reserved operation identity.");
        return retry;
    }

    private static MirrorPulseMutationIntent RetryIntent(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseJournalCoalescingDispatch execution, int ordinal, string? baseline, int version, MirrorPulseCoalescingContent? content)
    {
        var original = ordinal == 0 && execution.Original.IsMoveOnly
            ? new MirrorPulseJournalCoalescingDispatch(execution.Original, execution.Original.Content, execution.Original.Steps) : execution;
        var intent = CoalescingIntent(plan, original, ordinal, baseline);
        if (version == 1)
            return content is null ? intent : throw new InvalidDataException("An original retry cannot replace its retained content.");
        if (version == 3)
        {
            if (ordinal != 0 || !execution.Original.IsMoveOnly || execution.Original.Content is not null ||
                execution.Original.Steps.Count != 1 || execution.Original.Steps[ordinal].Kind != MirrorPulseCoalescingStepKind.Move || baseline is { Length: 0 })
                throw new InvalidDataException("Only an original move-only program can acquire a content suffix.");
            ValidateCoalescingDecisionContent(content);
            intent = intent with { ContentLength = null, ContentSha256 = null, UploadBinding = null };
            return baseline is null ? intent with
            { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate, RelativePath = plan.OriginalPath, PreviousRelativePath = null } : intent;
        }
        if (version != 2) throw new InvalidDataException("The retry version is unsupported.");
        if (execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.VerifyAbsence && content is null && !string.IsNullOrEmpty(baseline))
            return intent with { Kind = MirrorPulseWorkerChangeKind.Delete };
        if (execution.Steps[ordinal].Kind != MirrorPulseCoalescingStepKind.VerifyUnchanged &&
            !(execution.HasMoveContent && ordinal == 1 && execution.Steps[ordinal].Kind == MirrorPulseCoalescingStepKind.Upload))
            throw new InvalidDataException("A verification replacement requires a complete local content or conditional deletion decision.");
        ValidateCoalescingDecisionContent(content);
        return intent with
        {
            Kind = MirrorPulseWorkerChangeKind.ContentUpdate,
            ContentLength = content!.Length,
            ContentSha256 = content.Sha256,
            UploadBinding = content.UploadBinding
        };
    }

    private async Task RequireCoalescingRetryCommandAsync(Guid commandId, Guid conflictId, bool acceptObserved,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$command;";
        query.Parameters.AddWithValue("$command", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) ||
            reader.GetString(0) != (acceptObserved ? "keeplocal" : "retry") && !(acceptObserved && reader.GetString(0) == "keepboth") ||
            reader.GetString(1) != conflictId.ToString("D") ||
            reader.GetString(2) != "pending" && !(reader.GetString(0) == "keepboth" && reader.GetString(2) is "failed" or "resolved" or "superseded"))
            throw new InvalidOperationException("A matching pending user command must authorize the complete group retry.");
        bool keepBoth = reader.GetString(0) == "keepboth";
        string state = reader.GetString(2);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (keepBoth)
        {
            var decision = await ReadConflictKeepBothCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (decision?.Copy is null || decision.Intent.ConflictId != conflictId)
                throw new InvalidOperationException("The complete selected remote version must be preserved before KeepBoth authorizes a retry.");
            if (state is "resolved" or "superseded" && !decision.RetryAuthorized)
                throw new InvalidOperationException("A completed command cannot authorize a new KeepBoth retry.");
        }
    }
}
