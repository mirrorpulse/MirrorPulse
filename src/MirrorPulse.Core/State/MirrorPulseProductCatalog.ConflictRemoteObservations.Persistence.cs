using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadConflictRemoteObservationCoreAsync(
        Guid rejectedOperationId, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? current = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT observation_id FROM coalesced_conflict_remote_heads WHERE rejected_operation_id=$effect;";
            query.Parameters.AddWithValue("$effect", rejectedOperationId.ToString("D"));
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string id)
            {
                current = Guid.ParseExact(id, "D");
                if (current.Value.ToString("D") != id) throw new InvalidDataException("The current remote selection lost its canonical index identity.");
            }
        }
        if (current is null)
        {
            await using var history = _connection.CreateCommand(); history.Transaction = transaction;
            history.CommandText = "SELECT 1 FROM coalesced_conflict_remote_observations WHERE rejected_operation_id=$effect LIMIT 1;";
            history.Parameters.AddWithValue("$effect", rejectedOperationId.ToString("D"));
            if (await history.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A retained observation history lost its current selection.");
            return null;
        }
        var observation = await ReadConflictRemoteObservationPayloadAsync(current.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current remote selection lost its immutable observation.");
        if (observation.RejectedEffect.OperationId != rejectedOperationId)
            throw new InvalidDataException("The current remote selection belongs to another rejected effect.");
        return await ValidateRetainedRemoteObservationAsync(observation, transaction, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadConflictRemoteObservationPayloadAsync(
        Guid observationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT observation_id,plan_id,payload,fingerprint,rejected_operation_id,refresh_command_id FROM coalesced_conflict_remote_observations WHERE observation_id=$id;";
        query.Parameters.AddWithValue("$id", observationId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[2];
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[3]))
            throw new InvalidDataException("The retained remote observation fingerprint changed.");
        var observation = JsonSerializer.Deserialize<MirrorPulseCoalescedConflictRemoteObservation>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The retained remote observation is empty.");
        ValidateRemoteObservationShape(observation);
        if (observation.ObservationId != observationId || reader.GetString(0) != observationId.ToString("D") || reader.GetString(1) != observation.PlanId.ToString("D") ||
            reader.GetString(4) != observation.RejectedEffect.OperationId.ToString("D") ||
            (reader.IsDBNull(5) ? (Guid?)null : Guid.ParseExact(reader.GetString(5), "D")) != observation.RefreshCommandId)
            throw new InvalidDataException("The retained remote observation lost its indexed identity.");
        return observation;
    }

    private async Task<MirrorPulseCoalescedConflictRemoteObservation> ValidateRetainedRemoteObservationAsync(
        MirrorPulseCoalescedConflictRemoteObservation observation, SqliteTransaction transaction, CancellationToken token)
    {
        var plan = await ReadCoalescingPlanCoreAsync(observation.PlanId, transaction, token).ConfigureAwait(false);
        var execution = await ReadCoalescingExecutionCoreAsync(plan.PlanId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The observed conflict lost its original execution.");
        var rejected = await ReadMutationCoreAsync(observation.RejectedEffect.OperationId, token, transaction).ConfigureAwait(false);
        string target = observation.RejectedEffect.Kind == Sync.MirrorPulseWorkerChangeKind.Move
            ? observation.RejectedEffect.PreviousRelativePath! : observation.RejectedEffect.RelativePath;
        if (plan.InstanceId != observation.InstanceId || plan.RootKey != observation.RootKey || plan.ItemId != observation.ItemId ||
            execution.PlanFingerprint != observation.PlanFingerprint || !plan.Members.Select(member => member.OperationId).SequenceEqual(observation.OriginalOperationIds) ||
            rejected is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(rejected.Intent, observation.RejectedEffect) ||
            observation.RejectedEffect.InstanceId != observation.InstanceId || observation.RejectedEffect.RootKey != observation.RootKey || observation.RemoteRelativePath != target)
            throw new InvalidDataException("The selected remote observation lost its exact original group, request or rejected effect.");
        MirrorPulseCoalescedConflictRemoteObservation? previousObservation = null;
        if (observation.PreviousObservationId is { } previousId)
        {
            previousObservation = await ReadConflictRemoteObservationPayloadAsync(previousId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The explicit refresh lost its previous immutable observation.");
            if (previousObservation.PlanId != observation.PlanId || previousObservation.ItemId != observation.ItemId ||
                previousObservation.InstanceId != observation.InstanceId || previousObservation.RootKey != observation.RootKey ||
                previousObservation.LocalRelativePath != observation.LocalRelativePath || previousObservation.PlanFingerprint != observation.PlanFingerprint ||
                !SameIntent(previousObservation.RejectedEffect, observation.RejectedEffect) || previousObservation.RemoteRelativePath != observation.RemoteRelativePath ||
                previousObservation.OriginalErrorExpectedRevision != observation.OriginalErrorExpectedRevision || previousObservation.OriginalErrorActualRevision != observation.OriginalErrorActualRevision ||
                !previousObservation.SelectedConflictIds.SequenceEqual(observation.PreviousConflictIds) ||
                !previousObservation.InitialConflictIds!.SequenceEqual(observation.InitialConflictIds!) ||
                !previousObservation.OriginalOperationIds.SequenceEqual(observation.OriginalOperationIds) || previousObservation.ObservedAt > observation.StartedAt)
                throw new InvalidDataException("An explicit refresh cannot adopt another previous selection or original error history.");
            await ValidateRemoteObservationMemberLinksAsync(previousObservation, transaction, token).ConfigureAwait(false);
        }
        if (observation.RefreshCommandId is { } commandId)
            await RequireRemoteRefreshCommandAsync(commandId, observation.EntryConflictId, "resolved", transaction, token).ConfigureAwait(false);
        await ValidateRemoteObservationMemberLinksAsync(observation, transaction, token).ConfigureAwait(false);
        for (int index = 0; index < observation.OriginalOperationIds.Count; index++)
        {
            var previous = await ReadObservationConflictCoreAsync(observation.PreviousConflictIds[index], transaction, token).ConfigureAwait(false);
            var selected = await ReadObservationConflictCoreAsync(observation.SelectedConflictIds[index], transaction, token).ConfigureAwait(false);
            var initial = previousObservation is null ? previous :
                await ReadObservationConflictCoreAsync(observation.InitialConflictIds![index], transaction, token).ConfigureAwait(false);
            string original = observation.OriginalOperationIds[index].ToString("D");
            string? priorRevision = previousObservation is null ? observation.OriginalErrorActualRevision : previousObservation.RemoteRevision;
            string? priorExpected = previousObservation is null ? observation.OriginalErrorExpectedRevision : observation.RejectedEffect.ExpectedRevision;
            if (previous.InstanceId != observation.InstanceId || selected.InstanceId != observation.InstanceId || previous.ChangeId != original || selected.ChangeId != original ||
                previous.RelativePath != observation.LocalRelativePath || selected.RelativePath != observation.LocalRelativePath || previous.RemoteRevision != priorRevision ||
                previous.LocalRevision != priorExpected || previous.Status != MirrorPulseConflictStatus.Dismissed ||
                previous.DetectedAt > observation.StartedAt || previous.Reason != MirrorPulseConflictReason.StaleRemoteRevision ||
                initial.InstanceId != observation.InstanceId || initial.ChangeId != original || initial.RelativePath != observation.LocalRelativePath ||
                initial.LocalRevision != observation.OriginalErrorExpectedRevision || initial.RemoteRevision != observation.OriginalErrorActualRevision || initial.Status != MirrorPulseConflictStatus.Dismissed ||
                selected.RemoteRevision != observation.RemoteRevision || selected.LocalRevision != observation.RejectedEffect.ExpectedRevision ||
                selected.DetectedAt != observation.ObservedAt || selected.Reason != MirrorPulseConflictReason.StaleRemoteRevision ||
                selected.VersionComparison != Sync.MirrorPulseVersionComparison.Diverged || !Enum.IsDefined(selected.Status))
                throw new InvalidDataException("The previous error facts or selected version epoch changed.");
        }
        return observation with
        {
            OriginalOperationIds = Array.AsReadOnly(observation.OriginalOperationIds.ToArray()),
            PreviousConflictIds = Array.AsReadOnly(observation.PreviousConflictIds.ToArray()),
            SelectedConflictIds = Array.AsReadOnly(observation.SelectedConflictIds.ToArray()),
            InitialConflictIds = Array.AsReadOnly(observation.InitialConflictIds!.ToArray())
        };
    }

    private async Task ValidateRemoteObservationMemberLinksAsync(MirrorPulseCoalescedConflictRemoteObservation observation,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT ordinal,original_id,previous_conflict_id,selected_conflict_id FROM coalesced_conflict_remote_members WHERE observation_id=$id ORDER BY ordinal;";
            query.Parameters.AddWithValue("$id", observation.ObservationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            int index = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (index >= observation.OriginalOperationIds.Count || reader.GetInt32(0) != index ||
                    reader.GetString(1) != observation.OriginalOperationIds[index].ToString("D") ||
                    reader.GetString(2) != observation.PreviousConflictIds[index].ToString("D") || reader.GetString(3) != observation.SelectedConflictIds[index].ToString("D"))
                    throw new InvalidDataException("The complete selected conflict epoch membership changed.");
                index++;
            }
            if (index != observation.OriginalOperationIds.Count) throw new InvalidDataException("A selected conflict epoch member disappeared.");
        }
    }

    private static void ValidateRemoteObservationShape(MirrorPulseCoalescedConflictRemoteObservation observation)
    {
        if (observation.Version is not (1 or 2) || observation.ObservationId == Guid.Empty || observation.EntryConflictId == Guid.Empty ||
            observation.InstanceId.Value == Guid.Empty || observation.PlanId == Guid.Empty || observation.ItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(observation.RootKey) || string.IsNullOrWhiteSpace(observation.RemoteRevision) ||
            observation.PlanFingerprint is not { Length: 64 } || !observation.PlanFingerprint.All(Uri.IsHexDigit) ||
            observation.RejectedEffect is null || observation.RejectedEffect.OperationId == Guid.Empty || observation.RejectedEffect.IsDirectory ||
            observation.StartedAt <= DateTimeOffset.UnixEpoch || observation.ObservedAt < observation.StartedAt ||
            observation.OriginalOperationIds is not { Count: >= 2 } || observation.PreviousConflictIds is null || observation.SelectedConflictIds is null || observation.InitialConflictIds is null ||
            observation.RefreshCommandId == Guid.Empty || observation.Version == 1 && observation.PreviousObservationId is not null ||
            observation.Version == 2 && (observation.PreviousObservationId is null || observation.PreviousObservationId == Guid.Empty ||
                observation.PreviousObservationId == observation.ObservationId || observation.RefreshCommandId is null) ||
            observation.Version == 1 && observation.OriginalErrorActualRevision is not null && observation.RefreshCommandId is null)
            throw new InvalidDataException("The retained remote observation shape is invalid.");
        ValidatePermissionRelativePath(observation.LocalRelativePath);
        MirrorPulseConflictRemoteSource.ValidatePath(observation.RemoteRelativePath);
        int count = observation.OriginalOperationIds.Count;
        if (observation.PreviousConflictIds.Count != count || observation.SelectedConflictIds.Count != count || observation.InitialConflictIds.Count != count ||
            observation.OriginalOperationIds.Any(id => id == Guid.Empty) || observation.PreviousConflictIds.Any(id => id == Guid.Empty) || observation.SelectedConflictIds.Any(id => id == Guid.Empty) ||
            observation.OriginalOperationIds.Distinct().Count() != count || observation.PreviousConflictIds.Distinct().Count() != count || observation.SelectedConflictIds.Distinct().Count() != count ||
            observation.InitialConflictIds.Any(id => id == Guid.Empty) || observation.InitialConflictIds.Distinct().Count() != count ||
            observation.Version == 1 && !observation.PreviousConflictIds.SequenceEqual(observation.InitialConflictIds) ||
            !observation.PreviousConflictIds.Contains(observation.EntryConflictId) || observation.PreviousConflictIds.Intersect(observation.SelectedConflictIds).Any() ||
            observation.InitialConflictIds.Intersect(observation.SelectedConflictIds).Any())
            throw new InvalidDataException("The retained remote observation requires disjoint, complete original epoch identities.");
    }
}
