using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const int JournalDirectoryMovePayloadLimit = 8 * 1024 * 1024;
    private const string JournalDirectoryMoveTable = """
        CREATE TABLE journal_directory_moves(
            operation_id TEXT PRIMARY KEY NOT NULL REFERENCES mutation_intents(operation_id),
            local_move_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_moves(operation_id),
            admission BLOB NOT NULL CHECK(length(admission) BETWEEN 1 AND 8388608),
            admission_hash BLOB NOT NULL CHECK(length(admission_hash)=32),
            targets BLOB NULL CHECK(length(targets) BETWEEN 1 AND 8388608),
            targets_hash BLOB NULL CHECK(length(targets_hash)=32),
            acknowledgement_started INTEGER NOT NULL DEFAULT 0 CHECK(acknowledgement_started IN (0,1)),
            CHECK((targets IS NULL)=(targets_hash IS NULL)),
            CHECK(acknowledgement_started=0 OR targets IS NOT NULL))
        """;

    private const string JournalDirectoryMoveProjectionTable = """
        CREATE TABLE journal_directory_move_projections(
            operation_id TEXT NOT NULL REFERENCES journal_directory_moves(operation_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 65535),
            member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
            observed_at INTEGER NOT NULL CHECK(observed_at>0),
            PRIMARY KEY(operation_id,ordinal),
            UNIQUE(member_id,operation_id))
        """;

    private static async Task EnsureJournalDirectoryMoveSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        foreach (var (name, definition) in new (string, string)[]
            { ("journal_directory_moves", JournalDirectoryMoveTable), ("journal_directory_move_projections", JournalDirectoryMoveProjectionTable) })
        {
            sql.Parameters.Clear();
            sql.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            sql.Parameters.AddWithValue("$name", name);
            var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 109) throw new InvalidDataException($"The original directory Move table {name} is missing.");
                sql.CommandText = definition;
                await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string text || NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(definition))
                throw new InvalidDataException($"The original directory Move table {name} has changed.");
        }
        sql.Parameters.Clear();
        if (originalVersion < 109)
        {
            sql.CommandText = "SELECT 1 FROM journal_directory_moves LIMIT 1;";
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply original journal directory Move evidence.");
            sql.CommandText = "PRAGMA user_version=109;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal Task<MirrorPulseJournalDirectoryMove?> ReadJournalDirectoryMoveAsync(Guid operationId, CancellationToken token) =>
        WithJournalDirectoryMoveAsync(operationId, (value, _, _) => Task.FromResult(value), token);

    internal async Task<MirrorPulseJournalDirectoryMove> StartJournalDirectoryMoveAsync(MirrorPulseJournalDirectoryMoveAdmission admission,
        MirrorPulseMutationIntent mutation, CancellationToken token)
    {
        byte[] payload = EncodeJournalDirectoryMove(admission);
        return (await WithJournalDirectoryMoveAsync(admission.JournalOperationId, async (retained, transaction, stop) =>
        {
            if (retained is not null)
            {
                if (!payload.AsSpan().SequenceEqual(EncodeJournalDirectoryMove(retained.Admission)) || retained.Mutation.Intent != mutation)
                    throw new InvalidOperationException("The original directory Move cannot change its retained admission.");
                return retained;
            }
            await ValidateJournalDirectoryMoveAdmissionAsync(admission, mutation, transaction, stop).ConfigureAwait(false);
            if (await HasTypedDirectoryMutationOwnerCoreAsync(mutation.OperationId, transaction, stop).ConfigureAwait(false))
                throw new InvalidOperationException("The original directory Move already has a typed owner.");
            await RequireNamespaceMoveRootUnfencedAsync(admission.RootId, null, transaction, stop).ConfigureAwait(false);
            foreach (var original in admission.Members)
            {
                var member = await ReadNamespaceMemberCoreAsync(original.MemberId, transaction, stop).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The directory Move lost a registered member before dispatch.");
                var position = await ReadNamespaceMemberLocationCoreAsync(member, transaction, stop).ConfigureAwait(false);
                var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, stop).ConfigureAwait(false);
                if (position.CurrentPositionOperationId != admission.LocalMoveOperationId || position.RelativePath != original.TargetPath ||
                    member.LocalObject != original.Binding || identity.ItemId != original.OriginalItemId ||
                    identity.RemoteId != original.OriginalRemoteId || identity.AcceptanceOperationId != original.PreviousIdentityAcceptance)
                    throw new InvalidOperationException("A later member position or identity superseded the original directory Move.");
            }
            var owner = await PrepareMutationCoreAsync(mutation, JsonSerializer.SerializeToUtf8Bytes(mutation, TopologyJsonOptions),
                stop, transaction).ConfigureAwait(false);
            if (owner.State != MirrorPulseMutationState.Prepared || owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted)
                throw new InvalidOperationException("Only a never-dispatched journal Move can enter the durable Worker route.");
            await using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO journal_directory_moves(operation_id,local_move_id,admission,admission_hash)
                VALUES($operation,$local,$admission,$hash);
                """;
            insert.Parameters.AddWithValue("$operation", mutation.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$local", admission.LocalMoveOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$admission", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(stop).ConfigureAwait(false);
            await SetDirectoryRetirementStateAsync(mutation.OperationId, owner.State, MirrorPulseMutationState.Executing,
                null, transaction, stop).ConfigureAwait(false);
            return await ReadJournalDirectoryMoveCoreAsync(mutation.OperationId, transaction, stop).ConfigureAwait(false);
        }, token).ConfigureAwait(false))!;
    }

    internal Task<MirrorPulseJournalDirectoryMove> AcceptJournalDirectoryMoveAsync(Guid operationId, string revision, CancellationToken token) =>
        ChangeJournalDirectoryMoveAsync(operationId, async (value, transaction, stop) =>
        {
            if (string.IsNullOrEmpty(revision) || revision != value.Mutation.Intent.ExpectedRevision)
                throw new InvalidDataException("The Worker did not accept the exact moved directory revision.");
            if (value.Mutation.State == MirrorPulseMutationState.Executing)
                await SetDirectoryRetirementStateAsync(operationId, value.Mutation.State, MirrorPulseMutationState.RemoteAccepted,
                    revision, transaction, stop).ConfigureAwait(false);
        }, token);

    internal Task<MirrorPulseJournalDirectoryMove> SaveJournalDirectoryMoveTargetsAsync(Guid operationId,
        IReadOnlyList<MirrorPulseDirectoryMoveTarget> targets, CancellationToken token) =>
        ChangeJournalDirectoryMoveAsync(operationId, async (value, transaction, stop) =>
        {
            if (value.Mutation.State == MirrorPulseMutationState.Executing)
                throw new InvalidOperationException("Target projection requires a retained Worker acceptance.");
            ValidateJournalDirectoryMoveTargets(value.Admission, targets);
            byte[] payload = EncodeJournalDirectoryMove(targets);
            if (value.Targets is not null)
            {
                if (!payload.AsSpan().SequenceEqual(EncodeJournalDirectoryMove(value.Targets)))
                    throw new InvalidOperationException("A directory Move cannot replace its retained target identities.");
                return;
            }
            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE journal_directory_moves SET targets=$targets,targets_hash=$hash WHERE operation_id=$id AND targets IS NULL;";
            update.Parameters.AddWithValue("$id", operationId.ToString("D"));
            update.Parameters.AddWithValue("$targets", payload);
            update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            if (await update.ExecuteNonQueryAsync(stop).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The directory Move target plan changed while being retained.");
        }, token);

    /// <summary>Retains the next member whose public identity passed native and official readback.</summary>
    internal Task<MirrorPulseJournalDirectoryMove> RecordJournalDirectoryMoveProjectionAsync(Guid operationId,
        int ordinal, MirrorPulseDirectoryMoveProjectionReadback readback, CancellationToken token) =>
        ChangeJournalDirectoryMoveAsync(operationId, async (value, transaction, stop) =>
        {
            if (value.Targets is null || ordinal < 0 || ordinal >= value.Targets.Count || value.Targets[ordinal] != readback.Target)
                throw new InvalidOperationException("The projected member does not match its sealed target.");
            if (ordinal < value.ProjectedMemberCount) return;
            if (ordinal != value.ProjectedMemberCount || value.AcknowledgementStarted)
                throw new InvalidOperationException("Directory Move projections must retain their sealed order.");
            var original = value.Admission.Members[ordinal];
            var member = await ReadNamespaceMemberCoreAsync(original.MemberId, transaction, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The projected directory Move member is missing.");
            var position = await ReadNamespaceMemberLocationCoreAsync(member, transaction, stop).ConfigureAwait(false);
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, stop).ConfigureAwait(false);
            var localMove = await ReadNamespaceDirectoryMoveCompletionCoreAsync(value.Admission.LocalMoveOperationId, transaction, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory Move projection lost its original local completion.");
            if (member.LocalObject != original.Binding || position.CurrentPositionOperationId != value.Admission.LocalMoveOperationId ||
                position.RelativePath != original.TargetPath || identity.AcceptanceOperationId != original.PreviousIdentityAcceptance ||
                identity.ItemId != original.OriginalItemId || identity.RemoteId != original.OriginalRemoteId ||
                readback.ObservedAt < localMove.Root.VerifiedAt)
                throw new InvalidOperationException("The projected directory Move member has a later position or identity.");
            await using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO journal_directory_move_projections(operation_id,ordinal,member_id,observed_at)
                VALUES($id,$ordinal,$member,$observed);
                """;
            insert.Parameters.AddWithValue("$id", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$member", readback.Target.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$observed", readback.ObservedAt.UtcTicks);
            await insert.ExecuteNonQueryAsync(stop).ConfigureAwait(false);
        }, token);

    internal Task<MirrorPulseJournalDirectoryMove> BeginJournalDirectoryMoveAcknowledgementAsync(Guid operationId, CancellationToken token) =>
        ChangeJournalDirectoryMoveAsync(operationId, async (value, transaction, stop) =>
        {
            if (value.AcknowledgementStarted) return;
            if (value.Mutation.State != MirrorPulseMutationState.RemoteAccepted || value.Targets is null ||
                value.ProjectedMemberCount != value.Targets.Count)
                throw new InvalidOperationException("Acknowledgement requires the accepted Worker result and every exact target projection.");
            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE journal_directory_moves SET acknowledgement_started=1 WHERE operation_id=$id AND acknowledgement_started=0;";
            update.Parameters.AddWithValue("$id", operationId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(stop).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The directory Move acknowledgement changed before its durable start.");
        }, token);

    internal Task<MirrorPulseJournalDirectoryMove> CompleteJournalDirectoryMoveAcknowledgementAsync(Guid operationId, CancellationToken token) =>
        ChangeJournalDirectoryMoveAsync(operationId, async (value, transaction, stop) =>
        {
            if (!value.AcknowledgementStarted)
                throw new InvalidOperationException("The original directory Move acknowledgement has no durable start.");
            if (value.Mutation.State != MirrorPulseMutationState.Acknowledged)
                await SetDirectoryRetirementStateAsync(operationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged,
                    value.Mutation.AcceptedRevision, transaction, stop).ConfigureAwait(false);
        }, token);

    private async Task<MirrorPulseJournalDirectoryMove> ChangeJournalDirectoryMoveAsync(Guid id,
        Func<MirrorPulseJournalDirectoryMove, SqliteTransaction, CancellationToken, Task> change, CancellationToken token) =>
        (await WithJournalDirectoryMoveAsync(id, async (value, transaction, stop) =>
        {
            await change(value ?? throw new InvalidOperationException("The original directory Move was not admitted."), transaction, stop).ConfigureAwait(false);
            return await ReadJournalDirectoryMoveCoreAsync(id, transaction, stop).ConfigureAwait(false);
        }, token).ConfigureAwait(false))!;

    private async Task<MirrorPulseJournalDirectoryMove?> WithJournalDirectoryMoveAsync(Guid id,
        Func<MirrorPulseJournalDirectoryMove?, SqliteTransaction, CancellationToken, Task<MirrorPulseJournalDirectoryMove?>> action, CancellationToken token)
    {
        if (id == Guid.Empty) throw new ArgumentException("The original journal Move ID is required.", nameof(id));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var current = await ReadJournalDirectoryMoveCoreAsync(id, transaction, token).ConfigureAwait(false);
            var result = await action(current, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseJournalDirectoryMove?> ReadJournalDirectoryMoveCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseJournalDirectoryMoveAdmission admission;
        MirrorPulseDirectoryMoveTarget[]? targets;
        bool started;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT local_move_id,admission,admission_hash,targets,targets_hash,acknowledgement_started FROM journal_directory_moves WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            admission = DecodeJournalDirectoryMove<MirrorPulseJournalDirectoryMoveAdmission>((byte[])reader[1], (byte[])reader[2]);
            if (admission.JournalOperationId != id || admission.LocalMoveOperationId.ToString("D") != reader.GetString(0))
                throw new InvalidDataException("The original directory Move indexes disagree with its admission.");
            targets = reader.IsDBNull(3) ? null : DecodeJournalDirectoryMove<MirrorPulseDirectoryMoveTarget[]>((byte[])reader[3], (byte[])reader[4]);
            started = reader.GetInt64(5) == 1;
        }
        var mutation = await ReadMutationCoreAsync(id, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original directory Move lost its owning mutation.");
        var validating = _namespaceHistoryReads.GetOrCreateValue(transaction).JournalDirectoryMovesBeingValidated;
        if (!validating.Add(id)) throw new InvalidDataException("The original directory Move history contains a cycle.");
        try
        {
            await ValidateJournalDirectoryMoveAdmissionAsync(admission, mutation.Intent, transaction, token).ConfigureAwait(false);
        }
        finally { validating.Remove(id); }
        if (targets is not null) ValidateJournalDirectoryMoveTargets(admission, targets);
        int projected = await ReadJournalDirectoryMoveProjectionCountAsync(admission, targets, transaction, token).ConfigureAwait(false);
        if (mutation.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.Started ||
            (mutation.State switch
            {
                MirrorPulseMutationState.Executing => targets is not null || started || mutation.AcceptedRevision is not null,
                MirrorPulseMutationState.RemoteAccepted => mutation.AcceptedRevision != mutation.Intent.ExpectedRevision || started && projected != admission.Members.Count,
                MirrorPulseMutationState.Acknowledged => mutation.AcceptedRevision != mutation.Intent.ExpectedRevision || !started || projected != admission.Members.Count,
                _ => true,
            }))
            throw new InvalidDataException("The original directory Move ledger and mutation state disagree.");
        return new(admission, mutation, targets, projected, started);
    }

    private async Task<int> ReadJournalDirectoryMoveProjectionCountAsync(MirrorPulseJournalDirectoryMoveAdmission admission,
        MirrorPulseDirectoryMoveTarget[]? targets, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT ordinal,member_id,observed_at FROM journal_directory_move_projections
            WHERE operation_id=$id ORDER BY ordinal;
            """;
        query.Parameters.AddWithValue("$id", admission.JournalOperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        int projected = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (targets is null || projected >= targets.Length || reader.GetInt32(0) != projected ||
                reader.GetString(1) != targets[projected].MemberId.ToString("D") ||
                reader.GetInt64(2) > DateTimeOffset.MaxValue.UtcTicks)
                throw new InvalidDataException("A directory Move projection lost its sealed member, order or observation.");
            projected++;
        }
        return projected;
    }

    private async Task<(int Ordinal, DateTimeOffset ObservedAt)?> ReadJournalDirectoryMoveProjectionIndexAsync(Guid memberId,
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT ordinal,observed_at FROM journal_directory_move_projections
            WHERE operation_id=$operation AND member_id=$member;
            """;
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        query.Parameters.AddWithValue("$member", memberId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        long ticks = reader.GetInt64(1);
        if (ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            throw new InvalidDataException("The original Move projection observation time is invalid.");
        return (reader.GetInt32(0), new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    private async Task<NamespaceIdentityTransition?> ReadJournalDirectoryMoveIdentityTransitionAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var indexed = await ReadJournalDirectoryMoveProjectionIndexAsync(member.MemberId, operationId, transaction, token).ConfigureAwait(false);
        if (indexed is not { } proof) return null;
        var retained = await ReadJournalDirectoryMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The member projection lost its original directory Move.");
        if (proof.Ordinal < 0 || proof.Ordinal >= retained.ProjectedMemberCount || retained.Targets is null)
            throw new InvalidDataException("The original directory Move has no verified target for this projection.");
        var original = retained.Admission.Members[proof.Ordinal];
        var target = retained.Targets[proof.Ordinal];
        if (original.MemberId != member.MemberId || original.Binding != member.LocalObject ||
            original.IsDirectory != member.IsDirectory || retained.Admission.RootId != member.RootId)
            throw new InvalidDataException("The original directory Move projection no longer identifies the same member.");
        return new(operationId, original.PreviousIdentityAcceptance, original.OriginalItemId, original.OriginalRemoteId,
            target.ItemId, target.RemoteId, proof.ObservedAt);
    }

    private async Task ValidateJournalDirectoryMoveAdmissionAsync(MirrorPulseJournalDirectoryMoveAdmission admission,
        MirrorPulseMutationIntent mutation, SqliteTransaction transaction, CancellationToken token)
    {
        if (admission.JournalOperationId == Guid.Empty || admission.JournalOperationId != mutation.OperationId ||
            admission.JournalSequence <= 0 || admission.OriginalJournalPayloadSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
            admission.LocalMoveOperationId == Guid.Empty || admission.RootId.Value == Guid.Empty || admission.InstanceId != mutation.InstanceId ||
            string.IsNullOrWhiteSpace(admission.RootKey) || admission.RootKey != mutation.RootKey ||
            (mutation.PreviousRootKey ?? mutation.RootKey) != mutation.RootKey ||
            mutation.Origin != MirrorPulseMutationOrigin.Journal || mutation.Kind != MirrorPulseWorkerChangeKind.Move || !mutation.IsDirectory ||
            admission.Members is not { Count: > 0 and <= 65536 } || !admission.Root.IsDirectory ||
            string.IsNullOrEmpty(mutation.ExpectedRevision) || mutation.ExpectedRevision != admission.Root.OriginalRevision ||
            mutation.PreviousRelativePath is not { Length: > 0 } source || string.IsNullOrWhiteSpace(mutation.RelativePath) ||
            !MatchesJournalDirectoryMovePath(admission.Root.SourcePath, source) ||
            !MatchesJournalDirectoryMovePath(admission.Root.TargetPath, mutation.RelativePath))
            throw new InvalidDataException("The original journal directory Move admission is incomplete or disagrees with its mutation.");
        var graph = await ReadNamespaceDirectoryMoveCompletionGraphCoreAsync(admission.LocalMoveOperationId, transaction, token).ConfigureAwait(false);
        var move = await ReadNamespaceMemberMoveCoreAsync(admission.LocalMoveOperationId, transaction, token).ConfigureAwait(false);
        if (graph is null || move is null || move.RootId != admission.RootId || move.Version != 1 || move.RemoteEntry is not null ||
            graph.Snapshot.Entries.Count != admission.Members.Count)
            throw new InvalidDataException("The journal Move lost its retained controlled subtree.");
        for (int index = 0; index < admission.Members.Count; index++)
        {
            var member = admission.Members[index];
            var original = graph.Snapshot.Entries[index];
            var settled = graph.Completion.Entries[index];
            if (member.MemberId != original.MemberId || member.Binding != original.LocalObject ||
                member.SourcePath != original.RelativePath || member.TargetPath != settled.Completion.RelativePath ||
                member.IsDirectory != original.IsDirectory || member.OriginalItemId != settled.Completion.ItemId ||
                member.OriginalRemoteId != settled.Completion.RemoteId || member.OriginalRevision != settled.Completion.OfficialRemoteRevision ||
                member.OriginalNativeRevision != settled.Native.NativeRemoteRevision ||
                member.PreviousIdentityAcceptance != original.Identity.AcceptanceOperationId)
                throw new InvalidDataException("A journal Move member disagrees with its sealed original position and identity.");
        }
    }

    private static bool MatchesJournalDirectoryMovePath(string path, string workerPath) =>
        path.IndexOf('/') > 0 && path[(path.IndexOf('/') + 1)..].Equals(workerPath.Replace('\\', '/'), StringComparison.Ordinal);

    private static void ValidateJournalDirectoryMoveTargets(MirrorPulseJournalDirectoryMoveAdmission admission,
        IReadOnlyList<MirrorPulseDirectoryMoveTarget> targets)
    {
        if (targets.Count != admission.Members.Count) throw new InvalidDataException("A directory Move must retain every sealed target.");
        var itemIds = new HashSet<Guid>();
        var remoteIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var member = admission.Members[index];
            if (target.MemberId != member.MemberId || target.ItemId == Guid.Empty || !itemIds.Add(target.ItemId) ||
                string.IsNullOrWhiteSpace(target.RemoteId) || !remoteIds.Add(target.RemoteId) || target.Revision != member.OriginalRevision)
                throw new InvalidDataException("The directory Move target plan changed an original member or its acknowledged baseline.");
        }
    }

    private static byte[] EncodeJournalDirectoryMove<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > JournalDirectoryMovePayloadLimit) throw new InvalidDataException("The original directory Move evidence exceeds its size limit.");
        return payload;
    }

    private static T DecodeJournalDirectoryMove<T>(byte[] payload, byte[] hash)
    {
        if (payload.Length is < 1 or > JournalDirectoryMovePayloadLimit || !SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("The original directory Move evidence changed in the catalog.");
        return JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The original directory Move evidence is empty.");
    }
}
