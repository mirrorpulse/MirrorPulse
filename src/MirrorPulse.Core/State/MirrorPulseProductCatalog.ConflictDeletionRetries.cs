using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public enum MirrorPulseConflictDeletionObservationOrigin { WorkerRejection, Readback }

/// <summary>A retained remote observation; it does not authorize changing the previous conditional effect.</summary>
public sealed record MirrorPulseConflictDeletionObservation(int Version, Guid DecisionId, Guid OperationId,
    string? ExpectedRevision, string? ObservedRevision, MirrorPulseConflictDeletionObservationOrigin Origin, DateTimeOffset ObservedAt);

/// <summary>A new explicit user choice with its own conditional effect; original local preservation and journal ownership remain unchanged.</summary>
public sealed record MirrorPulseConflictDeletionRetry(int Version, Guid CommandId, Guid DecisionId, Guid ConflictId,
    Guid PreviousOperationId, MirrorPulseMutationIntent Intent, DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseMutationIntent> ReadCurrentConflictDeletionEffectAsync(Guid decisionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var decision = await ReadConflictDeletionCoreAsync(decisionId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original deletion decision is missing.");
            var result = await ReadCurrentDeletionEffectCoreAsync(decision, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictDeletionObservation?> ReadConflictDeletionObservationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadDeletionObservationCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Persists a real rejection or a separate readback for an already rejected effect. No mutation is dispatched here.</summary>
    public async Task RecordConflictDeletionObservationAsync(MirrorPulseConflictDeletionObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation); ValidateDeletionObservationFact(observation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var decision = await ReadConflictDeletionCoreAsync(observation.DecisionId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original deletion decision is missing.");
            var intent = await ReadCurrentDeletionEffectCoreAsync(decision, transaction, cancellationToken).ConfigureAwait(false);
            var rejected = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            if (decision.Phase != MirrorPulseConflictDeletionPhase.Retired || rejected is not { State: MirrorPulseMutationState.Conflict } ||
                !SameIntent(rejected.Intent, intent) || observation.OperationId != intent.OperationId || observation.ExpectedRevision != intent.ExpectedRevision)
                throw new InvalidOperationException("Only the current rejected conditional deletion may retain a new remote observation.");
            var replay = await ReadDeletionObservationCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != observation) throw new InvalidDataException("A rejected effect's first retained observation is immutable.");
                transaction.Commit(); return;
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(observation, TopologyJsonOptions);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO conflict_deletion_observations(operation_id,decision_id,payload,fingerprint) VALUES($operation,$decision,$payload,$hash);";
            insert.Parameters.AddWithValue("$operation", observation.OperationId.ToString("D")); insert.Parameters.AddWithValue("$decision", observation.DecisionId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ReplaceDeletionConflictEpochAsync(decision, intent.OperationId, observation, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Authorizes exactly one new effect against the revision shown by the retained current observation.</summary>
    public async Task<MirrorPulseConflictDeletionRetry> AuthorizeConflictDeletionRetryAsync(Guid commandId, Guid decisionId,
        Guid conflictId, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || decisionId == Guid.Empty || conflictId == Guid.Empty || commandId == decisionId)
            throw new ArgumentException("A distinct user command, original decision and selected conflict are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var decision = await ReadConflictDeletionCoreAsync(decisionId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original deletion decision is missing.");
            await using (var replay = _connection.CreateCommand())
            {
                replay.Transaction = transaction; replay.CommandText = "SELECT operation_id,payload,fingerprint FROM conflict_deletion_retries WHERE command_id=$command;";
                replay.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var retained = DecodeDeletionRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0), decision);
                    if (retained.ConflictId != conflictId) throw new InvalidDataException("A retry cannot change its selected conflict.");
                    await reader.DisposeAsync().ConfigureAwait(false);
                    _ = await ReadCurrentDeletionEffectCoreAsync(decision, transaction, cancellationToken).ConfigureAwait(false);
                    transaction.Commit(); return retained;
                }
            }
            if (decision.Phase != MirrorPulseConflictDeletionPhase.Retired) throw new InvalidOperationException("Only a rejected remote deletion may receive a new user decision.");
            await RequireDeletionRetryCommandAsync(commandId, conflictId, true, transaction, cancellationToken).ConfigureAwait(false);
            var intent = await ReadCurrentDeletionEffectCoreAsync(decision, transaction, cancellationToken).ConfigureAwait(false);
            var previous = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            var observation = await ReadDeletionObservationCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (previous is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(previous.Intent, intent) ||
                observation is null || observation.DecisionId != decisionId || observation.ExpectedRevision != intent.ExpectedRevision)
                throw new InvalidOperationException("A new deletion choice requires the exact rejected effect and its retained remote observation; unknown outcomes require readback.");
            var selected = await ReadDeletionConflictAsync(conflictId, transaction, cancellationToken).ConfigureAwait(false);
            if (selected.InstanceId != decision.Intent.InstanceId || !decision.Intent.OriginalOperationIds.Contains(selected.OriginalId) ||
                conflictId != DeletionConflictId(intent.OperationId, selected.OriginalId) || selected.Revision != observation.ObservedRevision ||
                selected.LocalPath != decision.Intent.LocalRelativePath)
                throw new InvalidOperationException("The user must select the current rejected deletion epoch, not a previous conflict or unseen remote version.");
            foreach (Guid original in decision.Intent.OriginalOperationIds)
            {
                var current = await ReadDeletionConflictAsync(DeletionConflictId(intent.OperationId, original), transaction, cancellationToken).ConfigureAwait(false);
                if (current.OriginalId != original || current.InstanceId != decision.Intent.InstanceId || current.Revision != observation.ObservedRevision ||
                    current.LocalPath != decision.Intent.LocalRelativePath)
                    throw new InvalidDataException("The complete deletion conflict epoch changed before authorization.");
            }
            Guid effectId = DeletionRetryEffectId(commandId);
            await RequireUnownedCoalescingEffectAsync(effectId, transaction, cancellationToken).ConfigureAwait(false);
            var next = intent with { OperationId = effectId, ExpectedRevision = observation.ObservedRevision };
            var retry = new MirrorPulseConflictDeletionRetry(1, commandId, decisionId, conflictId, intent.OperationId, next, DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retry, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_deletion_retries(command_id,operation_id,decision_id,previous_operation_id,payload,fingerprint) VALUES($command,$operation,$decision,$previous,$payload,$hash);";
                insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$operation", effectId.ToString("D"));
                insert.Parameters.AddWithValue("$decision", decisionId.ToString("D")); insert.Parameters.AddWithValue("$previous", intent.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            _ = await PrepareMutationCoreAsync(next, JsonSerializer.SerializeToUtf8Bytes(next, TopologyJsonOptions), cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit(); return retry;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseMutationIntent> ReadCurrentDeletionEffectCoreAsync(MirrorPulseConflictDeletion decision,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseConflictDeletionRetry? retry;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint FROM conflict_deletion_retries WHERE decision_id=$decision ORDER BY sequence DESC LIMIT 1;";
            query.Parameters.AddWithValue("$decision", decision.Intent.CommandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            retry = await reader.ReadAsync(token).ConfigureAwait(false) ? DecodeDeletionRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0), decision) : null;
        }
        if (retry is null) return decision.Intent.RemoteDeletion;
        var result = retry.Intent; var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(retry.Intent.OperationId)) throw new InvalidDataException("The deletion retry history contains a cycle.");
            await RequireDeletionRetryCommandAsync(retry.CommandId, retry.ConflictId, false, transaction, token).ConfigureAwait(false);
            var previous = await ReadMutationCoreAsync(retry.PreviousOperationId, token, transaction).ConfigureAwait(false);
            var observation = await ReadDeletionObservationCoreAsync(retry.PreviousOperationId, transaction, token).ConfigureAwait(false);
            if (previous is not { State: MirrorPulseMutationState.Conflict } || observation is null ||
                observation.DecisionId != decision.Intent.CommandId || observation.ExpectedRevision != previous.Intent.ExpectedRevision ||
                observation.ObservedRevision != retry.Intent.ExpectedRevision)
                throw new InvalidDataException("The deletion retry lost its rejected predecessor or observed condition.");
            if (retry.PreviousOperationId == decision.Intent.RemoteDeletion.OperationId)
            {
                if (!SameIntent(previous.Intent, decision.Intent.RemoteDeletion)) throw new InvalidDataException("The first rejected deletion changed.");
                return result;
            }
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint FROM conflict_deletion_retries WHERE operation_id=$operation AND decision_id=$decision;";
            query.Parameters.AddWithValue("$operation", retry.PreviousOperationId.ToString("D")); query.Parameters.AddWithValue("$decision", decision.Intent.CommandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("A prior deletion choice disappeared.");
            var prior = DecodeDeletionRetry((byte[])reader[1], (byte[])reader[2], reader.GetString(0), decision);
            if (!SameIntent(prior.Intent, previous.Intent)) throw new InvalidDataException("The previous deletion retry changed.");
            retry = prior;
        }
    }

    private async Task<MirrorPulseConflictDeletionObservation?> ReadDeletionObservationCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT decision_id,payload,fingerprint FROM conflict_deletion_observations WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[1];
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[2])) throw new InvalidDataException("The deletion observation fingerprint changed.");
        var observation = JsonSerializer.Deserialize<MirrorPulseConflictDeletionObservation>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The deletion observation is empty.");
        ValidateDeletionObservationFact(observation);
        if (observation.OperationId != operationId || observation.DecisionId.ToString("D") != reader.GetString(0))
            throw new InvalidDataException("The deletion observation changed its original effect ownership.");
        return observation;
    }

    private async Task RequireDeletionRetryCommandAsync(Guid commandId, Guid conflictId, bool pending,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$command;";
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "deleteremote" || reader.GetString(1) != conflictId.ToString("D") ||
            (pending ? reader.GetString(2) != "pending" : reader.GetString(2) is not ("pending" or "failed" or "resolved")))
            throw new InvalidOperationException("A matching explicit deletion command must authorize the new conditional effect.");
    }

    private static MirrorPulseConflictDeletionRetry DecodeDeletionRetry(byte[] payload, byte[] fingerprint, string operationId, MirrorPulseConflictDeletion decision)
    {
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint)) throw new InvalidDataException("The deletion retry fingerprint changed.");
        var retry = JsonSerializer.Deserialize<MirrorPulseConflictDeletionRetry>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The deletion retry is empty.");
        if (retry.Version != 1 || retry.CommandId == Guid.Empty || retry.CommandId == decision.Intent.CommandId || retry.DecisionId != decision.Intent.CommandId ||
            !decision.Intent.OriginalOperationIds.Any(original => DeletionConflictId(retry.PreviousOperationId, original) == retry.ConflictId) ||
            retry.PreviousOperationId == Guid.Empty || retry.Intent is null || retry.PreparedAt == default ||
            retry.Intent.OperationId != DeletionRetryEffectId(retry.CommandId) || retry.Intent.OperationId.ToString("D") != operationId ||
            retry.Intent.OperationId == retry.PreviousOperationId || retry.Intent.ExpectedRevision == string.Empty ||
            !SameIntent(retry.Intent with { OperationId = decision.Intent.RemoteDeletion.OperationId, ExpectedRevision = decision.Intent.RemoteDeletion.ExpectedRevision }, decision.Intent.RemoteDeletion))
            throw new InvalidDataException("A deletion retry cannot change its original target, ownership or local preservation decision.");
        return retry;
    }

    private static Guid DeletionRetryEffectId(Guid commandId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "MirrorPulse.ConflictDeletion.Retry.v1/" + commandId.ToString("D"))).AsSpan(0, 16));

    private static Guid DeletionConflictId(Guid operationId, Guid originalId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "MirrorPulse.ConflictDeletion.Conflict.v1/" + operationId.ToString("D") + "/" + originalId.ToString("D"))).AsSpan(0, 16));

    private async Task<Guid> ReadPreviousDeletionConflictIdAsync(MirrorPulseConflictDeletion decision, Guid effectId, int ordinal,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (effectId == decision.Intent.RemoteDeletion.OperationId) return decision.Intent.ConflictIds[ordinal];
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT previous_operation_id FROM conflict_deletion_retries WHERE operation_id=$operation AND decision_id=$decision;";
        command.Parameters.AddWithValue("$operation", effectId.ToString("D")); command.Parameters.AddWithValue("$decision", decision.Intent.CommandId.ToString("D"));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string previous || !Guid.TryParseExact(previous, "D", out Guid id))
            throw new InvalidDataException("The current deletion lost its explicitly authorized conflict epoch.");
        return DeletionConflictId(id, decision.Intent.OriginalOperationIds[ordinal]);
    }

    private async Task ResolvePendingDeletionConflictAsync(MirrorPulseConflictDeletion decision, Guid effectId, int ordinal,
        SqliteTransaction transaction, CancellationToken token)
    {
        Guid id = await ReadPreviousDeletionConflictIdAsync(decision, effectId, ordinal, transaction, token).ConfigureAwait(false);
        var pending = await ReadDeletionConflictAsync(id, transaction, token).ConfigureAwait(false);
        if (pending.InstanceId != decision.Intent.InstanceId || pending.OriginalId != decision.Intent.OriginalOperationIds[ordinal] ||
            pending.LocalPath != decision.Intent.LocalRelativePath)
            throw new InvalidDataException("The deletion lost its current complete original conflict scope.");
        await using var update = _connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE upload_conflicts SET status=1 WHERE conflict_id=$id AND status=0;";
        update.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The current deletion conflict epoch changed.");
    }

    private async Task ReplaceDeletionConflictEpochAsync(MirrorPulseConflictDeletion decision, Guid effectId,
        MirrorPulseConflictDeletionObservation observation, SqliteTransaction transaction, CancellationToken token)
    {
        for (int ordinal = 0; ordinal < decision.Intent.OriginalOperationIds.Count; ordinal++)
        {
            await ResolvePendingDeletionConflictAsync(decision, effectId, ordinal, transaction, token).ConfigureAwait(false);
            // A distinct public epoch prevents a choice made against an old list
            // from accepting a newer unseen remote revision. Original facts stay.
            Guid id = DeletionConflictId(effectId, decision.Intent.OriginalOperationIds[ordinal]);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO upload_conflicts(conflict_id,instance_id,change_id,relative_path,reason,version_comparison,local_revision,remote_revision,detected_utc,status)
                VALUES($id,$instance,$original,$path,$reason,$comparison,$expected,$observed,$time,0);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D")); insert.Parameters.AddWithValue("$instance", decision.Intent.InstanceId.ToString());
            insert.Parameters.AddWithValue("$original", decision.Intent.OriginalOperationIds[ordinal].ToString("D"));
            insert.Parameters.AddWithValue("$path", decision.Intent.LocalRelativePath); insert.Parameters.AddWithValue("$reason", (int)MirrorPulseConflictReason.StaleRemoteRevision);
            insert.Parameters.AddWithValue("$comparison", (int)MirrorPulseVersionComparison.Diverged);
            insert.Parameters.AddWithValue("$expected", (object?)observation.ExpectedRevision ?? DBNull.Value);
            insert.Parameters.AddWithValue("$observed", (object?)observation.ObservedRevision ?? DBNull.Value);
            insert.Parameters.AddWithValue("$time", observation.ObservedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static void ValidateDeletionObservationFact(MirrorPulseConflictDeletionObservation observation)
    {
        if (observation.Version != 1 || observation.DecisionId == Guid.Empty || observation.OperationId == Guid.Empty || observation.ObservedAt == default ||
            !Enum.IsDefined(observation.Origin) || observation.ExpectedRevision == string.Empty || observation.ObservedRevision == string.Empty)
            throw new InvalidDataException("A deletion observation requires its original effect and complete typed remote result.");
    }

    private static async Task EnsureConflictDeletionRetrySchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conflict_deletion_observations(
                operation_id TEXT PRIMARY KEY, decision_id TEXT NOT NULL REFERENCES conflict_deletions(command_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS conflict_deletion_retries(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
                decision_id TEXT NOT NULL REFERENCES conflict_deletions(command_id), previous_operation_id TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS conflict_deletion_retry_current ON conflict_deletion_retries(decision_id,sequence DESC);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
