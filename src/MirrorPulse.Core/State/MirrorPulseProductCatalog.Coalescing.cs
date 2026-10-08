using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Persists a decision and reserves its original operation IDs; it never acknowledges CfSharp.</summary>
    public async Task<MirrorPulseJournalCoalescingPlan> PrepareJournalCoalescingAsync(MirrorPulseJournalCoalescingPlan plan,
        CancellationToken cancellationToken = default)
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
                return plan;
            }
            using SqliteTransaction transaction = _connection.BeginTransaction();
            await using (SqliteCommand aggregate = _connection.CreateCommand())
            {
                aggregate.Transaction = transaction;
                aggregate.CommandText = "SELECT 1 FROM mutation_intents WHERE operation_id=$plan UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$plan LIMIT 1;";
                aggregate.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                if (await aggregate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The aggregate operation ID is already owned by another operation.");
            }
            foreach (MirrorPulseJournalOperationReference member in plan.Members)
            {
                await using SqliteCommand fence = _connection.CreateCommand();
                fence.Transaction = transaction;
                fence.CommandText = "SELECT 1 FROM mutation_intents WHERE operation_id=$operation UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$operation UNION ALL SELECT 1 FROM journal_coalescing_plans WHERE plan_id=$operation LIMIT 1;";
                fence.Parameters.AddWithValue("$operation", member.OperationId.ToString());
                if (await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An operation with existing mutation intent or coalescing ownership cannot be merged.");
            }
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO journal_coalescing_plans (plan_id, payload) VALUES ($plan, $payload);";
                insert.Parameters.AddWithValue("$plan", plan.PlanId.ToString());
                insert.Parameters.AddWithValue("$payload", payload);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
