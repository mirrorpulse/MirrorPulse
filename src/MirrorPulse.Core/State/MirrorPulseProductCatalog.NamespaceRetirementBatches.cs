using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespaceRetirementBatchPhase { Prepared, Completed, RecoveryRequired }

/// <summary>One immutable current member captured for a future leaf-first subtree retirement.</summary>
public sealed record MirrorPulseNamespaceRetirementBatchEntry(Guid MemberId, Guid? ParentMemberId,
    string RelativePath, bool IsDirectory, MirrorPulseLocalFileBinding LocalObject, string OwnerSid, int Depth);

/// <summary>A durable subtree snapshot and root fence; it is not native deletion evidence.</summary>
public sealed record MirrorPulseNamespaceRetirementBatch(int Version, Guid OperationId, Guid MemberId,
    RootId RootId, IReadOnlyList<MirrorPulseNamespaceRetirementBatchEntry> Entries,
    DateTimeOffset PreparedAt, MirrorPulseNamespaceRetirementBatchPhase Phase = MirrorPulseNamespaceRetirementBatchPhase.Prepared)
{
    /// <summary>Returns the stable operation ID for one leaf step of this sealed batch.</summary>
    public Guid MemberOperationId(Guid memberId)
    {
        if (!Entries.Any(entry => entry.MemberId == memberId)) throw new ArgumentException("The member is outside this retirement batch.", nameof(memberId));
        byte[] key = Encoding.UTF8.GetBytes($"MirrorPulse/retirement-batch/v1/{OperationId:D}/{memberId:D}");
        return new Guid(SHA256.HashData(key).AsSpan(0, 16));
    }
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchAsync(Guid operationId,
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || memberId == Guid.Empty) throw new ArgumentException("A retirement batch requires operation and member IDs.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            if (member.RootId is not { } rootId)
                throw new InvalidOperationException("A managed root or sync-root anchor cannot be retired as a subtree.");
            await RequireNamespaceRetirableMemberAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var replay = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.MemberId != memberId || replay.RootId != rootId)
                    throw new InvalidOperationException("A retirement batch operation is immutable.");
                transaction.Commit(); return replay;
            }
            await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, cancellationToken).ConfigureAwait(false);
            if (await ReadPendingNamespaceRetirementBatchCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Another controlled subtree retirement already fences this root.");
            // Capture membership and establish the fence in one write transaction. A second
            // catalog owner cannot admit an unseen child between these two operations.
            var snapshot = await ReadNamespaceMemberTreeCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            var currentMembers = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            foreach (var expected in snapshot)
            {
                var current = await ReadNamespaceMemberCoreAsync(expected.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The retirement subtree lost a registered member.");
                var location = await ReadNamespaceMemberLocationCoreAsync(current, transaction, cancellationToken).ConfigureAwait(false);
                await RequireNamespaceMemberNameAsync(current, transaction, cancellationToken).ConfigureAwait(false);
                if (current.RootId != rootId || current.IsDirectory != expected.IsDirectory || current.LocalObject != expected.LocalObject ||
                    location.ParentMemberId != expected.ParentMemberId || location.RelativePath != expected.RelativePath)
                    throw new InvalidDataException("The retirement subtree changed during admission.");
                currentMembers.Add(current.MemberId, current);
            }
            var batch = new MirrorPulseNamespaceRetirementBatch(1, operationId, memberId, rootId,
                snapshot.Select(entry => new MirrorPulseNamespaceRetirementBatchEntry(entry.MemberId, entry.ParentMemberId,
                    entry.RelativePath, entry.IsDirectory, entry.LocalObject, currentMembers[entry.MemberId].OwnerSid, entry.Depth)).ToArray(),
                DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(batch, TopologyJsonOptions);
            if (payload.Length > 4 * 1024 * 1024) throw new ArgumentException("The retirement subtree is too large.", nameof(memberId));
            await using SqliteCommand insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_member_retirement_batches(operation_id,member_id,root_id,payload,fingerprint,phase) VALUES($id,$member,$root,$payload,$fingerprint,$phase);";
            insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$member", memberId.ToString("D"));
            insert.Parameters.AddWithValue("$root", rootId.ToString()); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload)); insert.Parameters.AddWithValue("$phase", (int)batch.Phase);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); transaction.Commit(); return batch;

        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRetirementBatch?> ReadNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A retirement batch is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceRetirementBatchCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Releases the batch fence only after every sealed member has its own completed deletion receipt.</summary>
    public async Task<MirrorPulseNamespaceRetirementBatch> CompleteNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A retirement batch is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is missing.");
            if (batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.Completed) { transaction.Commit(); return batch; }
            if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared)
                throw new InvalidOperationException("The retirement batch requires explicit recovery.");
            foreach (var entry in batch.Entries)
            {
                var receipt = await ReadNamespaceRetirementCoreAsync(batch.MemberOperationId(entry.MemberId), transaction, cancellationToken).ConfigureAwait(false);
                if (receipt is null || receipt.Phase != MirrorPulseNamespaceRetirementPhase.Completed || !RetirementMatchesBatchEntry(receipt.Intent, batch, entry))
                    throw new InvalidOperationException("The retirement batch has an unsettled member.");
                await using SqliteCommand name = _connection.CreateCommand(); name.Transaction = transaction;
                name.CommandText = "SELECT 1 FROM namespace_member_names WHERE member_id=$member;";
                name.Parameters.AddWithValue("$member", entry.MemberId.ToString("D"));
                if (await name.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("A completed subtree retirement still owns a member name.");
            }
            var completed = batch with { Phase = MirrorPulseNamespaceRetirementBatchPhase.Completed };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completed, TopologyJsonOptions);
            await using SqliteCommand update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash,phase=$phase WHERE operation_id=$id;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$phase", (int)completed.Phase); update.Parameters.AddWithValue("$id", operationId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The retirement batch disappeared during completion.");
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireNamespaceRetirementBatchEntryAsync(MirrorPulseNamespaceRetirementIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.BatchOperationId is not { } operationId) return;
        var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The retirement batch is missing.");
        var entry = batch.Entries.SingleOrDefault(entry => entry.MemberId == intent.MemberId);
        if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared || entry is null || !RetirementMatchesBatchEntry(intent, batch, entry))
            throw new InvalidOperationException("The retirement must follow its sealed batch member and object binding.");
    }

    private static bool RetirementMatchesBatchEntry(MirrorPulseNamespaceRetirementIntent intent,
        MirrorPulseNamespaceRetirementBatch batch, MirrorPulseNamespaceRetirementBatchEntry entry) =>
        intent.BatchOperationId == batch.OperationId && intent.OperationId == batch.MemberOperationId(entry.MemberId) &&
        intent.MemberId == entry.MemberId && intent.RootId == batch.RootId && intent.LocalObject == entry.LocalObject &&
        intent.OwnerSid == entry.OwnerSid && intent.RelativePath == entry.RelativePath && intent.IsDirectory == entry.IsDirectory && intent.PreparedAt >= batch.PreparedAt;

    private async Task<MirrorPulseNamespaceRetirementBatch?> ReadNamespaceRetirementBatchCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,member_id,root_id,payload,fingerprint,phase FROM namespace_member_retirement_batches WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[3], fingerprint = (byte[])reader[4];
        if (payload.Length > 4 * 1024 * 1024 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The retirement batch fingerprint is invalid.");
        var batch = JsonSerializer.Deserialize<MirrorPulseNamespaceRetirementBatch>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The retirement batch is empty.");
        if (batch.Version != 1 || batch.OperationId != operationId || batch.MemberId == Guid.Empty || batch.RootId.Value == Guid.Empty ||
            batch.Entries.Count == 0 || batch.Entries.Count > 65_536 || batch.PreparedAt == default || !Enum.IsDefined(batch.Phase) || reader.GetString(0) != operationId.ToString("D") ||
            reader.GetString(1) != batch.MemberId.ToString("D") || reader.GetString(2) != batch.RootId.ToString() ||
            reader.GetInt32(5) != (int)batch.Phase || !JsonSerializer.SerializeToUtf8Bytes(batch, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The retirement batch index is inconsistent.");
        var entries = new Dictionary<Guid, MirrorPulseNamespaceRetirementBatchEntry>();
        foreach (var entry in batch.Entries)
        {
            ValidatePermissionBinding(entry.LocalObject);
            ValidatePermissionSid(entry.OwnerSid, role: false);
            ValidatePermissionRelativePath(entry.RelativePath);
            if (entry.MemberId == Guid.Empty || entry.ParentMemberId is null || entry.ParentMemberId == Guid.Empty || entry.Depth < 0 ||
                !entries.TryAdd(entry.MemberId, entry))
                throw new InvalidDataException("The retirement batch contains an invalid member entry.");
        }
        if (!entries.TryGetValue(batch.MemberId, out var root) || root.Depth != 0 ||
            batch.Entries.Count(entry => entry.Depth == 0) != 1 || entries.ContainsKey(root.ParentMemberId!.Value))
            throw new InvalidDataException("The retirement batch has an invalid subtree root.");
        foreach (var entry in batch.Entries.Where(entry => entry.Depth != 0))
        {
            int separator = entry.RelativePath.LastIndexOf('/');
            if (!entries.TryGetValue(entry.ParentMemberId!.Value, out var parent) || !parent.IsDirectory ||
                entry.Depth != parent.Depth + 1 || separator < 0 || entry.RelativePath[..separator] != parent.RelativePath ||
                entry.LocalObject.VolumeSerialNumber != parent.LocalObject.VolumeSerialNumber ||
                entry.LocalObject.SyncRootFileId != parent.LocalObject.SyncRootFileId || entry.OwnerSid != parent.OwnerSid)
                throw new InvalidDataException("The retirement batch does not describe one complete connected subtree.");
        }
        return batch;
    }

    private async Task<MirrorPulseNamespaceRetirementBatch?> ReadPendingNamespaceRetirementBatchCoreAsync(RootId rootId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id FROM namespace_member_retirement_batches WHERE root_id=$root AND phase<>$completed LIMIT 1;";
        query.Parameters.AddWithValue("$completed", (int)MirrorPulseNamespaceRetirementBatchPhase.Completed);
        query.Parameters.AddWithValue("$root", rootId.ToString());
        object? value = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is string id && Guid.TryParseExact(id, "D", out Guid operation)
            ? await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token).ConfigureAwait(false)
            : null;
    }

    private async Task<List<MirrorPulseNamespaceRetirementBatch>> ReadPendingNamespaceRetirementBatchesCoreAsync(
        SqliteTransaction transaction, CancellationToken token)
    {
        var result = new List<MirrorPulseNamespaceRetirementBatch>();
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id FROM namespace_member_retirement_batches WHERE phase<>$completed ORDER BY operation_id;";
        query.Parameters.AddWithValue("$completed", (int)MirrorPulseNamespaceRetirementBatchPhase.Completed);
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid operation)) throw new InvalidDataException("The retirement batch operation index is invalid.");
            result.Add(await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retirement batch disappeared."));
        }
        return result;
    }
}
