using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Captures a current rejected group for owner-side observation without creating an effect or contacting a Worker.</summary>
    public async Task<MirrorPulseCoalescedConflictRemoteTarget> ReadCoalescedConflictRemoteTargetAsync(
        Guid conflictId, CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty) throw new ArgumentException("A current conflict ID is required.", nameof(conflictId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var conflict = await ReadObservationConflictCoreAsync(conflictId, transaction, cancellationToken).ConfigureAwait(false);
            if (!conflict.IsPending || !Guid.TryParseExact(conflict.ChangeId, "D", out Guid originalId))
                throw new InvalidOperationException("Only a current pending original group can select another observed version.");
            Guid planId;
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction; owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$id;";
                owner.Parameters.AddWithValue("$id", originalId.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value)
                    throw new NotSupportedException("This observation path requires a complete original file group.");
                planId = Guid.ParseExact(value, "D");
            }
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, plan.RootKey, plan.ItemId, conflict.RelativePath, null, false, transaction, cancellationToken).ConfigureAwait(false);
            if (selection.RejectedEffect is not { } rejected) throw new InvalidOperationException("The current group has no explicit rejected effect.");
            foreach (Guid original in selection.Originals)
                await RequireUnownedRemoteObservationOriginalAsync(original, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadConflictRemoteObservationCoreAsync(rejected.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            MirrorPulseConflictRemoteSource.ValidatePath(selection.RemoteTarget);
            transaction.Commit(); return new(conflictId, plan.InstanceId, plan.ItemId, plan.RootKey, conflict.RelativePath, rejected, current?.ObservationId)
            { LocalRootRelativePath = plan.FinalPath, RemoteRelativePath = selection.RemoteTarget };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads an immutable observation epoch even after a later explicit refresh.</summary>
    public async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadCoalescedConflictRemoteObservationEpochAsync(
        Guid observationId, CancellationToken cancellationToken = default)
    {
        if (observationId == Guid.Empty) throw new ArgumentException("An observation ID is required.", nameof(observationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var observation = await ReadConflictRemoteObservationPayloadAsync(observationId, transaction, cancellationToken).ConfigureAwait(false);
            if (observation is not null) observation = await ValidateRetainedRemoteObservationAsync(observation, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return observation;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Recovers the same explicit refresh after a lost return without reobserving or retargeting it.</summary>
    public async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadCoalescedConflictRemoteRefreshAsync(
        Guid commandId, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A refresh command ID is required.", nameof(commandId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var observation = await ReadRemoteRefreshCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return observation;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicitly selects a new observed version for the current pending epochs without changing historical choices.</summary>
    /// <remarks>A pending refreshconflict command must target the current conflict ID. The actual Stat happens before this transaction.
    /// Any existing preservation or deletion ownership refuses replacement. This does not authorize a remote effect or SDK acknowledgement.</remarks>
    public async Task<MirrorPulseCoalescedConflictRemoteObservation> RefreshCoalescedConflictRemoteObservationAsync(
        Guid commandId, Guid conflictId, Guid itemId, string localPath, MirrorPulsePostRejectionRemoteVersion observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation); ArgumentNullException.ThrowIfNull(observation.RejectedEffect);
        if (commandId == Guid.Empty || conflictId == Guid.Empty || itemId == Guid.Empty || observation.Version != 1 || observation.ObservationId == Guid.Empty ||
            observation.RejectedEffect.OperationId == Guid.Empty || observation.RejectedEffect.IsDirectory || string.IsNullOrWhiteSpace(observation.RemoteRevision) ||
            string.IsNullOrWhiteSpace(observation.RejectedEffect.RootKey) || observation.StartedAt <= DateTimeOffset.UnixEpoch || observation.ObservedAt < observation.StartedAt)
            throw new ArgumentException("An explicit refresh requires its command, selected conflict, effect, version and ordered observation.");
        ValidatePermissionRelativePath(localPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadRemoteRefreshCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.ObservationId != observation.ObservationId || replay.EntryConflictId != conflictId || replay.ItemId != itemId || replay.LocalRelativePath != localPath ||
                    !SameIntent(replay.RejectedEffect, observation.RejectedEffect) || replay.RemoteRevision != observation.RemoteRevision ||
                    replay.StartedAt != observation.StartedAt || replay.ObservedAt != observation.ObservedAt)
                    throw new InvalidDataException("An explicit refresh replay cannot replace its immutable observation or move the current selection.");
                transaction.Commit(); return replay;
            }
            await RequireRemoteRefreshCommandAsync(commandId, conflictId, "pending", transaction, cancellationToken).ConfigureAwait(false);
            await using (var used = _connection.CreateCommand())
            {
                used.Transaction = transaction; used.CommandText = "SELECT 1 FROM coalesced_conflict_remote_observations WHERE observation_id=$id;";
                used.Parameters.AddWithValue("$id", observation.ObservationId.ToString("D"));
                if (await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("The observation ID already belongs to another immutable observation.");
            }
            var effect = observation.RejectedEffect;
            var current = await ReadConflictRemoteObservationCoreAsync(effect.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, effect.RootKey, itemId, localPath, null, false, transaction, cancellationToken).ConfigureAwait(false);
            if (selection.PlanId is not { } planId || selection.PlanFingerprint is not { } fingerprint || selection.RejectedEffect is not { } rejected ||
                !SameIntent(rejected, effect) || selection.InstanceId != effect.InstanceId || current is not null &&
                (!current.SelectedConflictIds.SequenceEqual(selection.Conflicts) || current.RemoteRevision != selection.RemoteRevision || current.ObservedAt > observation.StartedAt))
                throw new InvalidDataException("A refresh requires the exact complete current rejected group and current selected epoch.");
            string? oldExpected = null;
            for (int index = 0; index < selection.Originals.Count; index++)
            {
                var prior = await ReadObservationConflictCoreAsync(selection.Conflicts[index], transaction, cancellationToken).ConfigureAwait(false);
                if (!prior.IsPending || prior.Reason != MirrorPulseConflictReason.StaleRemoteRevision || prior.InstanceId != effect.InstanceId ||
                    prior.ChangeId != selection.Originals[index].ToString("D") || prior.RelativePath != localPath || prior.RemoteRevision != selection.RemoteRevision ||
                    prior.DetectedAt > observation.StartedAt || index > 0 && prior.LocalRevision != oldExpected)
                    throw new InvalidDataException("The original group changed before the separate observation started.");
                oldExpected = prior.LocalRevision;
                await RequireUnownedRemoteObservationOriginalAsync(selection.Originals[index], transaction, cancellationToken).ConfigureAwait(false);
            }
            var retained = new MirrorPulseCoalescedConflictRemoteObservation(current is null ? 1 : 2, observation.ObservationId, conflictId,
                selection.InstanceId, planId, fingerprint, effect.RootKey, itemId, localPath, effect, selection.RemoteTarget, observation.RemoteRevision,
                current is null ? oldExpected : current.OriginalErrorExpectedRevision, observation.StartedAt, observation.ObservedAt,
                Array.AsReadOnly(selection.Originals.ToArray()), Array.AsReadOnly(selection.Conflicts.ToArray()),
                Array.AsReadOnly(selection.Originals.Select(_ => Guid.NewGuid()).ToArray()))
            {
                InitialConflictIds = Array.AsReadOnly((current?.InitialConflictIds ?? selection.Conflicts).ToArray()),
                OriginalErrorActualRevision = current is null ? selection.RemoteRevision : current.OriginalErrorActualRevision,
                PreviousObservationId = current?.ObservationId,
                RefreshCommandId = commandId
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retained, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO coalesced_conflict_remote_observations(observation_id,rejected_operation_id,plan_id,refresh_command_id,payload,fingerprint) VALUES($id,$effect,$plan,$command,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", retained.ObservationId.ToString("D")); insert.Parameters.AddWithValue("$effect", effect.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$plan", planId.ToString("D")); insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            for (int index = 0; index < retained.OriginalOperationIds.Count; index++)
                await InsertObservedConflictEpochAsync(retained, index, transaction, cancellationToken, selection.RemoteRevision).ConfigureAwait(false);
            await using (var head = _connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText = current is null
                    ? "INSERT INTO coalesced_conflict_remote_heads(rejected_operation_id,observation_id) VALUES($effect,$id);"
                    : "UPDATE coalesced_conflict_remote_heads SET observation_id=$id WHERE rejected_operation_id=$effect AND observation_id=$previous;";
                head.Parameters.AddWithValue("$effect", effect.OperationId.ToString("D")); head.Parameters.AddWithValue("$id", retained.ObservationId.ToString("D"));
                if (current is not null) head.Parameters.AddWithValue("$previous", current.ObservationId.ToString("D"));
                if (await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The current remote selection changed before refresh committed.");
            }
            await using (var complete = _connection.CreateCommand())
            {
                complete.Transaction = transaction;
                complete.CommandText = "UPDATE user_commands SET state='resolved' WHERE command_id=$id AND action='refreshconflict' AND target_id=$target AND state='pending';";
                complete.Parameters.AddWithValue("$id", commandId.ToString("D")); complete.Parameters.AddWithValue("$target", conflictId.ToString("D"));
                if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The refresh command changed before its observation committed.");
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadRemoteRefreshCoreAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? id = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT observation_id FROM coalesced_conflict_remote_observations WHERE refresh_command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D"));
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string value) id = Guid.ParseExact(value, "D");
        }
        if (id is null) return null;
        var observation = await ReadConflictRemoteObservationPayloadAsync(id.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The refresh command lost its immutable observation.");
        return await ValidateRetainedRemoteObservationAsync(observation, transaction, token).ConfigureAwait(false);
    }

    private async Task RequireRemoteRefreshCommandAsync(Guid commandId, Guid conflictId, string state, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;"; query.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "refreshconflict" || reader.GetString(1) != conflictId.ToString("D") || reader.GetString(2) != state)
            throw new InvalidDataException("A refresh requires its exact explicit user command and selected conflict epoch.");
    }

    private async Task RequireUnownedRemoteObservationOriginalAsync(Guid originalId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var owner = _connection.CreateCommand(); owner.Transaction = transaction;
        owner.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_keep_both_owners WHERE original_id=$id LIMIT 1;";
        owner.Parameters.AddWithValue("$id", originalId.ToString("D"));
        if (await owner.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An existing user decision owns the complete original group.");
    }
}
