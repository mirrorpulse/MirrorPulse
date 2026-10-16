using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public enum MirrorPulseConflictDeletionPhase { Prepared, Preserved, Retiring, Retired, RemoteAccepted, Completed }

/// <summary>An explicit deletion decision; rejected journal effects remain immutable historical evidence.</summary>
public sealed record MirrorPulseConflictDeletionIntent(int Version, Guid CommandId, Guid ConflictId,
    InstanceId InstanceId, string RootKey, Guid? PlanId, Guid ItemId, string LocalRelativePath,
    MirrorPulseMutationIntent RemoteDeletion, MirrorPulseCoalescingContent? LocalContent,
    IReadOnlyList<Guid> OriginalOperationIds, IReadOnlyList<Guid> ConflictIds, DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseWorkerChangeCommand? OriginalObservation { get; init; }
}

public sealed record MirrorPulseConflictDeletion(MirrorPulseConflictDeletionIntent Intent, MirrorPulseConflictDeletionPhase Phase);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Freezes the complete rejected group and its observed remote condition before any local removal.</summary>
    public Task<MirrorPulseConflictDeletion> PrepareConflictDeletionAsync(Guid commandId, Guid conflictId,
        string rootKey, Guid itemId, string localRelativePath, MirrorPulseCoalescingContent? content,
        CancellationToken cancellationToken = default) =>
        PrepareConflictDeletionCoreAsync(commandId, conflictId, rootKey, itemId, localRelativePath, content, null, cancellationToken);

    /// <summary>Retains an exact public journal observation when a preflight conflict has no remote execution intent.</summary>
    public Task<MirrorPulseConflictDeletion> PrepareConflictDeletionFromObservationAsync(Guid commandId, Guid conflictId,
        string rootKey, Guid itemId, string localRelativePath, MirrorPulseCoalescingContent? content,
        MirrorPulseWorkerChangeCommand observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return PrepareConflictDeletionCoreAsync(commandId, conflictId, rootKey, itemId, localRelativePath, content, observation, cancellationToken);
    }

    private async Task<MirrorPulseConflictDeletion> PrepareConflictDeletionCoreAsync(Guid commandId, Guid conflictId,
        string rootKey, Guid itemId, string localRelativePath, MirrorPulseCoalescingContent? content,
        MirrorPulseWorkerChangeCommand? observation, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty || itemId == Guid.Empty || string.IsNullOrWhiteSpace(rootKey))
            throw new ArgumentException("Conflict deletion requires command, conflict, root and logical item identities.");
        ValidatePermissionRelativePath(localRelativePath);
        if (content is not null)
        {
            content.UploadBinding.Validate();
            if (content.Length < 0 || content.Sha256 is not { Length: 64 } || !content.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Local preservation requires a complete object and byte proof.", nameof(content));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadConflictDeletionCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                var old = replay.Intent;
                if (old.ConflictId != conflictId || old.RootKey != rootKey || old.ItemId != itemId ||
                    old.LocalRelativePath != localRelativePath || old.LocalContent != content || old.OriginalObservation != observation)
                    throw new InvalidDataException("A retained deletion decision cannot replace its original object or content.");
                transaction.Commit(); return replay;
            }
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$command;";
                command.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != "deleteremote" ||
                    reader.GetString(1) != conflictId.ToString("D") || reader.GetString(2) != "pending")
                    throw new InvalidOperationException("A matching pending user command must authorize deletion.");
            }
            var selected = await ReadDeletionConflictAsync(conflictId, transaction, cancellationToken).ConfigureAwait(false);
            if (observation is not null) ValidateDeletionObservation(observation, selected.OriginalId, selected.InstanceId, rootKey, itemId);
            Guid? planId = null;
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction;
                owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$operation;";
                owner.Parameters.AddWithValue("$operation", selected.OriginalId.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value) planId = Guid.Parse(value);
            }
            var originals = new List<Guid>(); var conflicts = new List<Guid>();
            string remoteTarget;
            if (planId is { } ownerId)
            {
                if (observation is not null) throw new ArgumentException("A single observation cannot replace an owned group's complete execution.", nameof(observation));
                var plan = await ReadCoalescingPlanCoreAsync(ownerId, transaction, cancellationToken).ConfigureAwait(false);
                var execution = await ReadCoalescingExecutionCoreAsync(ownerId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The rejected group lost its original execution.");
                if (plan.ItemId != itemId || plan.InstanceId != selected.InstanceId || plan.RootKey != rootKey)
                    throw new InvalidDataException("Deletion cannot adopt another group's logical item or root.");
                string? baseline = execution.ExpectedRevision;
                MirrorPulseMutationIntent? current = null;
                foreach (var step in execution.Steps)
                {
                    var intent = await ReadCurrentCoalescingIntentAsync(plan, execution, step.Ordinal, baseline, transaction, cancellationToken).ConfigureAwait(false);
                    var record = await ReadMutationCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                    if (record is { State: MirrorPulseMutationState.Acknowledged })
                    {
                        if (step.Kind == MirrorPulseCoalescingStepKind.Move) baseline = record.AcceptedRevision;
                        continue;
                    }
                    if (record is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(record.Intent, intent))
                        throw new InvalidOperationException("Unknown outcomes require reconciliation before a destructive conflict decision.");
                    // Two absence checks may describe different remote occupants. One choice
                    // cannot silently authorize deleting an unobserved second occupant.
                    if (execution.Steps.Skip(step.Ordinal + 1).Any(later => later.Kind != MirrorPulseCoalescingStepKind.Upload))
                        throw new NotSupportedException("Remaining remote verification requires its own explicit decision.");
                    current = intent; break;
                }
                var rejected = current ?? throw new InvalidOperationException("The group has no current rejected effect.");
                remoteTarget = rejected.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.PreviousRelativePath! : rejected.RelativePath;
                foreach (var member in plan.Members)
                {
                    await using var query = _connection.CreateCommand(); query.Transaction = transaction;
                    query.CommandText = "SELECT conflict_id FROM upload_conflicts WHERE change_id=$original AND status=0;";
                    query.Parameters.AddWithValue("$original", member.OperationId.ToString("D"));
                    await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("Every original group member requires pending conflict evidence.");
                    Guid id = Guid.Parse(reader.GetString(0));
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("A group member has multiple pending conflict epochs.");
                    originals.Add(member.OperationId); conflicts.Add(id);
                }
            }
            else
            {
                var record = await ReadMutationCoreAsync(selected.OriginalId, cancellationToken, transaction).ConfigureAwait(false);
                if (record is null)
                {
                    if (observation is null) throw new InvalidOperationException("A preflight conflict requires its exact public journal observation.");
                    remoteTarget = observation.Kind == MirrorPulseWorkerChangeKind.Move ? observation.PreviousRelativePath! : observation.RelativePath;
                }
                else
                {
                    if (record is not { State: MirrorPulseMutationState.Conflict, Intent.Origin: MirrorPulseMutationOrigin.Journal } ||
                        record.Intent.InstanceId != selected.InstanceId || record.Intent.RootKey != rootKey || record.Intent.IsDirectory ||
                        observation is not null && (record.Intent.Kind != observation.Kind || record.Intent.RelativePath != observation.RelativePath ||
                            record.Intent.PreviousRelativePath != observation.PreviousRelativePath))
                        throw new InvalidOperationException("An existing original mutation must remain explicitly rejected and match its public observation.");
                    remoteTarget = record.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? record.Intent.PreviousRelativePath! : record.Intent.RelativePath;
                }
                originals.Add(selected.OriginalId); conflicts.Add(conflictId);
            }
            if (!conflicts.Contains(conflictId)) throw new InvalidDataException("The selected conflict is not the current group epoch.");
            foreach (Guid id in conflicts)
            {
                var evidence = await ReadDeletionConflictAsync(id, transaction, cancellationToken).ConfigureAwait(false);
                if (evidence.InstanceId != selected.InstanceId || evidence.Revision != selected.Revision || evidence.LocalPath != localRelativePath)
                    throw new InvalidDataException("The complete group's conflict provenance or observed remote revisions disagree.");
            }
            if (string.IsNullOrEmpty(selected.Revision))
                throw new InvalidOperationException("Remote deletion requires an explicitly observed remote revision.");
            foreach (Guid original in originals)
            {
                await using var query = _connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$original;";
                query.Parameters.AddWithValue("$original", original.ToString("D"));
                if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An original operation already belongs to a retained deletion decision.");
            }
            Guid remoteId = new(SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.ConflictDeletion.v1/" + commandId.ToString("D"))).AsSpan(0, 16));
            await RequireUnownedCoalescingEffectAsync(remoteId, transaction, cancellationToken).ConfigureAwait(false);
            var remote = new MirrorPulseMutationIntent(remoteId, selected.InstanceId, rootKey, MirrorPulseWorkerChangeKind.Delete,
                remoteTarget,
                null, false, selected.Revision, null, null, MirrorPulseMutationOrigin.ConflictDecision);
            var deletion = new MirrorPulseConflictDeletionIntent(observation is null ? 1 : 2, commandId, conflictId, selected.InstanceId, rootKey, planId,
                itemId, localRelativePath, remote, content, originals.AsReadOnly(), conflicts.AsReadOnly(), DateTimeOffset.UtcNow)
            { OriginalObservation = observation };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(deletion, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_deletions(command_id,remote_operation_id,payload,fingerprint,phase) VALUES($command,$remote,$payload,$hash,0);";
                insert.Parameters.AddWithValue("$command", commandId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$remote", remoteId.ToString("D"));
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (Guid original in originals)
            {
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_deletion_members(original_id,command_id) VALUES($original,$command);";
                insert.Parameters.AddWithValue("$original", original.ToString("D")); insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            _ = await PrepareMutationCoreAsync(remote, JsonSerializer.SerializeToUtf8Bytes(remote, TopologyJsonOptions), cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit(); return new(deletion, MirrorPulseConflictDeletionPhase.Prepared);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictDeletion?> ReadConflictDeletionAsync(Guid commandId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadConflictDeletionCoreAsync(commandId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Guid>> ReadPendingConflictDeletionsAsync(Guid? after = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_deletions WHERE phase<5 AND ($after IS NULL OR command_id>$after) ORDER BY command_id LIMIT 64;";
            query.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
            var result = new List<Guid>(); await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> ReadConflictDeletionOwnerAsync(Guid originalId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_deletion_members WHERE original_id=$original;";
            query.Parameters.AddWithValue("$original", originalId.ToString("D"));
            return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records one completed boundary. The Host verifies local receipts and official acknowledgement separately.</summary>
    public async Task AdvanceConflictDeletionAsync(Guid commandId, MirrorPulseConflictDeletionPhase from,
        MirrorPulseConflictDeletionPhase to, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || (int)to != (int)from + 1)
            throw new ArgumentException("Deletion boundaries must complete in order.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var deletion = await ReadConflictDeletionCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retained deletion decision is missing.");
            if (deletion.Phase != from) throw new InvalidOperationException("The deletion phase changed before completion.");
            if (to >= MirrorPulseConflictDeletionPhase.RemoteAccepted)
            {
                var current = await ReadCurrentDeletionEffectCoreAsync(deletion, transaction, cancellationToken).ConfigureAwait(false);
                var remote = await ReadMutationCoreAsync(current.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (remote is not { State: MirrorPulseMutationState.Acknowledged } || !SameIntent(remote.Intent, current) || remote.AcceptedRevision is not null)
                    throw new InvalidOperationException("Remote deletion requires its own completed conditional effect, not an original rejected mutation.");
            }
            await using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction; update.CommandText = "UPDATE conflict_deletions SET phase=$to WHERE command_id=$command AND phase=$from;";
                update.Parameters.AddWithValue("$command", commandId.ToString("D")); update.Parameters.AddWithValue("$from", (int)from); update.Parameters.AddWithValue("$to", (int)to);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("The deletion receipt changed.");
            }
            if (to == MirrorPulseConflictDeletionPhase.Completed)
            {
                var current = await ReadCurrentDeletionEffectCoreAsync(deletion, transaction, cancellationToken).ConfigureAwait(false);
                for (int ordinal = 0; ordinal < deletion.Intent.OriginalOperationIds.Count; ordinal++)
                    await ResolvePendingDeletionConflictAsync(deletion, current.OperationId, ordinal, transaction, cancellationToken).ConfigureAwait(false);
                await using var command = _connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE user_commands SET state='resolved' WHERE command_id=$id AND action='deleteremote' AND target_id=$target;";
                command.Parameters.AddWithValue("$id", commandId.ToString("D")); command.Parameters.AddWithValue("$target", deletion.Intent.ConflictId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidDataException("The deletion command lost its explicit authorization.");
                await using (var retries = _connection.CreateCommand())
                {
                    retries.Transaction = transaction;
                    retries.CommandText = "UPDATE user_commands SET state='resolved' WHERE state IN ('pending','failed') AND command_id IN (SELECT command_id FROM conflict_deletion_retries WHERE decision_id=$decision);";
                    retries.Parameters.AddWithValue("$decision", commandId.ToString("D"));
                    await retries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                if (deletion.Intent.PlanId is { } planId)
                {
                    await using var prior = _connection.CreateCommand(); prior.Transaction = transaction;
                    prior.CommandText = "UPDATE user_commands SET state='superseded' WHERE state IN ('pending','failed') AND command_id IN (SELECT command_id FROM journal_coalescing_retries WHERE plan_id=$plan);";
                    prior.Parameters.AddWithValue("$plan", planId.ToString("D"));
                    await prior.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictDeletion?> ReadConflictDeletionCoreAsync(Guid commandId, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint,phase FROM conflict_deletions WHERE command_id=$id;";
        query.Parameters.AddWithValue("$id", commandId.ToString("D"));
        MirrorPulseConflictDeletionIntent intent; MirrorPulseConflictDeletionPhase phase;
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0];
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[1])) throw new InvalidDataException("The deletion decision fingerprint changed.");
            intent = JsonSerializer.Deserialize<MirrorPulseConflictDeletionIntent>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The deletion decision is empty.");
            phase = (MirrorPulseConflictDeletionPhase)reader.GetInt32(2);
        }
        if (intent.Version is not (1 or 2) || (intent.Version == 2) != (intent.OriginalObservation is not null) || intent.CommandId != commandId || intent.ConflictId == Guid.Empty || intent.ItemId == Guid.Empty ||
            intent.PreparedAt == default || !Enum.IsDefined(phase) || intent.OriginalOperationIds is not { Count: > 0 } ||
            intent.ConflictIds is null || intent.ConflictIds.Count != intent.OriginalOperationIds.Count ||
            intent.OriginalOperationIds.Distinct().Count() != intent.OriginalOperationIds.Count ||
            intent.ConflictIds.Distinct().Count() != intent.ConflictIds.Count || !intent.ConflictIds.Contains(intent.ConflictId) ||
            intent.RemoteDeletion is not { Kind: MirrorPulseWorkerChangeKind.Delete, IsDirectory: false, Origin: MirrorPulseMutationOrigin.ConflictDecision } ||
            intent.RemoteDeletion.InstanceId != intent.InstanceId || intent.RemoteDeletion.RootKey != intent.RootKey ||
            string.IsNullOrEmpty(intent.RemoteDeletion.ExpectedRevision) || intent.RemoteDeletion.OperationId !=
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.ConflictDeletion.v1/" + commandId.ToString("D"))).AsSpan(0, 16)))
            throw new InvalidDataException("The retained deletion decision lost its original provenance.");
        if (intent.OriginalObservation is { } observation)
        {
            if (intent.PlanId is not null || intent.OriginalOperationIds.Count != 1)
                throw new InvalidDataException("A preflight deletion lost its original single-operation scope.");
            ValidateDeletionObservation(observation, intent.OriginalOperationIds[0], intent.InstanceId, intent.RootKey, intent.ItemId);
            if (intent.RemoteDeletion.RelativePath != (observation.Kind == MirrorPulseWorkerChangeKind.Move ? observation.PreviousRelativePath : observation.RelativePath))
                throw new InvalidDataException("The preflight deletion target changed from its original public observation.");
        }
        ValidatePermissionRelativePath(intent.LocalRelativePath);
        ValidatePermissionRelativePath(intent.RemoteDeletion.RelativePath);
        intent.LocalContent?.UploadBinding.Validate();
        await using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "SELECT action,target_id FROM user_commands WHERE command_id=$id;";
            command.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "deleteremote" || reader.GetString(1) != intent.ConflictId.ToString("D"))
                throw new InvalidDataException("The deletion decision lost its explicit user authorization.");
        }
        var originals = new HashSet<Guid>();
        await using (var members = _connection.CreateCommand())
        {
            members.Transaction = transaction; members.CommandText = "SELECT original_id FROM conflict_deletion_members WHERE command_id=$id;";
            members.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await members.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) originals.Add(Guid.Parse(reader.GetString(0)));
        }
        if (!originals.SetEquals(intent.OriginalOperationIds)) throw new InvalidDataException("The deletion decision lost original journal ownership.");
        return new(intent with
        {
            OriginalOperationIds = Array.AsReadOnly(intent.OriginalOperationIds.ToArray()),
            ConflictIds = Array.AsReadOnly(intent.ConflictIds.ToArray())
        }, phase);
    }

    private async Task<(Guid OriginalId, InstanceId InstanceId, string LocalPath, string? Revision)> ReadDeletionConflictAsync(
        Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT change_id,instance_id,relative_path,remote_revision,status FROM upload_conflicts WHERE conflict_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(4) != 0 || !Guid.TryParseExact(reader.GetString(0), "D", out Guid original))
            throw new InvalidOperationException("Deletion requires the original pending upload conflict.");
        return (original, InstanceId.Parse(reader.GetString(1)), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task EnsureConflictDeletionSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conflict_deletions(
                command_id TEXT PRIMARY KEY, remote_operation_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, fingerprint BLOB NOT NULL,
                phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 5));
            CREATE TABLE IF NOT EXISTS conflict_deletion_members(
                original_id TEXT PRIMARY KEY, command_id TEXT NOT NULL REFERENCES conflict_deletions(command_id));
            CREATE INDEX IF NOT EXISTS conflict_deletions_pending ON conflict_deletions(phase,command_id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void ValidateDeletionObservation(MirrorPulseWorkerChangeCommand observation, Guid originalId, InstanceId instanceId, string rootKey, Guid itemId)
    {
        if (observation.OperationId != originalId || observation.Sequence <= 0 || observation.InstanceId != instanceId ||
            observation.RootKey != rootKey || observation.ItemId != itemId || observation.IsDirectory || observation.ObservedAt == default ||
            observation.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate or MirrorPulseWorkerChangeKind.Move or MirrorPulseWorkerChangeKind.Delete) ||
            observation.Kind == MirrorPulseWorkerChangeKind.Move && (observation.PreviousRootKey != rootKey || observation.PreviousRelativePath is null))
            throw new InvalidDataException("The preflight decision requires the exact original routable file observation.");
        ValidatePermissionRelativePath(observation.RelativePath);
        if (observation.PreviousRelativePath is not null) ValidatePermissionRelativePath(observation.PreviousRelativePath);
    }
}
