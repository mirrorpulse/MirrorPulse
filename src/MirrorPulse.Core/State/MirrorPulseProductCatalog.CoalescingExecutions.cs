using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Binds an owned plan to final content and reserves every derived remote-effect ID atomically.</summary>
    /// <remarks>
    /// The Host supplies the complete official observation window and an upload-time proof,
    /// never a later path substituted for historical binding. This method does not dispatch,
    /// confirm content, project an identity or acknowledge any original journal operation.
    /// </remarks>
    public async Task<MirrorPulseJournalCoalescingExecution> PrepareJournalCoalescingExecutionAsync(
        MirrorPulseJournalCoalescingPlan plan, IReadOnlyList<MirrorPulseWorkerChangeCommand> completeWindow,
        string? expectedRevision, MirrorPulseCoalescingContent? content = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Members);
        ArgumentNullException.ThrowIfNull(completeWindow);
        plan = plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) };
        MirrorPulseWorkerChangeCommand[] window = completeWindow.ToArray();
        MirrorPulseJournalCoalescingExecution execution = MirrorPulseJournalCoalescingExecutionPlanner.Create(
            plan, window, expectedRevision, content);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseJournalCoalescingPlan retainedPlan = await ReadCoalescingPlanCoreAsync(
                plan.PlanId, transaction, cancellationToken).ConfigureAwait(false);
            if (!JsonSerializer.SerializeToUtf8Bytes(retainedPlan, TopologyJsonOptions).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, TopologyJsonOptions)))
                throw new InvalidDataException("The execution cannot replace its immutable journal decision.");
            execution = execution with
            {
                OriginalMutations = await ValidateCoalescingExecutionOriginalsAsync(plan, execution,
                    transaction, cancellationToken, window).ConfigureAwait(false),
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(execution, TopologyJsonOptions);
            MirrorPulseJournalCoalescingExecution? replay = await ReadCoalescingExecutionCoreAsync(
                plan.PlanId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!JsonSerializer.SerializeToUtf8Bytes(replay, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The final coalesced content, baseline and remote-effect identities are immutable.");
                transaction.Commit();
                return replay;
            }
            foreach (MirrorPulseCoalescingStep step in execution.Steps)
            {
                await using SqliteCommand ownership = _connection.CreateCommand();
                ownership.Transaction = transaction;
                ownership.CommandText = """
                    SELECT 1 FROM mutation_intents WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM journal_coalescing_plans WHERE plan_id=$id
                    UNION ALL SELECT 1 FROM journal_coalescing_steps WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM worker_requests WHERE operation_id=$id LIMIT 1;
                    """;
                ownership.Parameters.AddWithValue("$id", step.OperationId.ToString("D"));
                if (await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The remote-effect identity already belongs to another operation.");
            }
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO journal_coalescing_executions(plan_id,payload,fingerprint) VALUES($plan,$payload,$fingerprint);";
                insert.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (MirrorPulseCoalescingStep step in execution.Steps)
            {
                await using SqliteCommand insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO journal_coalescing_steps(operation_id,plan_id,ordinal) VALUES($id,$plan,$ordinal);";
                insert.Parameters.AddWithValue("$id", step.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D"));
                insert.Parameters.AddWithValue("$ordinal", step.Ordinal);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            return execution;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseJournalCoalescingExecution?> ReadJournalCoalescingExecutionAsync(
        Guid planId, CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty) throw new ArgumentException("The execution plan ID is missing.", nameof(planId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var execution = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return execution;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseJournalCoalescingPlan> ReadCoalescingPlanCoreAsync(Guid planId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM journal_coalescing_plans WHERE plan_id=$plan;";
        query.Parameters.AddWithValue("$plan", planId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not string payload)
            throw new FileNotFoundException("The original coalescing decision is missing.");
        try
        {
            var plan = JsonSerializer.Deserialize<MirrorPulseJournalCoalescingPlan>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original coalescing decision is empty.");
            if (plan.PlanId != planId || plan.Members is null || plan.Members.Count < 2)
                throw new InvalidDataException("The original coalescing decision has invalid identity or members.");
            return plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) };
        }
        catch (JsonException exception) { throw new InvalidDataException("The original coalescing decision is invalid.", exception); }
    }

    private async Task<MirrorPulseJournalCoalescingExecution?> ReadCoalescingExecutionCoreAsync(Guid planId,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload, fingerprint;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint FROM journal_coalescing_executions WHERE plan_id=$plan;";
            query.Parameters.AddWithValue("$plan", planId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            payload = (byte[])reader[0];
            fingerprint = (byte[])reader[1];
        }
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The coalesced execution fingerprint does not match its immutable payload.");
        MirrorPulseJournalCoalescingExecution execution;
        try
        {
            execution = JsonSerializer.Deserialize<MirrorPulseJournalCoalescingExecution>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The retained coalesced execution is empty.");
            MirrorPulseJournalCoalescingPlan plan = await ReadCoalescingPlanCoreAsync(planId, transaction, token).ConfigureAwait(false);
            MirrorPulseJournalCoalescingExecution expected = MirrorPulseJournalCoalescingExecutionPlanner.FromPlan(
                plan, execution.ExpectedRevision, execution.Content);
            expected = expected with
            {
                OriginalMutations = await ValidateCoalescingExecutionOriginalsAsync(plan, execution, transaction, token).ConfigureAwait(false),
            };
            if (!JsonSerializer.SerializeToUtf8Bytes(expected, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The retained execution does not follow its original coalescing decision.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The retained coalesced execution is invalid.", exception);
        }
        await using SqliteCommand steps = _connection.CreateCommand();
        steps.Transaction = transaction;
        steps.CommandText = "SELECT operation_id,ordinal FROM journal_coalescing_steps WHERE plan_id=$plan ORDER BY ordinal LIMIT 3;";
        steps.Parameters.AddWithValue("$plan", planId.ToString("D"));
        await using SqliteDataReader indexes = await steps.ExecuteReaderAsync(token).ConfigureAwait(false);
        foreach (MirrorPulseCoalescingStep step in execution.Steps)
            if (!await indexes.ReadAsync(token).ConfigureAwait(false) || indexes.GetString(0) != step.OperationId.ToString("D") || indexes.GetInt32(1) != step.Ordinal)
                throw new InvalidDataException("The retained remote-effect ownership index changed.");
        if (await indexes.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("The retained execution has unexpected remote-effect ownership.");
        return execution with
        {
            Steps = Array.AsReadOnly(execution.Steps.ToArray()),
            OriginalMutations = Array.AsReadOnly(execution.OriginalMutations.ToArray()),
        };
    }

    private async Task<IReadOnlyList<MirrorPulseCoalescingOriginalMutation>> ValidateCoalescingExecutionOriginalsAsync(
        MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingExecution execution,
        SqliteTransaction transaction, CancellationToken token, IReadOnlyList<MirrorPulseWorkerChangeCommand>? completeWindow = null)
    {
        var fingerprints = new List<MirrorPulseCoalescingOriginalMutation>();
        Dictionary<Guid, MirrorPulseWorkerChangeCommand>? commands = completeWindow?.ToDictionary(command => command.OperationId);
        await using (SqliteCommand count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM journal_coalescing_members WHERE plan_id=$plan;";
            count.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D"));
            if ((long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L) != plan.Members.Count)
                throw new InvalidDataException("The original acknowledgement ownership is incomplete.");
        }
        foreach (MirrorPulseJournalOperationReference member in plan.Members)
        {
            await using (SqliteCommand ownership = _connection.CreateCommand())
            {
                ownership.Transaction = transaction;
                ownership.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$id;";
                ownership.Parameters.AddWithValue("$id", member.OperationId.ToString("D"));
                if (await ownership.ExecuteScalarAsync(token).ConfigureAwait(false) is not string owner || owner != plan.PlanId.ToString("D"))
                    throw new InvalidDataException("The original journal identity is not owned by this decision.");
            }
            await using SqliteCommand mutation = _connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText = "SELECT payload,state,accepted_revision,updated_utc,execution_started FROM mutation_intents WHERE operation_id=$id;";
            mutation.Parameters.AddWithValue("$id", member.OperationId.ToString("D"));
            await using SqliteDataReader reader = await mutation.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) continue;
            MirrorPulseMutationRecord original;
            try
            {
                original = DecodeMutation(member.OperationId, (byte[])reader[0], reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), plan.PlanId.ToString("D"));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("The retained original mutation is invalid.", exception);
            }
            if (original.State != MirrorPulseMutationState.Superseded || original.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted ||
                original.Intent.Origin != MirrorPulseMutationOrigin.Journal || original.Intent.IsDirectory ||
                original.Intent.InstanceId != plan.InstanceId || original.Intent.RootKey != plan.RootKey ||
                (original.Intent.PreviousRootKey ?? original.Intent.RootKey) != plan.RootKey ||
                original.Intent.ExpectedRevision != execution.ExpectedRevision ||
                original.Intent.UploadBinding is { } binding && execution.Content is { } content && binding.LocalObject != content.UploadBinding.LocalObject)
                throw new InvalidDataException("The execution cannot replace historical original mutation or upload-time object proof.");
            if (commands is not null && (!commands.TryGetValue(member.OperationId, out MirrorPulseWorkerChangeCommand? command) ||
                original.Intent.Kind != command.Kind || original.Intent.RelativePath != command.RelativePath ||
                original.Intent.PreviousRelativePath != command.PreviousRelativePath))
                throw new InvalidDataException("The original mutation no longer follows its journal observation.");
            fingerprints.Add(new(member.OperationId, Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(original.Intent, TopologyJsonOptions)))));
        }
        return fingerprints.AsReadOnly();
    }
}
