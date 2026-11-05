using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>Freezes a complete rejected group and both selected sides before any KeepRemote application.</summary>
public sealed record MirrorPulseConflictKeepRemoteIntent(int Version, Guid CommandId, Guid ConflictId,
    InstanceId InstanceId, Guid PlanId, string PlanFingerprint, Guid ItemId, string LocalRelativePath,
    MirrorPulseMutationIntent RejectedEffect, MirrorPulseConflictRemoteSource RemoteSource,
    MirrorPulseCoalescingContent? LocalContent, IReadOnlyList<Guid> OriginalOperationIds,
    IReadOnlyList<Guid> ConflictIds, DateTimeOffset PreparedAt);

/// <summary>A null copy records the originally observed local absence, not permission to discard existing bytes.</summary>
public sealed record MirrorPulseConflictKeepRemoteLocalPreservation(MirrorPulseConflictKeepBothCopy? Copy);

/// <summary>Preservation alone establishes neither local remote-content application nor official journal acknowledgement.</summary>
public sealed record MirrorPulseConflictKeepRemotePreparation(MirrorPulseConflictKeepRemoteIntent Intent,
    MirrorPulseConflictKeepRemoteLocalPreservation? LocalPreservation, MirrorPulseConflictKeepBothCopy? RemoteCopy);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseConflictKeepRemotePreparation> PrepareConflictKeepRemoteAsync(Guid commandId, Guid conflictId,
        Guid itemId, string localPath, MirrorPulseConflictRemoteSource source, MirrorPulseCoalescingContent? localContent,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty || itemId == Guid.Empty) throw new ArgumentException("KeepRemote requires its exact command, conflict and item identities.");
        ArgumentNullException.ThrowIfNull(source); ValidatePermissionRelativePath(localPath);
        if (localContent is not null) ValidateCoalescingDecisionContent(localContent);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                var old = replay.Intent;
                if (old.ConflictId != conflictId || old.ItemId != itemId || old.LocalRelativePath != localPath || old.RemoteSource != source || old.LocalContent != localContent)
                    throw new InvalidDataException("KeepRemote cannot replace its original group, selected remote version or local bytes.");
                transaction.Commit(); return replay;
            }
            await RequireKeepRemoteCommandAsync(commandId, conflictId, transaction, token).ConfigureAwait(false);
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, source.RootKey, itemId, localPath, null, false, transaction, token).ConfigureAwait(false);
            if (selection.PlanId is not { } plan || selection.PlanFingerprint is not { } fingerprint || selection.RejectedEffect is not { } rejected ||
                source.InstanceId != selection.InstanceId.ToString() || source.RelativePath != selection.RemoteTarget || source.RemoteRevision != selection.RemoteRevision)
                throw new InvalidDataException("KeepRemote requires the complete original rejected group and its exact selected remote file.");
            source.Validate(new(conflictId, selection.InstanceId, selection.Originals[0].ToString("D"), localPath,
                MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, null, selection.RemoteRevision, DateTimeOffset.UtcNow));
            foreach (Guid original in selection.Originals) await RequireUnownedRemoteObservationOriginalAsync(original, transaction, token).ConfigureAwait(false);
            var intent = new MirrorPulseConflictKeepRemoteIntent(1, commandId, conflictId, selection.InstanceId, plan, fingerprint,
                itemId, localPath, rejected, source, localContent, selection.Originals, selection.Conflicts, DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            if (payload.Length > 16_777_216) throw new InvalidDataException("The complete KeepRemote preparation exceeds its retained payload bound.");
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_keep_remote_preparations(command_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            for (int index = 0; index < intent.OriginalOperationIds.Count; index++)
            {
                await using var member = _connection.CreateCommand(); member.Transaction = transaction;
                member.CommandText = "INSERT INTO conflict_keep_remote_members(original_id,command_id,ordinal,conflict_id) VALUES($original,$command,$ordinal,$conflict);";
                member.Parameters.AddWithValue("$original", intent.OriginalOperationIds[index].ToString("D")); member.Parameters.AddWithValue("$command", commandId.ToString("D"));
                member.Parameters.AddWithValue("$ordinal", index); member.Parameters.AddWithValue("$conflict", intent.ConflictIds[index].ToString("D"));
                await member.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return new(intent, null, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictKeepRemotePreparation?> ReadConflictKeepRemoteAsync(Guid commandId, CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original KeepRemote command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> ReadConflictKeepRemoteOwnerAsync(Guid originalId, CancellationToken token = default)
    {
        if (originalId == Guid.Empty) throw new ArgumentException("The original journal operation is required.", nameof(originalId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_keep_remote_members WHERE original_id=$id;";
            query.Parameters.AddWithValue("$id", originalId.ToString("D"));
            return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string value ? Guid.ParseExact(value, "D") : null;
        }
        finally { _gate.Release(); }
    }

    public Task RecordConflictKeepRemoteLocalPreservationAsync(Guid commandId, MirrorPulseConflictKeepRemoteLocalPreservation proof, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return RecordKeepRemotePreservationAsync(commandId, proof, null, token);
    }

    public Task RecordConflictKeepRemoteRemoteCopyAsync(Guid commandId, MirrorPulseConflictKeepBothCopy proof, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return RecordKeepRemotePreservationAsync(commandId, null, proof, token);
    }

    private async Task RecordKeepRemotePreservationAsync(Guid commandId, MirrorPulseConflictKeepRemoteLocalPreservation? local,
        MirrorPulseConflictKeepBothCopy? remote, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original KeepRemote preparation is missing.");
            bool isLocal = local is not null;
            if (isLocal) ValidateKeepRemoteLocalCopy(current.Intent, local!);
            else
            {
                if (current.LocalPreservation is null) throw new InvalidOperationException("The exact local bytes or original absence must be retained before the remote copy.");
                ValidateKeepRemoteCopy(remote!, current.Intent.RemoteSource.Length);
            }
            object? prior = isLocal ? current.LocalPreservation : current.RemoteCopy;
            object proof = (object?)local ?? remote!;
            if (prior is not null)
            {
                if (!prior.Equals(proof)) throw new InvalidDataException("A complete KeepRemote preservation receipt is immutable.");
                transaction.Commit(); return;
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(proof, proof.GetType(), TopologyJsonOptions);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO conflict_keep_remote_copies(command_id,side,payload,fingerprint) VALUES($id,$side,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$side", isLocal ? 0 : 1);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemotePreparation?> ReadConflictKeepRemoteCoreAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseConflictKeepRemoteIntent intent;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT payload,fingerprint FROM conflict_keep_remote_preparations WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0];
            if (payload.Length > 16_777_216 || !SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[1])) throw new InvalidDataException("The KeepRemote preparation fingerprint changed.");
            intent = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteIntent>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The original KeepRemote preparation is empty.");
        }
        if (intent.Version != 1 || intent.CommandId != commandId || intent.ConflictId == Guid.Empty || intent.ItemId == Guid.Empty || intent.PreparedAt <= DateTimeOffset.UnixEpoch ||
            intent.OriginalOperationIds is not { Count: >= 2 } || intent.ConflictIds is null || intent.OriginalOperationIds.Count != intent.ConflictIds.Count ||
            intent.OriginalOperationIds.Distinct().Count() != intent.OriginalOperationIds.Count || intent.ConflictIds.Distinct().Count() != intent.ConflictIds.Count ||
            !intent.ConflictIds.Contains(intent.ConflictId) || intent.RemoteSource is null || intent.RejectedEffect is null)
            throw new InvalidDataException("KeepRemote lost its complete original group or selected source.");
        ValidatePermissionRelativePath(intent.LocalRelativePath);
        if (intent.LocalContent is not null) ValidateCoalescingDecisionContent(intent.LocalContent);
        bool completed = await HasKeepRemoteAcknowledgementResultAsync(commandId, transaction, token).ConfigureAwait(false);
        await RequireKeepRemoteCommandAsync(commandId, intent.ConflictId, transaction, token, completed).ConfigureAwait(false);
        var plan = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
        var execution = await ReadCoalescingExecutionCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false) ?? throw new InvalidDataException("The original KeepRemote execution is missing.");
        var rejected = await ReadMutationCoreAsync(intent.RejectedEffect.OperationId, token, transaction).ConfigureAwait(false);
        if (plan.InstanceId != intent.InstanceId || plan.ItemId != intent.ItemId || plan.RootKey != intent.RemoteSource.RootKey || execution.PlanFingerprint != intent.PlanFingerprint ||
            !plan.Members.Select(member => member.OperationId).SequenceEqual(intent.OriginalOperationIds) || rejected is not { State: MirrorPulseMutationState.Conflict } ||
            !SameIntent(rejected.Intent, intent.RejectedEffect) || intent.RemoteSource.InstanceId != intent.InstanceId.ToString() ||
            intent.RemoteSource.RelativePath != (rejected.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.Intent.PreviousRelativePath : rejected.Intent.RelativePath))
            throw new InvalidDataException("KeepRemote lost its original execution, rejected effect or selected root.");
        if (!completed)
        {
            var selected = await ReadConflictDecisionSelectionAsync(intent.ConflictId, intent.RemoteSource.RootKey, intent.ItemId,
                intent.LocalRelativePath, null, false, transaction, token).ConfigureAwait(false);
            if (selected.PlanId != intent.PlanId || selected.PlanFingerprint != intent.PlanFingerprint || selected.InstanceId != intent.InstanceId ||
                selected.RejectedEffect is not { } currentEffect || !SameIntent(currentEffect, intent.RejectedEffect) || selected.RemoteTarget != intent.RemoteSource.RelativePath ||
                selected.RemoteRevision != intent.RemoteSource.RemoteRevision || !selected.Originals.SequenceEqual(intent.OriginalOperationIds) || !selected.Conflicts.SequenceEqual(intent.ConflictIds))
                throw new InvalidDataException("KeepRemote cannot substitute another selected version epoch or current rejected program.");
        }
        intent.RemoteSource.Validate(new(intent.ConflictId, intent.InstanceId, intent.OriginalOperationIds[0].ToString("D"), intent.LocalRelativePath,
            MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, null, intent.RemoteSource.RemoteRevision, intent.PreparedAt));
        await using (var members = _connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = "SELECT m.ordinal,m.original_id,m.conflict_id,c.change_id,c.instance_id,c.relative_path,c.remote_revision,c.status FROM conflict_keep_remote_members m JOIN upload_conflicts c ON c.conflict_id=m.conflict_id WHERE m.command_id=$id ORDER BY m.ordinal;";
            members.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await members.ExecuteReaderAsync(token).ConfigureAwait(false); int index = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (index >= intent.OriginalOperationIds.Count || reader.GetInt32(0) != index || reader.GetString(1) != intent.OriginalOperationIds[index].ToString("D") ||
                    reader.GetString(2) != intent.ConflictIds[index].ToString("D") || reader.GetString(3) != reader.GetString(1) || reader.GetString(4) != intent.InstanceId.ToString() ||
                    reader.GetString(5) != intent.LocalRelativePath || reader.IsDBNull(6) || reader.GetString(6) != intent.RemoteSource.RemoteRevision || reader.GetInt32(7) != (completed ? 1 : 0))
                    throw new InvalidDataException("KeepRemote lost its exact conflict epochs or original journal ownership.");
                index++;
            }
            if (index != intent.OriginalOperationIds.Count) throw new InvalidDataException("The complete original KeepRemote membership disappeared.");
        }
        MirrorPulseConflictKeepRemoteLocalPreservation? local = null; MirrorPulseConflictKeepBothCopy? remote = null;
        await using (var copies = _connection.CreateCommand())
        {
            copies.Transaction = transaction; copies.CommandText = "SELECT side,payload,fingerprint FROM conflict_keep_remote_copies WHERE command_id=$id ORDER BY side;";
            copies.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await copies.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                byte[] payload = (byte[])reader[1];
                if (payload.Length > 65_536 || !SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[2])) throw new InvalidDataException("An original KeepRemote copy receipt changed.");
                if (reader.GetInt32(0) == 0)
                {
                    local = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteLocalPreservation>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The local preservation receipt is empty.");
                    ValidateKeepRemoteLocalCopy(intent, local);
                }
                else if (reader.GetInt32(0) == 1)
                {
                    remote = JsonSerializer.Deserialize<MirrorPulseConflictKeepBothCopy>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The remote preservation receipt is empty.");
                    ValidateKeepRemoteCopy(remote, intent.RemoteSource.Length);
                }
                else throw new InvalidDataException("An unknown side cannot authorize KeepRemote application.");
            }
        }
        if (remote is not null && local is null) throw new InvalidDataException("KeepRemote lost its original local preservation before remote staging.");
        return new(intent with { OriginalOperationIds = Array.AsReadOnly(intent.OriginalOperationIds.ToArray()), ConflictIds = Array.AsReadOnly(intent.ConflictIds.ToArray()) }, local, remote);
    }

    private async Task RequireKeepRemoteCommandAsync(Guid commandId, Guid conflictId, SqliteTransaction transaction, CancellationToken token,
        bool completed = false)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;"; query.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "keepremote" || reader.GetString(1) != conflictId.ToString("D") ||
            (completed ? reader.GetString(2) != "resolved" : reader.GetString(2) is not ("pending" or "failed")))
            throw new InvalidDataException("KeepRemote requires the original explicit user command in its retained state.");
    }

    private static void ValidateKeepRemoteLocalCopy(MirrorPulseConflictKeepRemoteIntent intent, MirrorPulseConflictKeepRemoteLocalPreservation proof)
    {
        if (intent.LocalContent is null)
        {
            if (proof.Copy is not null) throw new InvalidDataException("A local copy cannot replace the originally observed local absence.");
        }
        else
        {
            if (proof.Copy is null) throw new InvalidDataException("The original local bytes cannot be replaced with absence.");
            ValidateKeepRemoteCopy(proof.Copy, intent.LocalContent.Length);
            if (proof.Copy.Sha256 != intent.LocalContent.Sha256) throw new InvalidDataException("The original complete local byte proof changed.");
        }
    }

    private static void ValidateKeepRemoteCopy(MirrorPulseConflictKeepBothCopy proof, long length)
    {
        if (proof.Length != length || proof.Length < 0 || proof.Sha256 is not { Length: 64 } || proof.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new InvalidDataException("KeepRemote requires the complete selected copy's length and canonical byte hash.");
    }
}
