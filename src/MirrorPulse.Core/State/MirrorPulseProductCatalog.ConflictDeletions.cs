using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public enum MirrorPulseConflictDeletionPhase { Prepared, Preserved, Retiring, Retired, RemoteAccepted, Completed }

/// <summary>An explicit deletion decision; rejected journal effects remain immutable historical evidence.</summary>
public sealed record MirrorPulseConflictDeletionIntent(int Version, Guid CommandId, Guid ConflictId,
    InstanceId InstanceId, string RootKey, Guid? PlanId, Guid ItemId, string LocalRelativePath,
    MirrorPulseMutationIntent RemoteDeletion, MirrorPulseCoalescingContent? LocalContent,
    IReadOnlyList<Guid> OriginalOperationIds, IReadOnlyList<Guid> ConflictIds, DateTimeOffset PreparedAt);

public sealed record MirrorPulseConflictDeletion(MirrorPulseConflictDeletionIntent Intent, MirrorPulseConflictDeletionPhase Phase);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Freezes the complete rejected group and its observed remote condition before any local removal.</summary>
    public async Task<MirrorPulseConflictDeletion> PrepareConflictDeletionAsync(Guid commandId, Guid conflictId,
        string rootKey, Guid itemId, string localRelativePath, MirrorPulseCoalescingContent? content,
        CancellationToken cancellationToken = default)
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
                    old.LocalRelativePath != localRelativePath || old.LocalContent != content)
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
            Guid? planId = null;
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction;
                owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$operation;";
                owner.Parameters.AddWithValue("$operation", selected.OriginalId.ToString("D"));
                if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value) planId = Guid.Parse(value);
            }
            var originals = new List<Guid>(); var conflicts = new List<Guid>();
            MirrorPulseMutationIntent rejected;
            if (planId is { } ownerId)
            {
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
                rejected = current ?? throw new InvalidOperationException("The group has no current rejected effect.");
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
                if (record is not { State: MirrorPulseMutationState.Conflict, Intent.Origin: MirrorPulseMutationOrigin.Journal } ||
                    record.Intent.InstanceId != selected.InstanceId || record.Intent.RootKey != rootKey || record.Intent.IsDirectory)
                    throw new InvalidOperationException("Single-operation deletion requires the original rejected file mutation.");
                rejected = record.Intent; originals.Add(selected.OriginalId); conflicts.Add(conflictId);
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
                rejected.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.PreviousRelativePath! : rejected.RelativePath,
                null, false, selected.Revision, null, null, MirrorPulseMutationOrigin.ConflictDecision);
            var deletion = new MirrorPulseConflictDeletionIntent(1, commandId, conflictId, selected.InstanceId, rootKey, planId,
                itemId, localRelativePath, remote, content, originals.AsReadOnly(), conflicts.AsReadOnly(), DateTimeOffset.UtcNow);
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
                var remote = await ReadMutationCoreAsync(deletion.Intent.RemoteDeletion.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (remote is not { State: MirrorPulseMutationState.Acknowledged } || !SameIntent(remote.Intent, deletion.Intent.RemoteDeletion) || remote.AcceptedRevision is not null)
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
                foreach (Guid id in deletion.Intent.ConflictIds)
                {
                    await using var update = _connection.CreateCommand(); update.Transaction = transaction;
                    update.CommandText = "UPDATE upload_conflicts SET status=1 WHERE conflict_id=$id AND status=0;";
                    update.Parameters.AddWithValue("$id", id.ToString("D"));
                    if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                        throw new InvalidDataException("A deletion decision lost its unresolved group conflict.");
                }
                await using var command = _connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE user_commands SET state='resolved' WHERE command_id=$id AND action='deleteremote' AND target_id=$target;";
                command.Parameters.AddWithValue("$id", commandId.ToString("D")); command.Parameters.AddWithValue("$target", deletion.Intent.ConflictId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidDataException("The deletion command lost its explicit authorization.");
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
        if (intent.Version != 1 || intent.CommandId != commandId || intent.ConflictId == Guid.Empty || intent.ItemId == Guid.Empty ||
            intent.PreparedAt == default || !Enum.IsDefined(phase) || intent.OriginalOperationIds is not { Count: > 0 } ||
            intent.ConflictIds is null || intent.ConflictIds.Count != intent.OriginalOperationIds.Count ||
            intent.OriginalOperationIds.Distinct().Count() != intent.OriginalOperationIds.Count ||
            intent.ConflictIds.Distinct().Count() != intent.ConflictIds.Count || !intent.ConflictIds.Contains(intent.ConflictId) ||
            intent.RemoteDeletion is not { Kind: MirrorPulseWorkerChangeKind.Delete, IsDirectory: false, Origin: MirrorPulseMutationOrigin.ConflictDecision } ||
            intent.RemoteDeletion.InstanceId != intent.InstanceId || intent.RemoteDeletion.RootKey != intent.RootKey ||
            string.IsNullOrEmpty(intent.RemoteDeletion.ExpectedRevision) || intent.RemoteDeletion.OperationId !=
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.ConflictDeletion.v1/" + commandId.ToString("D"))).AsSpan(0, 16)))
            throw new InvalidDataException("The retained deletion decision lost its original provenance.");
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
}
