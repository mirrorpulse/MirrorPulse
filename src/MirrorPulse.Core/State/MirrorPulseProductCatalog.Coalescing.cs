using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Persists a decision and reserves its original operation IDs; it never acknowledges CfSharp.</summary>
    public Task<MirrorPulseJournalCoalescingPlan> PrepareJournalCoalescingAsync(MirrorPulseJournalCoalescingPlan plan,
        CancellationToken cancellationToken = default) => PrepareJournalCoalescingCoreAsync(plan, [], cancellationToken);

    /// <summary>
    /// Reserves a complete file-chain decision and transfers only proven never-started intent to it.
    /// The Host must supply the complete official observation window, not an arbitrary first journal page.
    /// </summary>
    public Task<MirrorPulseJournalCoalescingPlan> PrepareJournalCoalescingWithUnstartedIntentsAsync(
        MirrorPulseJournalCoalescingPlan plan, IReadOnlyList<MirrorPulseWorkerChangeCommand> completeWindow,
        IReadOnlyList<MirrorPulseMutationIntent> unstartedIntents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Members);
        plan = plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) };
        ArgumentNullException.ThrowIfNull(completeWindow);
        ArgumentNullException.ThrowIfNull(unstartedIntents);
        MirrorPulseMutationIntent[] intents = unstartedIntents.ToArray();
        MirrorPulseWorkerChangeCommand[] window = completeWindow.ToArray();
        if (intents.Length == 0 || intents.Select(intent => intent.OperationId).Distinct().Count() != intents.Length)
            throw new ArgumentException("At least one distinct original mutation intent is required.", nameof(unstartedIntents));
        if (intents.Select(intent => intent.ExpectedRevision).Distinct(StringComparer.Ordinal).Count() != 1 ||
            intents.Where(intent => intent.UploadBinding is not null).Select(intent => intent.UploadBinding).Distinct().Skip(1).Any() ||
            intents.Any(intent => intent.Kind is MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate && intent.UploadBinding is null))
            throw new InvalidDataException("Superseding requires one unchanged remote baseline and historical native object proof for prepared content.");
        MirrorPulseJournalCoalescingPlan? expected = MirrorPulseJournalCoalescingPlanner.TryPlan(window, plan.ItemId, plan.WindowUpperSequence, new HashSet<Guid>());
        if (expected is null || JsonSerializer.Serialize(expected, TopologyJsonOptions) != JsonSerializer.Serialize(plan, TopologyJsonOptions))
            throw new InvalidDataException("The decision does not match the complete original observation window.");
        foreach (MirrorPulseMutationIntent intent in intents)
        {
            MirrorPulseWorkerChangeCommand? command = window.SingleOrDefault(candidate => candidate.OperationId == intent.OperationId);
            if (command is null || command.ItemId != plan.ItemId || intent.Origin != MirrorPulseMutationOrigin.Journal ||
                intent.InstanceId != command.InstanceId || intent.RootKey != command.RootKey || intent.Kind != command.Kind ||
                intent.RelativePath != command.RelativePath || intent.PreviousRelativePath != command.PreviousRelativePath || intent.IsDirectory ||
                (intent.PreviousRootKey ?? intent.RootKey) != (command.PreviousRootKey ?? command.RootKey))
                throw new InvalidDataException("An original mutation intent does not match its journal observation.");
        }
        return PrepareJournalCoalescingCoreAsync(plan, intents, cancellationToken);
    }

    private async Task<MirrorPulseJournalCoalescingPlan> PrepareJournalCoalescingCoreAsync(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseMutationIntent[] unstartedIntents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Members);
        plan = plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) };
        if (plan.PlanId == Guid.Empty || plan.ItemId == Guid.Empty || plan.InstanceId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(plan.RootKey) || string.IsNullOrWhiteSpace(plan.OriginalPath) || string.IsNullOrWhiteSpace(plan.FinalPath) ||
            !Enum.IsDefined(plan.Effect) || plan.Members.Count < 2 || plan.WindowUpperSequence <= 0 ||
            plan.Members.Any(member => member.OperationId == Guid.Empty || member.OperationId == plan.PlanId || member.Sequence <= 0 ||
                member.Sequence > plan.WindowUpperSequence || member.Fingerprint is not { Length: 64 } || !member.Fingerprint.All(Uri.IsHexDigit)) ||
            plan.Members.Select(member => member.OperationId).Distinct().Count() != plan.Members.Count ||
            plan.Members.Zip(plan.Members.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw new ArgumentException("The journal coalescing decision is invalid.", nameof(plan));
        string payload = JsonSerializer.Serialize(plan, TopologyJsonOptions);
        Dictionary<Guid, MirrorPulseMutationIntent> originals = unstartedIntents.ToDictionary(intent => intent.OperationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand existing = _connection.CreateCommand();
            existing.CommandText = "SELECT payload FROM journal_coalescing_plans WHERE plan_id=$plan;";
            existing.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stored)
            {
                if (stored != payload) throw new InvalidDataException("A coalescing plan ID cannot be reused with different intent.");
                if (unstartedIntents.Length != 0)
                {
                    foreach (MirrorPulseMutationIntent intent in unstartedIntents)
                    {
                        MirrorPulseMutationRecord? record = await ReadMutationCoreAsync(intent.OperationId, cancellationToken).ConfigureAwait(false);
                        if (record is null || record.State != MirrorPulseMutationState.Superseded || record.SupersededByPlanId != plan.PlanId ||
                            !SameIntent(record.Intent, intent))
                            throw new InvalidDataException("A coalescing replay cannot change the superseded original intent.");
                    }
                    await using SqliteCommand count = _connection.CreateCommand();
                    count.CommandText = """
                        SELECT COUNT(*) FROM journal_coalescing_members members JOIN mutation_intents mutations USING (operation_id)
                        WHERE members.plan_id=$plan AND mutations.state=$superseded;
                        """;
                    count.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                    count.Parameters.AddWithValue("$superseded", (int)MirrorPulseMutationState.Superseded);
                    if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != unstartedIntents.Length)
                        throw new InvalidDataException("A coalescing replay cannot omit superseded original intent.");
                }
                return plan;
            }
            using SqliteTransaction transaction = _connection.BeginTransaction();
            await using (SqliteCommand aggregate = _connection.CreateCommand())
            {
                aggregate.Transaction = transaction;
                aggregate.CommandText = "SELECT 1 FROM mutation_intents WHERE operation_id=$plan UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$plan UNION ALL SELECT 1 FROM journal_coalescing_steps WHERE operation_id=$plan LIMIT 1;";
                aggregate.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                if (await aggregate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The aggregate operation ID is already owned by another operation.");
            }
            foreach (MirrorPulseJournalOperationReference member in plan.Members)
            {
                await using SqliteCommand fence = _connection.CreateCommand();
                fence.Transaction = transaction;
                fence.CommandText = "SELECT 1 FROM journal_coalescing_members WHERE operation_id=$operation UNION ALL SELECT 1 FROM journal_coalescing_plans WHERE plan_id=$operation UNION ALL SELECT 1 FROM journal_coalescing_steps WHERE operation_id=$operation LIMIT 1;";
                fence.Parameters.AddWithValue("$operation", member.OperationId.ToString());
                if (await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An operation with existing coalescing ownership cannot be merged.");
                await using SqliteCommand mutation = _connection.CreateCommand();
                mutation.Transaction = transaction;
                mutation.CommandText = "SELECT payload,state,execution_started FROM mutation_intents WHERE operation_id=$operation;";
                mutation.Parameters.AddWithValue("$operation", member.OperationId.ToString());
                await using SqliteDataReader reader = await mutation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                originals.TryGetValue(member.OperationId, out MirrorPulseMutationIntent? expectedIntent);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (expectedIntent is null || reader.GetInt32(1) != (int)MirrorPulseMutationState.Prepared ||
                        reader.IsDBNull(2) || reader.GetInt32(2) != 0)
                        throw new InvalidOperationException("Only a proven never-started Prepared mutation can transfer to a coalescing plan.");
                    if (!((byte[])reader[0]).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expectedIntent, TopologyJsonOptions)))
                        throw new InvalidDataException("The original immutable mutation intent changed before coalescing.");
                }
                else if (expectedIntent is not null)
                    throw new InvalidOperationException("The original mutation intent is missing.");
            }
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO journal_coalescing_plans (plan_id, payload) VALUES ($plan, $payload);";
                insert.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                insert.Parameters.AddWithValue("$payload", payload);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (MirrorPulseMutationIntent intent in unstartedIntents)
            {
                await using SqliteCommand supersede = _connection.CreateCommand();
                supersede.Transaction = transaction;
                supersede.CommandText = """
                    UPDATE mutation_intents SET state=$superseded, updated_utc=$updated
                    WHERE operation_id=$operation AND state=$prepared AND execution_started=0;
                    """;
                supersede.Parameters.AddWithValue("$superseded", (int)MirrorPulseMutationState.Superseded);
                supersede.Parameters.AddWithValue("$prepared", (int)MirrorPulseMutationState.Prepared);
                supersede.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                supersede.Parameters.AddWithValue("$operation", intent.OperationId.ToString());
                if (await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The original mutation started before coalescing committed.");
            }
            foreach (MirrorPulseJournalOperationReference member in plan.Members)
            {
                await using SqliteCommand insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO journal_coalescing_members (operation_id, plan_id) VALUES ($operation, $plan);";
                insert.Parameters.AddWithValue("$operation", member.OperationId.ToString());
                insert.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            return plan;
        }
        finally { _gate.Release(); }
    }

    private static bool SameIntent(MirrorPulseMutationIntent left, MirrorPulseMutationIntent right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, TopologyJsonOptions).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, TopologyJsonOptions));

    public async Task<IReadOnlyList<MirrorPulseJournalCoalescingPlan>> ReadJournalCoalescingPlansAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM journal_coalescing_plans ORDER BY rowid;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<MirrorPulseJournalCoalescingPlan>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var plan = JsonSerializer.Deserialize<MirrorPulseJournalCoalescingPlan>(reader.GetString(0), TopologyJsonOptions)
                    ?? throw new InvalidDataException("The journal coalescing decision is invalid.");
                result.Add(plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) });
            }
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }
}
