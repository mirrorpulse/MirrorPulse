using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the current authorized program without rewriting its original preparation or rejected effects.</summary>
    public async Task<MirrorPulseJournalCoalescingDispatch> ReadJournalCoalescingDispatchAsync(Guid planId,
        CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty) throw new ArgumentException("A coalescing plan is required.", nameof(planId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            var original = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original coalesced execution is missing.");
            var dispatch = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return dispatch;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseJournalCoalescingDispatch> ReadCoalescingDispatchCoreAsync(
        MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingExecution original,
        SqliteTransaction transaction, CancellationToken token)
    {
        var dispatch = new MirrorPulseJournalCoalescingDispatch(original, original.Content, original.Steps);
        if (plan.Effect != MirrorPulseCoalescedEffect.MoveFile) return dispatch;
        MirrorPulseJournalCoalescingRetry? retry = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint,followup_operation_id FROM journal_coalescing_retries WHERE plan_id=$plan AND ordinal=0 ORDER BY sequence DESC LIMIT 1;";
            query.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                retry = DecodeCoalescingRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0),
                    reader.IsDBNull(3) ? null : reader.GetString(3), plan, dispatch);
        }
        if (retry is null) return dispatch;
        await ValidateCoalescingRetryHistoryAsync(retry, plan, dispatch, original.ExpectedRevision, transaction, token).ConfigureAwait(false);
        if (retry.Version != 3) return dispatch;
        var first = new MirrorPulseCoalescingStep(original.Steps[0].OperationId, 0,
            retry.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? MirrorPulseCoalescingStepKind.Move : MirrorPulseCoalescingStepKind.VerifyAbsence,
            retry.Intent.RelativePath, retry.Intent.PreviousRelativePath);
        return new(original, retry.AuthorizedContent,
            Array.AsReadOnly(new[] { first, new MirrorPulseCoalescingStep(retry.FollowupOperationId!.Value,
                1, MirrorPulseCoalescingStepKind.Upload, plan.FinalPath) }));
    }

    private static Guid CoalescingMoveContentId(Guid commandId) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.MoveContent.v1/" + commandId.ToString("D"))).AsSpan(0, 16));

    private static void ValidateCoalescingDecisionContent(MirrorPulseCoalescingContent? content)
    {
        if (content is not { Length: >= 0, Sha256.Length: 64, UploadBinding: not null } || !content.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("An explicit local-content decision requires its original object and complete byte proof.");
        content.UploadBinding.Validate();
    }

    private async Task RequirePreparedCoalescingFollowupAsync(Guid operationId, InstanceId instanceId,
        CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.CommandText = "SELECT 1 FROM journal_coalescing_retries WHERE followup_operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is null) return;
        var mutation = await ReadMutationCoreAsync(operationId, token).ConfigureAwait(false);
        if (mutation is not { Intent.Origin: MirrorPulseMutationOrigin.CoalescedJournal } || mutation.Intent.InstanceId != instanceId)
            throw new InvalidOperationException("A reserved content suffix cannot be used before its owning mutation is prepared.");
    }

    private static async Task EnsureCoalescingMoveContentSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using (var column = connection.CreateCommand())
        {
            column.CommandText = "SELECT 1 FROM pragma_table_info('journal_coalescing_retries') WHERE name='followup_operation_id';";
            if (await column.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE journal_coalescing_retries ADD COLUMN followup_operation_id TEXT NULL;";
                await alter.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        await using var index = connection.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ix_journal_coalescing_followup ON journal_coalescing_retries(followup_operation_id) WHERE followup_operation_id IS NOT NULL;";
        await index.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
