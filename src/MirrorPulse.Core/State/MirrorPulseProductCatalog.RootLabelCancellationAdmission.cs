using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Validates metadata for cancelling an original unstarted sealed Label, without granting execution.</summary>
    /// <remarks>The physical anchor may have a newer verified protection epoch after Host restart.
    /// Original descendant epochs, positions, identities and ownership must remain intact. The Host still
    /// retains complete native guards through actual public NotMoved and the final atomic cancellation.</remarks>
    public async Task<MirrorPulseNamespaceMemberMoveIntent> ValidateUnstartedRootLabelCancellationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var intent = await RequireUnstartedRootLabelCancellationAdmissionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberMoveIntent> RequireUnstartedRootLabelCancellationAdmissionCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        _ = await ReadRootLabelCancellationTreeCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original sealed Label cancellation owner is missing.");
        if (intent.Version != 3 || intent.RemoteEntry is not null)
            throw new InvalidOperationException("Only an original local sealed Label has this cancellation admission.");
        return intent;
    }

    private async Task RequireUnstartedRootLabelCancellationNoProgressAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
                SELECT 1 FROM namespace_member_move_starts WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_observations WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_projections WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_directory_move_completions WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_remote_move_rejections WHERE operation_id=$operation LIMIT 1;
                """;
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Started, observed or otherwise settled originals cannot acquire cancellation admission.");
    }
}
