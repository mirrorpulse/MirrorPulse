using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public enum MirrorPulseMutationState { Prepared, Executing, RemoteAccepted, Ambiguous, Acknowledged, Conflict }
public enum MirrorPulseMutationOrigin { Journal, Rescan }
public sealed record MirrorPulseMutationIntent(Guid OperationId, InstanceId InstanceId, string RootKey,
    MirrorPulseWorkerChangeKind Kind, string RelativePath, string? PreviousRelativePath, bool IsDirectory,
    string? ExpectedRevision, long? ContentLength, string? ContentSha256, MirrorPulseMutationOrigin Origin,
    MirrorPulseUploadBinding? UploadBinding = null,
    string? PreviousRootKey = null);
public sealed record MirrorPulseMutationRecord(MirrorPulseMutationIntent Intent, MirrorPulseMutationState State,
    string? AcceptedRevision, DateTimeOffset UpdatedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseMutationRecord> PrepareMutationAsync(MirrorPulseMutationIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.OperationId == Guid.Empty || intent.InstanceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(intent.RootKey) || string.IsNullOrWhiteSpace(intent.RelativePath) ||
            !Enum.IsDefined(intent.Kind) || !Enum.IsDefined(intent.Origin) || intent.ContentLength < 0 ||
            (intent.ContentSha256 is not null && (intent.ContentSha256.Length != 64 || !intent.ContentSha256.All(Uri.IsHexDigit))))
            throw new ArgumentException("The mutation intent is invalid.", nameof(intent));
        if (intent.UploadBinding is not null)
        {
            intent.UploadBinding.Validate();
            if (intent.IsDirectory || intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
                intent.ContentLength is null || intent.ContentSha256 is null)
                throw new ArgumentException("An upload binding requires a complete file-content intent.", nameof(intent));
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mutation_intents (operation_id, payload, state, accepted_revision, updated_utc)
                VALUES ($operation, $payload, $state, NULL, $updated) ON CONFLICT(operation_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$state", (int)MirrorPulseMutationState.Prepared);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseMutationRecord record = await ReadMutationCoreAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The prepared mutation disappeared.");
            if (!JsonSerializer.SerializeToUtf8Bytes(record.Intent, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("A mutation operation ID was reused with different intent.");
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseMutationRecord?> ReadMutationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadMutationCoreAsync(operationId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseMutationRecord>> ReadIncompleteMutationsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT operation_id, payload, state, accepted_revision, updated_utc FROM mutation_intents WHERE state<>$acknowledged ORDER BY operation_id;";
            command.Parameters.AddWithValue("$acknowledged", (int)MirrorPulseMutationState.Acknowledged);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var values = new List<MirrorPulseMutationRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                values.Add(DecodeMutation(Guid.Parse(reader.GetString(0)), (byte[])reader[1], reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            return values.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task TransitionMutationAsync(Guid operationId, MirrorPulseMutationState from, MirrorPulseMutationState to,
        string? acceptedRevision = null, CancellationToken cancellationToken = default)
    {
        if ((from, to) is not ((MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing) or
            (MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Ambiguous or MirrorPulseMutationState.Conflict) or
            (MirrorPulseMutationState.Ambiguous, MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Conflict) or
            (MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Acknowledged or MirrorPulseMutationState.Conflict)))
            throw new ArgumentException("The mutation transition is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE mutation_intents SET state=$to, accepted_revision=COALESCE($revision,accepted_revision), updated_utc=$updated WHERE operation_id=$operation AND state=$from;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$from", (int)from);
            command.Parameters.AddWithValue("$to", (int)to);
            command.Parameters.AddWithValue("$revision", (object?)acceptedRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The mutation state changed before its transition.");
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseMutationRecord?> ReadMutationCoreAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload,state,accepted_revision,updated_utc FROM mutation_intents WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DecodeMutation(operationId, (byte[])reader[0], reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)) : null;
    }

    private static MirrorPulseMutationRecord DecodeMutation(Guid operationId, byte[] payload, int state, string? revision, string updated)
    {
        MirrorPulseMutationIntent intent = JsonSerializer.Deserialize<MirrorPulseMutationIntent>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The mutation intent is empty.");
        if (intent.OperationId != operationId || !Enum.IsDefined((MirrorPulseMutationState)state))
            throw new InvalidDataException("The mutation intent has invalid identity or state.");
        return new(intent, (MirrorPulseMutationState)state, revision, DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture));
    }
}
