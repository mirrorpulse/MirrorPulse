using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads an immutable post-rejection observation without refreshing its selected version.</summary>
    public async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadCoalescedConflictRemoteObservationAsync(
        Guid rejectedOperationId, CancellationToken cancellationToken = default)
    {
        if (rejectedOperationId == Guid.Empty) throw new ArgumentException("A rejected effect ID is required.", nameof(rejectedOperationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var observation = await ReadConflictRemoteObservationCoreAsync(rejectedOperationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return observation;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically retains a separate observed version and new pending epochs for the complete rejected group.</summary>
    /// <remarks>The original optional error fields, request baseline and rejected effect are retained.
    /// No mutation is authorized and no official journal ID is acknowledged. Replays cannot select another version.</remarks>
    public async Task<MirrorPulseCoalescedConflictRemoteObservation> PrepareCoalescedConflictRemoteObservationAsync(
        Guid conflictId, Guid itemId, string localPath, MirrorPulsePostRejectionRemoteVersion observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.RejectedEffect);
        if (conflictId == Guid.Empty || itemId == Guid.Empty || observation.Version != 1 || observation.ObservationId == Guid.Empty ||
            observation.RejectedEffect.OperationId == Guid.Empty || observation.RejectedEffect.IsDirectory ||
            string.IsNullOrWhiteSpace(observation.RejectedEffect.RootKey) || string.IsNullOrWhiteSpace(observation.RemoteRevision) ||
            observation.StartedAt <= DateTimeOffset.UnixEpoch || observation.ObservedAt < observation.StartedAt)
            throw new ArgumentException("A post-rejection observation requires its exact effect, identities, version and ordered times.", nameof(observation));
        ValidatePermissionRelativePath(localPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadConflictRemoteObservationCoreAsync(observation.RejectedEffect.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.ObservationId != observation.ObservationId || replay.EntryConflictId != conflictId || replay.ItemId != itemId ||
                    replay.LocalRelativePath != localPath || !SameIntent(replay.RejectedEffect, observation.RejectedEffect) ||
                    replay.RemoteRevision != observation.RemoteRevision || replay.StartedAt != observation.StartedAt || replay.ObservedAt != observation.ObservedAt ||
                    replay.Version != 1 || replay.RefreshCommandId is not null)
                    throw new InvalidDataException("A retained observation cannot replace its original source, time, group or selected version.");
                transaction.Commit(); return replay;
            }
            await using (var used = _connection.CreateCommand())
            {
                used.Transaction = transaction; used.CommandText = "SELECT 1 FROM coalesced_conflict_remote_observations WHERE observation_id=$id;";
                used.Parameters.AddWithValue("$id", observation.ObservationId.ToString("D"));
                if (await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("The observation ID already belongs to another rejected effect.");
            }
            var effect = observation.RejectedEffect;
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, effect.RootKey, itemId, localPath,
                null, false, transaction, cancellationToken).ConfigureAwait(false);
            if (selection.PlanId is not { } planId || selection.PlanFingerprint is not { } fingerprint ||
                selection.RejectedEffect is not { } rejected || !SameIntent(rejected, effect) ||
                selection.InstanceId != effect.InstanceId || selection.RemoteRevision is not null)
                throw new InvalidOperationException("Only a complete current rejected group with missing response versions can receive a first separate observation.");
            string? errorExpected = null;
            for (int index = 0; index < selection.Conflicts.Count; index++)
            {
                var previous = await ReadObservationConflictCoreAsync(selection.Conflicts[index], transaction, cancellationToken).ConfigureAwait(false);
                if (!previous.IsPending || previous.RemoteRevision is not null || previous.Reason != MirrorPulseConflictReason.StaleRemoteRevision ||
                    previous.InstanceId != effect.InstanceId || previous.ChangeId != selection.Originals[index].ToString("D") || previous.RelativePath != localPath ||
                    previous.DetectedAt > observation.StartedAt || index > 0 && previous.LocalRevision != errorExpected)
                    throw new InvalidDataException("The original missing-version conflict group or observation ordering changed.");
                errorExpected = previous.LocalRevision;
                await using var owner = _connection.CreateCommand(); owner.Transaction = transaction;
                owner.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_keep_both_owners WHERE original_id=$id LIMIT 1;";
                owner.Parameters.AddWithValue("$id", selection.Originals[index].ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An existing user decision already owns this original group.");
            }
            var retained = new MirrorPulseCoalescedConflictRemoteObservation(1, observation.ObservationId, conflictId,
                selection.InstanceId, planId, fingerprint, effect.RootKey, itemId, localPath, effect,
                selection.RemoteTarget, observation.RemoteRevision, errorExpected, observation.StartedAt, observation.ObservedAt,
                Array.AsReadOnly(selection.Originals.ToArray()), Array.AsReadOnly(selection.Conflicts.ToArray()),
                Array.AsReadOnly(selection.Originals.Select(_ => Guid.NewGuid()).ToArray()))
            { InitialConflictIds = Array.AsReadOnly(selection.Conflicts.ToArray()) };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retained, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO coalesced_conflict_remote_observations(observation_id,rejected_operation_id,plan_id,payload,fingerprint) VALUES($id,$effect,$plan,$payload,$hash);
                    INSERT INTO coalesced_conflict_remote_heads(rejected_operation_id,observation_id) VALUES($effect,$id);
                    """;
                insert.Parameters.AddWithValue("$id", retained.ObservationId.ToString("D")); insert.Parameters.AddWithValue("$effect", effect.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$plan", planId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            for (int index = 0; index < retained.OriginalOperationIds.Count; index++)
                await InsertObservedConflictEpochAsync(retained, index, transaction, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    private async Task InsertObservedConflictEpochAsync(MirrorPulseCoalescedConflictRemoteObservation observation, int index,
        SqliteTransaction transaction, CancellationToken token, string? previousRevision = null)
    {
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO upload_conflicts(conflict_id,instance_id,change_id,relative_path,reason,version_comparison,local_revision,remote_revision,detected_utc,status)
            VALUES($selected,$instance,$original,$path,$reason,$comparison,$expected,$revision,$detected,0);
            INSERT INTO coalesced_conflict_remote_members(observation_id,ordinal,original_id,previous_conflict_id,selected_conflict_id)
            VALUES($observation,$ordinal,$original,$previous,$selected);
            """;
        command.Parameters.AddWithValue("$selected", observation.SelectedConflictIds[index].ToString("D"));
        command.Parameters.AddWithValue("$instance", observation.InstanceId.ToString()); command.Parameters.AddWithValue("$original", observation.OriginalOperationIds[index].ToString("D"));
        command.Parameters.AddWithValue("$path", observation.LocalRelativePath); command.Parameters.AddWithValue("$reason", (int)MirrorPulseConflictReason.StaleRemoteRevision);
        command.Parameters.AddWithValue("$comparison", (int)MirrorPulseVersionComparison.Diverged); command.Parameters.AddWithValue("$expected", (object?)observation.RejectedEffect.ExpectedRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", observation.RemoteRevision); command.Parameters.AddWithValue("$detected", observation.ObservedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observation", observation.ObservationId.ToString("D")); command.Parameters.AddWithValue("$ordinal", index);
        command.Parameters.AddWithValue("$previous", observation.PreviousConflictIds[index].ToString("D"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.Parameters.Clear(); command.CommandText = "UPDATE upload_conflicts SET status=2 WHERE conflict_id=$id AND status=0 AND remote_revision IS $previous;";
        command.Parameters.AddWithValue("$id", observation.PreviousConflictIds[index].ToString("D"));
        command.Parameters.AddWithValue("$previous", (object?)previousRevision ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The original pending error epoch changed during observation selection.");
    }

    private async Task<MirrorPulseConflictRecord> ReadObservationConflictCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT conflict_id,instance_id,change_id,relative_path,reason,version_comparison,local_revision,remote_revision,detected_utc,status FROM upload_conflicts WHERE conflict_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadUploadConflictRecord(reader) : throw new InvalidDataException("An original conflict epoch is missing.");
    }
}
