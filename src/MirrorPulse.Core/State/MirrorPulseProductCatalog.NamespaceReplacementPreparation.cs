using System.Security.Cryptography;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains admission, start and first permission action atomically before any native replacement work.</summary>
    /// <remarks>The Host verifies resident original and private input beforehand. Exact replay returns original authority without inspecting today's object.</remarks>
    public async Task<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementExecutionAsync(
        MirrorPulseNamespaceReplacementIntent intent, MirrorPulseNamespaceReplacementStart start,
        MirrorPulseNamespaceReplacementAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(start); ArgumentNullException.ThrowIfNull(action);
        ValidateNamespaceReplacement(intent); ValidateReplacementAction(action);
        if (start.Version != 1 || start.OperationId != intent.OperationId || start.StartedAt == default || start.StartedAt < intent.PreparedAt)
            throw new ArgumentException("The replacement start must follow its original admission.");
        RequireReplacementActionReferences(action, intent, start);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            await PrepareNamespaceReplacementCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The replacement admission disappeared.");
            if (execution.Action is not null)
            {
                if (execution.Start != start || !SameNamespaceReplacementPayload(execution.Action, action))
                    throw new InvalidOperationException("Atomic preparation cannot replace the original start or action.");
                transaction.Commit(); return execution;
            }
            if (execution.Cancellation is not null || execution.Start is not null && execution.Start != start)
                throw new InvalidOperationException("Atomic preparation cannot adopt a cancelled or different started admission.");
            await RequireNamespaceReplacementPendingCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken,
                allowedReplacement: intent.OperationId).ConfigureAwait(false);
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (current is null || !SameNamespaceReplacementPayload(current, intent.Original))
                throw new InvalidOperationException("First execution authority requires the unchanged admitted original.");
            await RequireReplacementIdAvailableCoreAsync(action.ActionId, transaction, cancellationToken).ConfigureAwait(false);
            if (execution.Start is null)
                await InsertNamespaceReplacementStageCoreAsync(start.OperationId, start, false, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = NamespaceReplacementPayload(action);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_replacement_execution_facts(operation_id,kind,action_id,payload,fingerprint) VALUES($operation,1,$action,$payload,$fingerprint);";
                insert.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$action", action.ActionId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var prepared = await ReadNamespaceReplacementExecutionCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The prepared execution disappeared.");
            transaction.Commit(); return prepared;
        }
        finally { _gate.Release(); }
    }
}
