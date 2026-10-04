using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseLocalOperationBlockReason
{
    RootReconciliationRequired, UnregisteredPath, CrossRootMove, InvalidMove,
    UnsupportedDirectoryCreate, UnsupportedMetadataChange, UnsupportedChangeKind, RequestIdentityMismatch,
    IncompleteLocalContent, UnsupportedRescanDirectoryDeletion,
    MissingUploadBinding,
}

public sealed record MirrorPulseBlockedLocalOperation(Guid OperationId, InstanceId? InstanceId,
    string RelativePath, MirrorPulseLocalOperationBlockReason Reason, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task SaveBlockedLocalOperationAsync(MirrorPulseBlockedLocalOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.OperationId == Guid.Empty) throw new ArgumentException("The operation ID must not be empty.", nameof(operation));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO blocked_local_operations (operation_id, instance_id, relative_path, reason, observed_utc)
                VALUES ($operation, $instance, $path, $reason, $observed)
                ON CONFLICT(operation_id) DO UPDATE SET instance_id=excluded.instance_id,
                    relative_path=excluded.relative_path, reason=excluded.reason;
                """;
            command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$instance", (object?)operation.InstanceId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$path", operation.RelativePath);
            command.Parameters.AddWithValue("$reason", (int)operation.Reason);
            command.Parameters.AddWithValue("$observed", operation.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseBlockedLocalOperation>> ReadBlockedLocalOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT operation_id, instance_id, relative_path, reason, observed_utc FROM blocked_local_operations ORDER BY observed_utc, operation_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<MirrorPulseBlockedLocalOperation>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : InstanceId.Parse(reader.GetString(1)),
                    reader.GetString(2), (MirrorPulseLocalOperationBlockReason)reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task ClearBlockedLocalOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM blocked_local_operations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
