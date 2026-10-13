using System.Globalization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseFullRescanPhase { Requested, Running, Acknowledging }
public sealed record MirrorPulseFullRescanCheckpoint(Guid Generation, MirrorPulseFullRescanPhase Phase, DateTimeOffset UpdatedAt);
public sealed record MirrorPulseRootRescanRequest(RootId RootId, Guid Generation, DateTimeOffset RequestedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseRootRescanRequest> RequireRootRescanAsync(RootId rootId, CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("The rescan root must not be empty.", nameof(rootId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            // Reject corrupted current-schema requests before an upsert could conceal them.
            await ReadRootRescanRequestsCoreAsync(_connection, transaction, cancellationToken).ConfigureAwait(false);
            var request = new MirrorPulseRootRescanRequest(rootId, Guid.NewGuid(), DateTimeOffset.UtcNow);
            await SaveRootRescanRequestCoreAsync(_connection, transaction, request, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return request;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Completes only the captured request; a newer obligation survives stale scan projection.</summary>
    public async Task<bool> CompleteRootRescanAsync(RootId rootId, Guid expectedGeneration, CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty || expectedGeneration == Guid.Empty) throw new ArgumentException("The root rescan generation is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            await ReadRootRescanRequestsCoreAsync(_connection, transaction, cancellationToken).ConfigureAwait(false);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM deferred_rescan_roots WHERE root_id=$root AND EXISTS (
                    SELECT 1 FROM root_rescan_generations WHERE root_id=$root AND generation=$generation);
                """;
            command.Parameters.AddWithValue("$root", rootId.ToString());
            command.Parameters.AddWithValue("$generation", expectedGeneration.ToString("D"));
            bool completed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return completed;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RootId>> ReadDeferredRescanRootsAsync(CancellationToken cancellationToken = default) =>
        (await ReadRootRescanRequestsAsync(cancellationToken).ConfigureAwait(false)).Select(request => request.RootId).ToArray();

    public async Task<IReadOnlyList<MirrorPulseRootRescanRequest>> ReadRootRescanRequestsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadRootRescanRequestsCoreAsync(_connection, null, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseRootRescanRequest>> ReadCapturedRootRescanRequestsAsync(
        Guid generation, CancellationToken cancellationToken = default)
    {
        if (generation == Guid.Empty) throw new ArgumentException("The full rescan generation must not be empty.", nameof(generation));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadCapturedRootRescanRequestsCoreAsync(generation, null, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseFullRescanCheckpoint> BeginFullRescanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO full_rescan_checkpoint (singleton,generation,phase,updated_utc) VALUES (1,$generation,$phase,$updated) ON CONFLICT(singleton) DO NOTHING;";
            command.Parameters.AddWithValue("$generation", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$phase", (int)MirrorPulseFullRescanPhase.Requested);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return await ReadFullRescanCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The full rescan checkpoint disappeared.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseFullRescanCheckpoint?> ReadFullRescanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadFullRescanCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task UpdateFullRescanAsync(Guid generation, MirrorPulseFullRescanPhase phase, CancellationToken cancellationToken = default)
    {
        if (generation == Guid.Empty || !Enum.IsDefined(phase)) throw new ArgumentException("The full rescan checkpoint is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseFullRescanCheckpoint checkpoint = await ReadFullRescanCoreAsync(cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The full rescan generation changed.");
            if (checkpoint.Generation != generation) throw new InvalidOperationException("The full rescan generation changed.");
            if (phase == MirrorPulseFullRescanPhase.Running)
                await CaptureRootRescanRequestsCoreAsync(generation, transaction, cancellationToken).ConfigureAwait(false);
            else if (phase == MirrorPulseFullRescanPhase.Acknowledging)
            {
                if (checkpoint.Phase != MirrorPulseFullRescanPhase.Running)
                    throw new InvalidOperationException("Discovery must run before acknowledgement.");
                await ReadCapturedRootRescanRequestsCoreAsync(generation, transaction, cancellationToken).ConfigureAwait(false);
            }
            else if (checkpoint.Phase != MirrorPulseFullRescanPhase.Requested)
                throw new InvalidOperationException("A running rescan cannot return to Requested.");
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE full_rescan_checkpoint SET phase=$phase,updated_utc=$updated WHERE singleton=1 AND generation=$generation;";
            command.Parameters.AddWithValue("$phase", (int)phase);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$generation", generation.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The full rescan generation changed.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteFullRescanAsync(Guid generation, CancellationToken cancellationToken = default)
    {
        if (generation == Guid.Empty) throw new ArgumentException("The full rescan generation must not be empty.", nameof(generation));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseFullRescanCheckpoint checkpoint = await ReadFullRescanCoreAsync(cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The full rescan generation changed.");
            if (checkpoint.Generation != generation || checkpoint.Phase != MirrorPulseFullRescanPhase.Acknowledging)
                throw new InvalidOperationException("The completed discovery generation must be acknowledged.");
            await ReadCapturedRootRescanRequestsCoreAsync(generation, transaction, cancellationToken).ConfigureAwait(false);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM full_rescan_checkpoint WHERE singleton=1 AND generation=$generation;";
            command.Parameters.AddWithValue("$generation", generation.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The full rescan generation changed.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseFullRescanCheckpoint?> ReadFullRescanCoreAsync(CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation,phase,updated_utc FROM full_rescan_checkpoint WHERE singleton=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        Guid generation = ReadRescanGuid(reader.GetString(0));
        var phase = (MirrorPulseFullRescanPhase)reader.GetInt32(1);
        if (generation == Guid.Empty || !Enum.IsDefined(phase)) throw new InvalidDataException("The full rescan checkpoint is invalid.");
        return new(generation, phase, ReadRescanTimestamp(reader.GetString(2)));
    }
}
