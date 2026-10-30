namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Begins an explicit version refresh without resetting a completed command or retargeting its selected conflict.</summary>
    public async Task<MirrorPulseUserCommandRecord> BeginCoalescedConflictRemoteRefreshAsync(
        Guid commandId, Guid conflictId, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty) throw new ArgumentException("A refresh command and its exact selected conflict are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var completed = await ReadRemoteRefreshCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            if (completed is not null)
            {
                if (completed.EntryConflictId != conflictId) throw new InvalidDataException("A completed refresh cannot target another conflict epoch.");
                transaction.Commit(); return new(commandId, "refreshconflict", conflictId.ToString("D"), "resolved");
            }
            bool existing;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;";
                query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                existing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (existing && (reader.GetString(0) != "refreshconflict" || reader.GetString(1) != conflictId.ToString("D") || reader.GetString(2) != "pending"))
                    throw new InvalidDataException("A refresh command cannot replace another action, target or completed outcome.");
            }
            var conflict = await ReadObservationConflictCoreAsync(conflictId, transaction, cancellationToken).ConfigureAwait(false);
            if (!conflict.IsPending || !Guid.TryParseExact(conflict.ChangeId, "D", out Guid original))
                throw new InvalidOperationException("A refresh requires a current pending original epoch.");
            Guid planId;
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction; owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$id;";
                owner.Parameters.AddWithValue("$id", original.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value)
                    throw new NotSupportedException("An explicit refresh requires a complete file group.");
                planId = Guid.ParseExact(value, "D");
            }
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            var selected = await ReadConflictDecisionSelectionAsync(conflictId, plan.RootKey, plan.ItemId, conflict.RelativePath, null, false, transaction, cancellationToken).ConfigureAwait(false);
            if (selected.RejectedEffect is null) throw new InvalidOperationException("A refresh requires an explicitly rejected effect.");
            foreach (Guid id in selected.Originals) await RequireUnownedRemoteObservationOriginalAsync(id, transaction, cancellationToken).ConfigureAwait(false);
            if (!existing)
            {
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO user_commands(command_id,action,target_id,state) VALUES($id,'refreshconflict',$target,'pending');";
                insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$target", conflictId.ToString("D"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            return new(commandId, "refreshconflict", conflictId.ToString("D"), "pending");
        }
        finally { _gate.Release(); }
    }
}
