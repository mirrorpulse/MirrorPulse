using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>Owns the original rejected file group before preserving its selected remote version.</summary>
public sealed record MirrorPulseConflictKeepBothIntent(int Version, Guid CommandId, Guid ConflictId, InstanceId InstanceId,
    Guid PlanId, string PlanFingerprint, Guid ItemId, string LocalRelativePath, MirrorPulseMutationIntent RejectedEffect,
    MirrorPulseConflictRemoteSource RemoteSource, MirrorPulseCoalescingContent? AuthorizedContent,
    IReadOnlyList<Guid> OriginalOperationIds, IReadOnlyList<Guid> ConflictIds, DateTimeOffset PreparedAt);

/// <summary>A Host-verified private copy receipt; it proves no remote acceptance or official journal acknowledgement.</summary>
public sealed record MirrorPulseConflictKeepBothCopy(long Length, string Sha256);

public sealed record MirrorPulseConflictKeepBothDecision(MirrorPulseConflictKeepBothIntent Intent,
    MirrorPulseConflictKeepBothCopy? Copy, bool RetryAuthorized, bool OriginalRetriesPrepared = false);

public sealed record MirrorPulseConflictKeepBothTarget(InstanceId InstanceId, string RootKey, string RelativePath, string RemoteRevision);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the exact current rejected remote target before bounded Worker metadata capture.</summary>
    public async Task<MirrorPulseConflictKeepBothTarget> ReadConflictKeepBothTargetAsync(Guid conflictId, string rootKey,
        Guid itemId, string localPath, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, rootKey, itemId, localPath, null, false, transaction, token).ConfigureAwait(false);
            if (selection.PlanId is null || string.IsNullOrEmpty(selection.RemoteRevision))
                throw new NotSupportedException("Preserving both sides requires an original file group and a selected existing remote version.");
            transaction.Commit(); return new(selection.InstanceId, rootKey, selection.RemoteTarget, selection.RemoteRevision);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictKeepBothDecision> PrepareConflictKeepBothAsync(Guid commandId, Guid conflictId,
        Guid itemId, string localPath, MirrorPulseConflictRemoteSource source, MirrorPulseCoalescingContent? content,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty || itemId == Guid.Empty) throw new ArgumentException("A decision requires its command, conflict and item identities.");
        ArgumentNullException.ThrowIfNull(source); ValidatePermissionRelativePath(localPath);
        if (content is not null) ValidateCoalescingDecisionContent(content);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadConflictKeepBothCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                var original = replay.Intent;
                if (original.ConflictId != conflictId || original.ItemId != itemId || original.LocalRelativePath != localPath ||
                    original.RemoteSource != source || original.AuthorizedContent != content)
                    throw new InvalidDataException("A retained KeepBoth decision cannot substitute another selected source or local object.");
                transaction.Commit(); return replay;
            }
            if (await RequireKeepBothCommandAsync(commandId, conflictId, transaction, token).ConfigureAwait(false) != "pending")
                throw new InvalidOperationException("A new KeepBoth decision requires a pending user command.");
            var selection = await ReadConflictDecisionSelectionAsync(conflictId, source.RootKey, itemId, localPath, null, false, transaction, token).ConfigureAwait(false);
            if (selection.PlanId is not { } planId || selection.PlanFingerprint is not { } fingerprint || selection.RejectedEffect is not { } rejected ||
                source.InstanceId != selection.InstanceId.ToString() || source.RelativePath != selection.RemoteTarget || source.RemoteRevision != selection.RemoteRevision)
                throw new InvalidDataException("KeepBoth requires the complete original rejected group and its selected remote source.");
            source.Validate(new(conflictId, selection.InstanceId, selection.Originals[0].ToString("D"), localPath,
                MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, null, selection.RemoteRevision, DateTimeOffset.UtcNow));
            var originalExecution = await ReadCoalescingExecutionCoreAsync(planId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original preservation execution disappeared.");
            bool needsContent = originalExecution.IsMoveOnly || originalExecution.Steps.Any(step => step.Kind == MirrorPulseCoalescingStepKind.VerifyUnchanged);
            if (needsContent != (content is not null))
                throw new InvalidOperationException("The preservation choice must retain exactly the local-content proof required by its original program.");
            foreach (Guid original in selection.Originals)
            {
                await using var owner = _connection.CreateCommand(); owner.Transaction = transaction;
                owner.CommandText = "SELECT 1 FROM conflict_deletion_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$id UNION ALL SELECT 1 FROM conflict_keep_both_owners WHERE original_id=$id LIMIT 1;";
                owner.Parameters.AddWithValue("$id", original.ToString("D"));
                if (await owner.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) throw new InvalidOperationException("An original group already belongs to another conflict decision.");
            }
            var intent = new MirrorPulseConflictKeepBothIntent(1, commandId, conflictId, selection.InstanceId, planId, fingerprint,
                itemId, localPath, rejected, source, content, selection.Originals, selection.Conflicts, DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO conflict_keep_both(command_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (Guid original in selection.Originals)
            {
                await using var member = _connection.CreateCommand(); member.Transaction = transaction;
                member.CommandText = "INSERT INTO conflict_keep_both_members(original_id,command_id) VALUES($original,$command); INSERT INTO conflict_keep_both_owners(original_id,command_id) VALUES($original,$command);";
                member.Parameters.AddWithValue("$original", original.ToString("D")); member.Parameters.AddWithValue("$command", commandId.ToString("D"));
                await member.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            transaction.Commit(); return new(intent, null, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseConflictKeepBothDecision?> ReadConflictKeepBothAsync(Guid commandId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var decision = await ReadConflictKeepBothCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return decision;
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> ReadConflictKeepBothOwnerAsync(Guid originalId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT command_id FROM conflict_keep_both_owners WHERE original_id=$id;";
            query.Parameters.AddWithValue("$id", originalId.ToString("D"));
            return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string value ? Guid.Parse(value) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Guid>> ReadPendingConflictKeepBothAsync(Guid? after = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT d.command_id FROM conflict_keep_both d JOIN user_commands u ON u.command_id=d.command_id WHERE u.state IN ('pending','failed') AND ($after IS NULL OR d.command_id>$after) ORDER BY d.command_id LIMIT 64;";
            query.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
            var ids = new List<Guid>(); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
            return ids.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the selected preservation commands in the currently authorized bounded remote program.</summary>
    public async Task<IReadOnlyList<Guid>> ReadCurrentCoalescingKeepBothCommandsAsync(Guid planId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT r.command_id FROM journal_coalescing_retries r JOIN user_commands u USING(command_id)
                    WHERE r.plan_id=$plan AND u.action='keepboth' AND r.sequence IN
                        (SELECT MAX(sequence) FROM journal_coalescing_retries WHERE plan_id=$plan GROUP BY ordinal)
                    ORDER BY r.ordinal LIMIT 3;
                    """;
                query.Parameters.AddWithValue("$plan", planId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
            }
            if (ids.Count > 2) throw new InvalidDataException("The preservation program exceeds the original coalesced effect bound.");
            foreach (Guid id in ids)
            {
                var decision = await ReadConflictKeepBothCoreAsync(id, transaction, token).ConfigureAwait(false);
                if (decision?.Copy is null || !decision.RetryAuthorized || decision.Intent.PlanId != planId)
                    throw new InvalidDataException("The current preservation program lost its original complete copy receipt.");
            }
            transaction.Commit(); return ids.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The Host records this only after independently verifying and retaining the complete private copy.</summary>
    public async Task RecordConflictKeepBothCopyAsync(Guid commandId, MirrorPulseConflictKeepBothCopy copy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (copy.Length < 0 || copy.Sha256 is not { Length: 64 } || !copy.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("A complete private-copy byte proof is required.", nameof(copy));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var decision = await ReadConflictKeepBothCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original KeepBoth decision is missing.");
            if (copy.Length != decision.Intent.RemoteSource.Length || decision.Copy is { } prior && prior != copy)
                throw new InvalidDataException("The private copy cannot replace the original remote version or byte proof.");
            if (decision.Copy is null)
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(copy, TopologyJsonOptions);
                await using var update = _connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE conflict_keep_both SET copy_payload=$copy,copy_fingerprint=$hash WHERE command_id=$id AND copy_payload IS NULL;";
                update.Parameters.AddWithValue("$id", commandId.ToString("D")); update.Parameters.AddWithValue("$copy", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("The KeepBoth preservation receipt changed.");
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records that the Host reset the still-pending original SDK retries without acknowledging or recreating any entry.</summary>
    public async Task RecordConflictKeepBothOriginalRetriesPreparedAsync(Guid commandId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var decision = await ReadConflictKeepBothCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original preservation decision is missing.");
            if (!decision.RetryAuthorized) throw new InvalidOperationException("Original retries cannot be released before the selected remote copy and distinct retry authorization.");
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE conflict_keep_both SET original_retries_prepared=1 WHERE command_id=$id;";
            update.Parameters.AddWithValue("$id", commandId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The preservation decision disappeared.");
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepBothDecision?> ReadConflictKeepBothCoreAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseConflictKeepBothIntent intent; MirrorPulseConflictKeepBothCopy? copy = null; bool retriesPrepared;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT payload,fingerprint,copy_payload,copy_fingerprint,original_retries_prepared FROM conflict_keep_both WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0];
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[1])) throw new InvalidDataException("The KeepBoth decision fingerprint changed.");
            intent = JsonSerializer.Deserialize<MirrorPulseConflictKeepBothIntent>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The KeepBoth decision is empty.");
            if (!reader.IsDBNull(2))
            {
                byte[] proof = (byte[])reader[2];
                if (reader.IsDBNull(3) || !SHA256.HashData(proof).AsSpan().SequenceEqual((byte[])reader[3])) throw new InvalidDataException("The KeepBoth copy fingerprint changed.");
                copy = JsonSerializer.Deserialize<MirrorPulseConflictKeepBothCopy>(proof, TopologyJsonOptions) ?? throw new InvalidDataException("The KeepBoth copy receipt is empty.");
            }
            else if (!reader.IsDBNull(3)) throw new InvalidDataException("The KeepBoth copy receipt is incomplete.");
            retriesPrepared = reader.GetInt32(4) == 1;
        }
        if (intent.Version != 1 || intent.CommandId != commandId || intent.ConflictId == Guid.Empty || intent.ItemId == Guid.Empty || intent.PreparedAt == default ||
            intent.OriginalOperationIds is not { Count: > 0 } || intent.ConflictIds is null || intent.OriginalOperationIds.Count != intent.ConflictIds.Count ||
            intent.OriginalOperationIds.Distinct().Count() != intent.OriginalOperationIds.Count || intent.ConflictIds.Distinct().Count() != intent.ConflictIds.Count ||
            !intent.ConflictIds.Contains(intent.ConflictId) || intent.RemoteSource is null || intent.RejectedEffect is null)
            throw new InvalidDataException("The KeepBoth decision lost its complete original conflict group.");
        ValidatePermissionRelativePath(intent.LocalRelativePath);
        if (intent.AuthorizedContent is not null) ValidateCoalescingDecisionContent(intent.AuthorizedContent);
        var plan = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
        var execution = await ReadCoalescingExecutionCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepBoth execution is missing.");
        bool needsContent = execution.IsMoveOnly || execution.Steps.Any(step => step.Kind == MirrorPulseCoalescingStepKind.VerifyUnchanged);
        if (needsContent != (intent.AuthorizedContent is not null))
            throw new InvalidDataException("The original preservation program lost its required local-content choice.");
        var rejected = await ReadMutationCoreAsync(intent.RejectedEffect.OperationId, token, transaction).ConfigureAwait(false);
        if (plan.InstanceId != intent.InstanceId || plan.ItemId != intent.ItemId || plan.RootKey != intent.RemoteSource.RootKey || execution.PlanFingerprint != intent.PlanFingerprint ||
            !plan.Members.Select(member => member.OperationId).SequenceEqual(intent.OriginalOperationIds) ||
            rejected is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(rejected.Intent, intent.RejectedEffect) ||
            intent.RemoteSource.RelativePath != (rejected.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.Intent.PreviousRelativePath : rejected.Intent.RelativePath) ||
            intent.RemoteSource.InstanceId != intent.InstanceId.ToString() || copy is not null &&
                (copy.Length != intent.RemoteSource.Length || copy.Sha256 is not { Length: 64 } || !copy.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The KeepBoth decision changed its original program or preservation proof.");
        intent.RemoteSource.Validate(new(intent.ConflictId, intent.InstanceId, intent.OriginalOperationIds[0].ToString("D"), intent.LocalRelativePath,
            MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, null, intent.RemoteSource.RemoteRevision, intent.PreparedAt));
        bool authorized;
        await using (var retry = _connection.CreateCommand())
        {
            retry.Transaction = transaction; retry.CommandText = "SELECT 1 FROM journal_coalescing_retries WHERE command_id=$id;";
            retry.Parameters.AddWithValue("$id", commandId.ToString("D")); authorized = await retry.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
        }
        if (authorized && copy is null) throw new InvalidDataException("KeepBoth cannot authorize a retry before preservation.");
        if (retriesPrepared && !authorized) throw new InvalidDataException("The original retries lost their preservation authorization.");
        string commandState = await RequireKeepBothCommandAsync(commandId, intent.ConflictId, transaction, token).ConfigureAwait(false);
        if (!authorized && commandState is "resolved" or "superseded")
            throw new InvalidDataException("An unpreserved KeepBoth decision cannot already be completed or superseded.");
        var members = new HashSet<Guid>(); var owners = new HashSet<Guid>();
        foreach (bool active in new[] { false, true })
        {
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = active ? "SELECT original_id FROM conflict_keep_both_owners WHERE command_id=$id;" :
                "SELECT original_id FROM conflict_keep_both_members WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) (active ? owners : members).Add(Guid.Parse(reader.GetString(0)));
        }
        if (!members.SetEquals(intent.OriginalOperationIds) || (authorized ? owners.Count != 0 : !owners.SetEquals(intent.OriginalOperationIds)))
            throw new InvalidDataException("KeepBoth lost its exact historical members or active decision ownership.");
        for (int index = 0; index < intent.OriginalOperationIds.Count; index++)
        {
            await using var member = _connection.CreateCommand(); member.Transaction = transaction;
            member.CommandText = "SELECT c.instance_id,c.change_id,c.relative_path,c.remote_revision,c.status,m.command_id FROM upload_conflicts c JOIN conflict_keep_both_members m ON m.original_id=c.change_id AND m.command_id=$command WHERE c.conflict_id=$conflict;";
            member.Parameters.AddWithValue("$conflict", intent.ConflictIds[index].ToString("D"));
            member.Parameters.AddWithValue("$command", commandId.ToString("D"));
            await using var reader = await member.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != intent.InstanceId.ToString() || reader.GetString(1) != intent.OriginalOperationIds[index].ToString("D") ||
                reader.GetString(2) != intent.LocalRelativePath || reader.IsDBNull(3) || reader.GetString(3) != intent.RemoteSource.RemoteRevision ||
                reader.GetInt32(4) != (authorized ? 1 : 0) || reader.GetString(5) != commandId.ToString("D"))
                throw new InvalidDataException("KeepBoth lost its original conflict epoch or exclusive group ownership.");
        }
        return new(intent with { OriginalOperationIds = Array.AsReadOnly(intent.OriginalOperationIds.ToArray()), ConflictIds = Array.AsReadOnly(intent.ConflictIds.ToArray()) }, copy, authorized, retriesPrepared);
    }

    private async Task<string> RequireKeepBothCommandAsync(Guid commandId, Guid conflictId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;";
        query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "keepboth" || reader.GetString(1) != conflictId.ToString("D") ||
            reader.GetString(2) is not ("pending" or "failed" or "resolved" or "superseded")) throw new InvalidDataException("KeepBoth requires its original explicit user command.");
        return reader.GetString(2);
    }

    private static async Task EnsureConflictKeepBothSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS conflict_keep_both(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL,copy_payload BLOB NULL,copy_fingerprint BLOB NULL,
                original_retries_prepared INTEGER NOT NULL DEFAULT 0 CHECK(original_retries_prepared IN (0,1)),
                CHECK((copy_payload IS NULL)=(copy_fingerprint IS NULL)));
            CREATE TABLE IF NOT EXISTS conflict_keep_both_members(original_id TEXT NOT NULL,
                command_id TEXT NOT NULL REFERENCES conflict_keep_both(command_id),PRIMARY KEY(original_id,command_id));
            CREATE TABLE IF NOT EXISTS conflict_keep_both_owners(original_id TEXT PRIMARY KEY,
                command_id TEXT NOT NULL REFERENCES conflict_keep_both(command_id));
            CREATE INDEX IF NOT EXISTS conflict_keep_both_members_command ON conflict_keep_both_members(command_id,original_id);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
