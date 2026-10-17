using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Original registered subtree facts, before one unchanged SDK directory delete.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeEntry(int Ordinal, int Depth,
    MirrorPulseNamespaceMemberProtectionSnapshot Original, string? NativeRemoteRevision, string? OfficialRemoteRevision);

public sealed record MirrorPulseNamespaceRemoteRetirementTreePage(
    IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeEntry> Entries, int LastOrdinal, bool HasMore);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains the complete original registered tree and original SDK entry in one transaction.</summary>
    /// <remarks>Native inspection remains with the Host. This admission creates no SDK child events,
    /// deletes no object, releases no name and publishes no cursor.</remarks>
    public async Task<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRemoteRetirementTreeAsync(Guid operationId,
        MirrorPulseNamespaceRemoteRetirementReference original, IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeEntry> entries,
        DateTimeOffset preparedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(entries);
        if (operationId == Guid.Empty || preparedAt == default || entries.Count is < 1 or > 65_536)
            throw new ArgumentException("An original remote tree requires a bounded complete admission.");
        var root = original.Child.Context.Member;
        var intent = TreeRootIntent(operationId, original, preparedAt);
        ValidateNamespaceRetirementIntent(intent);
        if (!root.IsDirectory) throw new ArgumentException("A remote retirement tree requires a registered directory.", nameof(original));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            string fingerprint = RemoteRetirementTreeFingerprint(entries);
            if (replay is not null)
            {
                if (replay.Version != 2 || replay.RemoteEntry != original || replay.PreparedAt != preparedAt ||
                    replay.RemoteTreeFingerprint != fingerprint || replay.Entries.Count != entries.Count)
                    throw new InvalidOperationException("The original SDK retirement tree is immutable.");
                transaction.Commit(); return replay;
            }
            await RequireNamespaceRetirableMemberAsync(root, transaction, cancellationToken).ConfigureAwait(false);
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken).ConfigureAwait(false)).Contains(intent.RootId))
                throw new InvalidOperationException("Another namespace owner prevents original SDK tree admission.");
            await ValidateRemoteRetirementCurrentAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            var graph = await ReadNamespaceMemberTreeCoreAsync(root.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (graph.Count != entries.Count) throw new InvalidOperationException("The SDK tree must retain every current registered descendant.");
            var nodes = graph.ToDictionary(entry => entry.MemberId);
            var seen = new HashSet<Guid>();
            for (int ordinal = 0; ordinal < entries.Count; ordinal++)
            {
                var entry = entries[ordinal]; ValidateRemoteTreeEntry(entry);
                var member = entry.Original.Context.Member;
                if (entry.Ordinal != ordinal || !seen.Add(member.MemberId) || !nodes.TryGetValue(member.MemberId, out var node) ||
                    entry.Depth != node.Depth || member.RootId != intent.RootId || member.OwnerSid != intent.OwnerSid ||
                    entry.Original.Context.Protection.RoleSid != intent.RoleSid || node.LocalObject != member.LocalObject ||
                    node.RelativePath != entry.Original.Context.Location.RelativePath || node.ParentMemberId != entry.Original.Context.Location.ParentMemberId ||
                    node.IsDirectory != member.IsDirectory || entry.Original.Context.Verification.ObservedAt > preparedAt ||
                    await ReadNamespaceMemberProtectionSnapshotCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false) != entry.Original ||
                    ordinal == 0 && (member.MemberId != root.MemberId || entry.Original != original.Child ||
                        entry.NativeRemoteRevision != original.NativeRemoteRevision || entry.OfficialRemoteRevision != original.OfficialRemoteRevision) ||
                    ordinal != 0 && (entry.Original.Context.Location.ParentMemberId is not { } parentId || !seen.Contains(parentId)))
                    throw new InvalidOperationException("The SDK tree changed its original member, parent, identity, position or protection.");
            }
            var batch = new MirrorPulseNamespaceRetirementBatch(2, operationId, root.MemberId, intent.RootId,
                entries.Select(entry => new MirrorPulseNamespaceRetirementBatchEntry(entry.Original.Context.Member.MemberId,
                    entry.Original.Context.Location.ParentMemberId, entry.Original.Context.Location.RelativePath, entry.Original.Context.Member.IsDirectory,
                    entry.Original.Context.Member.LocalObject, entry.Original.Context.Member.OwnerSid, entry.Depth)).ToArray(), preparedAt)
            { RemoteEntry = original, RemoteTreeFingerprint = fingerprint };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(batch, TopologyJsonOptions);
            if (payload.Length > 4 * 1024 * 1024) throw new ArgumentException("The retained retirement graph exceeds its catalog bound.", nameof(entries));
            await ValidateNamespaceRemoteEntryCrossKindAsync(operationId, original.InstanceId, original.BatchId, original.BatchFingerprint,
                original.BatchEntryCount, original.Ordinal, original.ChangeId, 3, transaction, cancellationToken).ConfigureAwait(false);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_member_retirement_batches(operation_id,member_id,root_id,payload,fingerprint,phase) VALUES($id,$member,$root,$payload,$hash,0);";
                insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$member", root.MemberId.ToString("D"));
                insert.Parameters.AddWithValue("$root", intent.RootId.ToString()); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var owner = _connection.CreateCommand())
            {
                owner.Transaction = transaction;
                owner.CommandText = "INSERT INTO namespace_remote_retirement_tree_owners(operation_id,instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,tree_count,tree_fingerprint) VALUES($operation,$instance,$batch,$hash,$count,$ordinal,$change,$treeCount,$treeHash);";
                AddRemoteRetirementParameters(owner, intent); owner.Parameters.AddWithValue("$treeCount", entries.Count);
                owner.Parameters.AddWithValue("$treeHash", fingerprint); await owner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var entry in entries)
            {
                byte[] row = JsonSerializer.SerializeToUtf8Bytes(entry, TopologyJsonOptions);
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_remote_retirement_tree_entries(operation_id,ordinal,member_id,payload,fingerprint) VALUES($operation,$ordinal,$member,$payload,$hash);";
                insert.Parameters.AddWithValue("$operation", operationId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", entry.Ordinal);
                insert.Parameters.AddWithValue("$member", entry.Original.Context.Member.MemberId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", row); insert.Parameters.AddWithValue("$hash", SHA256.HashData(row));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return batch;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRetirementBatch?> ReadNamespaceRemoteRetirementTreeAsync(InstanceId instance,
        string batchId, int ordinal, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instance, batchId, ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT operation_id FROM namespace_remote_retirement_tree_owners WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;";
            query.Parameters.AddWithValue("$instance", instance.ToString()); query.Parameters.AddWithValue("$batch", batchId); query.Parameters.AddWithValue("$ordinal", ordinal);
            string? id = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            var result = id is null ? null : await ReadNamespaceRetirementBatchCoreAsync(ParseNamespaceMemberId(id), transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote tree owner lost its admission.");
            if (result is not null && (result.RemoteEntry is not { } remote || remote.InstanceId != instance || remote.BatchId != batchId || remote.Ordinal != ordinal))
                throw new InvalidDataException("The original remote tree lookup changed its SDK entry.");
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementTreePage> ReadNamespaceRemoteRetirementTreePageAsync(Guid operationId,
        int afterOrdinal = -1, int limit = 64, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || afterOrdinal < -1 || limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(afterOrdinal));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote retirement tree is missing.");
            if (batch.RemoteEntry is null || afterOrdinal >= batch.Entries.Count) throw new ArgumentException("The page requires its original SDK tree and ordinal.");
            var result = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>();
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT ordinal,member_id,payload,fingerprint FROM namespace_remote_retirement_tree_entries WHERE operation_id=$id AND ordinal>$after ORDER BY ordinal LIMIT $limit;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D")); query.Parameters.AddWithValue("$after", afterOrdinal); query.Parameters.AddWithValue("$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int ordinal = reader.GetInt32(0); byte[] payload = (byte[])reader[2], hash = (byte[])reader[3];
                if (ordinal != afterOrdinal + result.Count + 1 || ordinal >= batch.Entries.Count || payload.Length > 131_072 || hash.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash)) throw new InvalidDataException("The original remote tree page is corrupt or incomplete.");
                var entry = JsonSerializer.Deserialize<MirrorPulseNamespaceRemoteRetirementTreeEntry>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The original remote tree entry is empty.");
                ValidateRemoteTreeEntry(entry);
                var retained = batch.Entries[ordinal];
                if (entry.Ordinal != ordinal || entry.Depth != retained.Depth || reader.GetString(1) != retained.MemberId.ToString("D") ||
                    entry.Original.Context.Member.MemberId != retained.MemberId || entry.Original.Context.Member.LocalObject != retained.LocalObject ||
                    entry.Original.Context.Member.OwnerSid != retained.OwnerSid || entry.Original.Context.Member.IsDirectory != retained.IsDirectory ||
                    entry.Original.Context.Location.RelativePath != retained.RelativePath || entry.Original.Context.Location.ParentMemberId != retained.ParentMemberId ||
                    !JsonSerializer.SerializeToUtf8Bytes(entry, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The original remote tree entry changed its sealed member.");
                result.Add(entry);
            }
            if (result.Count != Math.Min(limit, batch.Entries.Count - afterOrdinal - 1)) throw new InvalidDataException("The original remote tree page lost a retained entry.");
            int last = result.Count == 0 ? afterOrdinal : result[^1].Ordinal;
            await reader.DisposeAsync().ConfigureAwait(false);
            transaction.Commit(); return new(result.AsReadOnly(), last, last + 1 < batch.Entries.Count);
        }
        finally { _gate.Release(); }
    }

    private async Task ValidateRemoteRetirementTreeOwnerAsync(MirrorPulseNamespaceRetirementBatch batch, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,tree_count,tree_fingerprint FROM namespace_remote_retirement_tree_owners WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", batch.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false); bool exists = await reader.ReadAsync(token).ConfigureAwait(false);
        if (batch.RemoteEntry is not { } remote)
        {
            if (exists) throw new InvalidDataException("A controlled retirement batch acquired a remote SDK owner.");
            return;
        }
        ValidateNamespaceRetirementIntent(TreeRootIntent(batch.OperationId, remote, batch.PreparedAt));
        if (!exists || !remote.Child.Context.Member.IsDirectory || batch.MemberId != remote.Child.Context.Member.MemberId || batch.RootId != remote.Child.Context.Member.RootId ||
            batch.RemoteTreeFingerprint!.Any(character => !char.IsAsciiHexDigitUpper(character)) || reader.GetString(0) != remote.InstanceId.ToString() ||
            reader.GetString(1) != remote.BatchId || reader.GetString(2) != remote.BatchFingerprint || reader.GetInt32(3) != remote.BatchEntryCount ||
            reader.GetInt32(4) != remote.Ordinal || reader.GetString(5) != remote.ChangeId || reader.GetInt32(6) != batch.Entries.Count || reader.GetString(7) != batch.RemoteTreeFingerprint)
            throw new InvalidDataException("The original SDK tree owner index disagrees with its immutable admission.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidateNamespaceRemoteEntryCrossKindAsync(batch.OperationId, remote.InstanceId, remote.BatchId, remote.BatchFingerprint,
            remote.BatchEntryCount, remote.Ordinal, remote.ChangeId, 3, transaction, token).ConfigureAwait(false);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var rows = _connection.CreateCommand(); rows.Transaction = transaction;
        rows.CommandText = "SELECT ordinal,fingerprint FROM namespace_remote_retirement_tree_entries WHERE operation_id=$id ORDER BY ordinal;";
        rows.Parameters.AddWithValue("$id", batch.OperationId.ToString("D"));
        await using var values = await rows.ExecuteReaderAsync(token).ConfigureAwait(false); int count = 0;
        while (await values.ReadAsync(token).ConfigureAwait(false))
        {
            byte[] hash = (byte[])values[1];
            if (values.GetInt32(0) != count || hash.Length != 32) throw new InvalidDataException("The original remote tree manifest lost a sealed ordinal.");
            AppendRemoteTreeDigest(digest, count++, hash);
        }
        if (count != batch.Entries.Count || Convert.ToHexString(digest.GetHashAndReset()) != batch.RemoteTreeFingerprint)
            throw new InvalidDataException("The original remote tree manifest changed its complete fingerprint.");
    }

    private static MirrorPulseNamespaceRetirementIntent TreeRootIntent(Guid operationId, MirrorPulseNamespaceRemoteRetirementReference original, DateTimeOffset prepared) =>
        new(2, operationId, original.Child.Context.Member.MemberId, original.Child.Context.Member.RootId!.Value,
            original.Child.Context.Member.LocalObject, original.Child.Context.Location.RelativePath, original.Child.Context.Member.IsDirectory,
            original.Child.Context.Member.OwnerSid, original.Child.Context.Protection.RoleSid, prepared)
        { RemoteEntry = original };

    private static string RemoteRetirementTreeFingerprint(IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeEntry> entries)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry); ValidateRemoteTreeEntry(entry);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("An original remote tree entry must be bounded.", nameof(entries));
            AppendRemoteTreeDigest(digest, entry.Ordinal, SHA256.HashData(payload));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static void AppendRemoteTreeDigest(IncrementalHash digest, int ordinal, byte[] hash)
    {
        Span<byte> encoded = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(encoded, ordinal);
        digest.AppendData(encoded); digest.AppendData(hash);
    }

    private static void ValidateRemoteTreeEntry(MirrorPulseNamespaceRemoteRetirementTreeEntry entry)
    {
        if (entry.Ordinal < 0 || entry.Depth < 0 || entry.Original is not { } original || original.Context is not { } context ||
            original.Identity.MemberId != context.Member.MemberId || original.Identity.ItemId is null || original.Identity.ItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(original.Identity.RemoteId) || context.Member.RootId is null || context.Location.MemberId != context.Member.MemberId ||
            context.Location.ParentMemberId is null || context.Protection.MemberId != context.Member.MemberId ||
            context.Verification.LocalObject != context.Member.LocalObject || context.Verification.OwnerSid != context.Member.OwnerSid ||
            context.Verification.ObservedAt == default || new[] { entry.NativeRemoteRevision, entry.OfficialRemoteRevision }
                .Any(value => value is not null && (value.Length > 4096 || value.Contains('\0'))))
            throw new ArgumentException("An original remote tree entry requires its complete registered identity and protection.", nameof(entry));
        ValidatePermissionBinding(context.Member.LocalObject); ValidatePermissionSid(context.Member.OwnerSid, false);
        ValidatePermissionSid(context.Protection.RoleSid, true); ValidatePermissionDacl(context.Verification.Dacl);
    }

    private static async Task EnsureNamespaceRemoteRetirementTreeSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_owners(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_retirement_batches(operation_id),
                instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
                entry_count INTEGER NOT NULL CHECK(entry_count>0),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),change_id TEXT NOT NULL,
                tree_count INTEGER NOT NULL CHECK(tree_count>0 AND tree_count<=65536),tree_fingerprint TEXT NOT NULL CHECK(length(tree_fingerprint)=64),
                UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_entries(
                operation_id TEXT NOT NULL REFERENCES namespace_remote_retirement_tree_owners(operation_id),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0),member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),PRIMARY KEY(operation_id,ordinal),UNIQUE(operation_id,member_id));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
