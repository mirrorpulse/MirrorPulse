using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task RequireCurrentObservedConflictEpochsAsync(List<Guid> conflicts, Guid? planId,
        Guid? rejectedEffectId, SqliteTransaction transaction, CancellationToken token)
    {
        if (conflicts.Count == 0) throw new InvalidDataException("A decision requires its current conflict epochs.");
        var names = Enumerable.Range(0, conflicts.Count).Select(index => "$conflict" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        Guid? observationId = null; var observed = new HashSet<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT selected_conflict_id,observation_id FROM coalesced_conflict_remote_members WHERE selected_conflict_id IN (" + string.Join(',', names) + ");";
            for (int index = 0; index < conflicts.Count; index++) query.Parameters.AddWithValue(names[index], conflicts[index].ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                Guid selected = Guid.ParseExact(reader.GetString(0), "D"), current = Guid.ParseExact(reader.GetString(1), "D");
                if (!observed.Add(selected) || observationId is { } prior && prior != current)
                    throw new InvalidDataException("A complete choice cannot combine independently selected version epochs.");
                observationId = current;
            }
        }
        if (observationId is null && (planId is null || rejectedEffectId is null)) return;
        if (planId is null || rejectedEffectId is null)
            throw new InvalidDataException("The complete group lost its version observation ownership.");
        var observation = await ReadConflictRemoteObservationCoreAsync(rejectedEffectId.Value, transaction, token).ConfigureAwait(false);
        if (observation is null && observationId is null) return; // Original Worker error epochs retain their existing semantics.
        if (observed.Count != conflicts.Count || observation is null || observation.ObservationId != observationId || observation.PlanId != planId ||
            !observation.SelectedConflictIds.SequenceEqual(conflicts))
            throw new InvalidDataException("A decision cannot adopt a historical or unowned selected version epoch.");
    }
}
