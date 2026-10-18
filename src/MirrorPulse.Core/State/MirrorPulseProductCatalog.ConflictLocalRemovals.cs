using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public enum MirrorPulseConflictLocalRemovalPhase { Prepared, Preserved, Retiring, Retired, Completed }

/// <summary>An explicit local-only choice. It owns no remote mutation or remote acceptance receipt.</summary>
public sealed record MirrorPulseConflictLocalRemovalIntent(int Version, Guid CommandId, Guid ConflictId,
    InstanceId InstanceId, string RootKey, Guid? PlanId, string? PlanFingerprint, Guid ItemId,
    string LocalRelativePath, MirrorPulseCoalescingContent? LocalContent, MirrorPulseMutationIntent? RejectedEffect,
    IReadOnlyList<Guid> OriginalOperationIds, IReadOnlyList<Guid> ConflictIds, DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseWorkerChangeCommand? OriginalObservation { get; init; }
}

public sealed record MirrorPulseConflictLocalRemoval(MirrorPulseConflictLocalRemovalIntent Intent,
    MirrorPulseConflictLocalRemovalPhase Phase);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Freezes a local-only choice and all original IDs before preservation or controlled retirement.</summary>
    public async Task<MirrorPulseConflictLocalRemoval> PrepareConflictLocalRemovalAsync(Guid commandId, Guid conflictId,
        string rootKey, Guid itemId, string localPath, MirrorPulseCoalescingContent? content,
        MirrorPulseWorkerChangeCommand? observation = null, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty || itemId == Guid.Empty || string.IsNullOrWhiteSpace(rootKey))
            throw new ArgumentException("Local removal requires command, conflict, root and logical item identities.");
        ValidatePermissionRelativePath(localPath);
        if (content is not null) ValidateCoalescingDecisionContent(content);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadConflictLocalRemovalCoreAsync(commandId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                var old = replay.Intent;
                if (old.ConflictId != conflictId || old.RootKey != rootKey || old.ItemId != itemId || old.LocalRelativePath != localPath ||
                    old.LocalContent != content || old.OriginalObservation != observation)
                    throw new InvalidDataException("A local-only choice cannot replace its original object or content.");
                transaction.Commit(); return replay;
            }
            await RequireLocalRemovalCommandAsync(commandId, conflictId, transaction, true, cancellationToken).ConfigureAwait(false);
            var selected = await ReadConflictDecisionSelectionAsync(conflictId, rootKey, itemId, localPath, observation,
                false, transaction, cancellationToken).ConfigureAwait(false);
            foreach (Guid original in selected.Originals)
            {
                await using var owner = _connection.CreateCommand(); owner.Transaction = transaction;
                owner.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_keep_both_owners WHERE original_id=$id LIMIT 1;";
                owner.Parameters.AddWithValue("$id", original.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An original operation already belongs to an explicit conflict decision.");
            }
            var intent = new MirrorPulseConflictLocalRemovalIntent(1, commandId, conflictId, selected.InstanceId, rootKey,
                selected.PlanId, selected.PlanFingerprint, itemId, localPath, content, selected.RejectedEffect,
                selected.Originals, selected.Conflicts, DateTimeOffset.UtcNow)
            { OriginalObservation = observation };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_local_removals(command_id,payload,fingerprint,phase) VALUES($id,$payload,$hash,0);";
                insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (Guid original in intent.OriginalOperationIds)
            {
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_local_removal_members(original_id,command_id) VALUES($original,$command);";
                insert.Parameters.AddWithValue("$original", original.ToString("D")); insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return new(intent, MirrorPulseConflictLocalRemovalPhase.Prepared);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictLocalRemoval?> ReadConflictLocalRemovalAsync(Guid commandId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadConflictLocalRemovalCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> ReadConflictLocalRemovalOwnerAsync(Guid originalId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_local_removal_members WHERE original_id=$id;";
            query.Parameters.AddWithValue("$id", originalId.ToString("D"));
            return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string value ? Guid.Parse(value) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Guid>> ReadPendingConflictLocalRemovalsAsync(Guid? after = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_local_removals WHERE phase<4 AND ($after IS NULL OR command_id>$after) ORDER BY command_id LIMIT 64;";
            query.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
            var result = new List<Guid>(); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records Host-verified local boundaries; completion follows the official whole-group acknowledgement.</summary>
    public async Task AdvanceConflictLocalRemovalAsync(Guid commandId, MirrorPulseConflictLocalRemovalPhase from,
        MirrorPulseConflictLocalRemovalPhase to, CancellationToken token = default)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || (int)to != (int)from + 1)
            throw new ArgumentException("Local-only boundaries must complete in order.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var removal = await ReadConflictLocalRemovalCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The local-only decision is missing.");
            if (removal.Phase != from) throw new InvalidOperationException("The local-only phase changed before completion.");
            await using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE conflict_local_removals SET phase=$to WHERE command_id=$id AND phase=$from;";
                update.Parameters.AddWithValue("$id", commandId.ToString("D")); update.Parameters.AddWithValue("$from", (int)from); update.Parameters.AddWithValue("$to", (int)to);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("The local-only receipt changed.");
            }
            if (to == MirrorPulseConflictLocalRemovalPhase.Completed)
            {
                for (int index = 0; index < removal.Intent.ConflictIds.Count; index++)
                {
                    await using var resolve = _connection.CreateCommand(); resolve.Transaction = transaction;
                    resolve.CommandText = "UPDATE upload_conflicts SET status=1 WHERE conflict_id=$id AND change_id=$original AND status=0;";
                    resolve.Parameters.AddWithValue("$id", removal.Intent.ConflictIds[index].ToString("D"));
                    resolve.Parameters.AddWithValue("$original", removal.Intent.OriginalOperationIds[index].ToString("D"));
                    if (await resolve.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The original local-only conflict epoch changed.");
                }
                await using var command = _connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE user_commands SET state='resolved' WHERE command_id=$id AND action='deletelocal' AND target_id=$target AND state IN ('pending','failed');";
                command.Parameters.AddWithValue("$id", commandId.ToString("D")); command.Parameters.AddWithValue("$target", removal.Intent.ConflictId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The local-only command lost its authorization.");
                if (removal.Intent.PlanId is { } planId)
                {
                    await using var prior = _connection.CreateCommand(); prior.Transaction = transaction;
                    prior.CommandText = "UPDATE user_commands SET state='superseded' WHERE state IN ('pending','failed') AND command_id IN (SELECT command_id FROM journal_coalescing_retries WHERE plan_id=$plan);";
                    prior.Parameters.AddWithValue("$plan", planId.ToString("D")); await prior.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictLocalRemoval?> ReadConflictLocalRemovalCoreAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseConflictLocalRemovalIntent intent; MirrorPulseConflictLocalRemovalPhase phase;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT payload,fingerprint,phase FROM conflict_local_removals WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0];
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[1])) throw new InvalidDataException("The local-only decision fingerprint changed.");
            intent = JsonSerializer.Deserialize<MirrorPulseConflictLocalRemovalIntent>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The local-only decision is empty.");
            phase = (MirrorPulseConflictLocalRemovalPhase)reader.GetInt32(2);
        }
        if (intent.Version != 1 || intent.CommandId != commandId || intent.ConflictId == Guid.Empty || intent.ItemId == Guid.Empty ||
            intent.PreparedAt == default || !Enum.IsDefined(phase) || intent.OriginalOperationIds is not { Count: > 0 } ||
            intent.ConflictIds is null || intent.ConflictIds.Count != intent.OriginalOperationIds.Count ||
            intent.OriginalOperationIds.Distinct().Count() != intent.OriginalOperationIds.Count ||
            intent.ConflictIds.Distinct().Count() != intent.ConflictIds.Count || !intent.ConflictIds.Contains(intent.ConflictId) ||
            intent.PlanId is null && (intent.PlanFingerprint is not null || intent.OriginalOperationIds.Count != 1) ||
            intent.PlanId is not null && (intent.PlanFingerprint is not { Length: 64 } || intent.OriginalObservation is not null))
            throw new InvalidDataException("The local-only decision lost its original provenance.");
        ValidatePermissionRelativePath(intent.LocalRelativePath);
        if (intent.LocalContent is not null) ValidateCoalescingDecisionContent(intent.LocalContent);
        await RequireLocalRemovalCommandAsync(commandId, intent.ConflictId, transaction, false, token).ConfigureAwait(false);
        if (intent.PlanId is { } planId)
        {
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, token).ConfigureAwait(false);
            var execution = await ReadCoalescingExecutionCoreAsync(planId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The local-only group lost its execution.");
            if (plan.ItemId != intent.ItemId || plan.InstanceId != intent.InstanceId || plan.RootKey != intent.RootKey ||
                execution.PlanFingerprint != intent.PlanFingerprint || !plan.Members.Select(member => member.OperationId).SequenceEqual(intent.OriginalOperationIds))
                throw new InvalidDataException("The local-only group changed its original execution or membership.");
        }
        if (intent.RejectedEffect is { } rejected)
        {
            var original = await ReadMutationCoreAsync(rejected.OperationId, token, transaction).ConfigureAwait(false);
            if (original is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(original.Intent, rejected) ||
                rejected.InstanceId != intent.InstanceId || rejected.RootKey != intent.RootKey || rejected.IsDirectory)
                throw new InvalidDataException("The local-only decision lost its original rejected effect.");
            if (intent.PlanId is { } ownerId)
            {
                await using var owner = _connection.CreateCommand(); owner.Transaction = transaction;
                owner.CommandText = "SELECT plan_id FROM journal_coalescing_steps WHERE operation_id=$id UNION ALL SELECT plan_id FROM journal_coalescing_retries WHERE operation_id=$id OR followup_operation_id=$id;";
                owner.Parameters.AddWithValue("$id", rejected.OperationId.ToString("D"));
                if (await owner.ExecuteScalarAsync(token).ConfigureAwait(false) is not string value || value != ownerId.ToString("D"))
                    throw new InvalidDataException("The rejected effect belongs to another group.");
            }
            else if (rejected.OperationId != intent.OriginalOperationIds[0] || rejected.Origin != MirrorPulseMutationOrigin.Journal)
                throw new InvalidDataException("The rejected single operation changed its original identity.");
        }
        else if (intent.PlanId is not null || intent.OriginalObservation is null)
            throw new InvalidDataException("The local-only preflight decision lost its original observation.");
        if (intent.OriginalObservation is { } observation)
            ValidateDeletionObservation(observation, intent.OriginalOperationIds[0], intent.InstanceId, intent.RootKey, intent.ItemId);
        var originals = new HashSet<Guid>();
        await using (var members = _connection.CreateCommand())
        {
            members.Transaction = transaction; members.CommandText = "SELECT original_id FROM conflict_local_removal_members WHERE command_id=$id;";
            members.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await members.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) originals.Add(Guid.Parse(reader.GetString(0)));
        }
        if (!originals.SetEquals(intent.OriginalOperationIds)) throw new InvalidDataException("The local-only decision lost its original journal ownership.");
        for (int index = 0; index < intent.ConflictIds.Count; index++)
        {
            await using var conflict = _connection.CreateCommand(); conflict.Transaction = transaction;
            conflict.CommandText = "SELECT change_id,instance_id,relative_path,status FROM upload_conflicts WHERE conflict_id=$id;";
            conflict.Parameters.AddWithValue("$id", intent.ConflictIds[index].ToString("D"));
            await using var reader = await conflict.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != intent.OriginalOperationIds[index].ToString("D") ||
                reader.GetString(1) != intent.InstanceId.ToString() || reader.GetString(2) != intent.LocalRelativePath ||
                reader.GetInt32(3) != (phase == MirrorPulseConflictLocalRemovalPhase.Completed ? 1 : 0))
                throw new InvalidDataException("The local-only decision lost its exact original conflict epoch.");
        }
        return new(intent with
        {
            OriginalOperationIds = Array.AsReadOnly(intent.OriginalOperationIds.ToArray()),
            ConflictIds = Array.AsReadOnly(intent.ConflictIds.ToArray())
        }, phase);
    }

    private async Task RequireLocalRemovalCommandAsync(Guid commandId, Guid conflictId, SqliteTransaction? transaction,
        bool pending, CancellationToken token)
    {
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;";
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "deletelocal" ||
            reader.GetString(1) != conflictId.ToString("D") || pending && reader.GetString(2) != "pending")
            throw new InvalidDataException("A local-only decision requires its explicit user authorization.");
    }

    private static async Task EnsureConflictLocalRemovalSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conflict_local_removals(
                command_id TEXT PRIMARY KEY, payload BLOB NOT NULL, fingerprint BLOB NOT NULL,
                phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 4));
            CREATE TABLE IF NOT EXISTS conflict_local_removal_members(
                original_id TEXT PRIMARY KEY, command_id TEXT NOT NULL REFERENCES conflict_local_removals(command_id));
            CREATE INDEX IF NOT EXISTS conflict_local_removals_pending ON conflict_local_removals(phase,command_id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
