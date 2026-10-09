using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespacePermissionTreePhase { Capturing, Sealed }

/// <summary>An immutable capture definition for one existing managed-root subtree.</summary>
/// <remarks>ExpectedMembers describes the capture, not independent proof that the live tree is complete.</remarks>
public sealed record MirrorPulseNamespacePermissionTreeDefinition(Guid ManifestId,
    MirrorPulseNamespacePermissionPreparation Anchor, int ExpectedMembers, DateTimeOffset CreatedAt);

/// <summary>A parent-before-child capture of existing object evidence and its planned permission change.</summary>
/// <remarks>Objects created under protection require separate birth and restoration evidence.</remarks>
public sealed record MirrorPulseNamespacePermissionTreeMember(int Sequence, Guid? ParentEvidenceId,
    MirrorPulseNamespacePermissionPreparation Preparation);

public sealed record MirrorPulseNamespacePermissionTreeSeal(int MemberCount, string Fingerprint, DateTimeOffset SealedAt);

/// <summary>A durable evidence set. Sealed is not TreeReady or authorization to mutate the namespace.</summary>
public sealed record MirrorPulseNamespacePermissionTree(MirrorPulseNamespacePermissionTreeDefinition Definition,
    MirrorPulseNamespacePermissionTreePhase Phase, int CapturedMembers, MirrorPulseNamespacePermissionTreeSeal? Seal = null);

public sealed partial class MirrorPulseProductCatalog
{
    private const int PermissionTreePageLimit = 4096;
    private const int PermissionTreeMemberLimit = 1_000_000;

    public async Task<MirrorPulseNamespacePermissionTree> CreateNamespacePermissionTreeAsync(
        MirrorPulseNamespacePermissionTreeDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidatePermissionTreeDefinition(definition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseNamespacePermissionTree? retained = await ReadPermissionTreeCoreAsync(definition.ManifestId, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained.Definition != definition) throw new InvalidOperationException("The permission tree definition is immutable.");
                return retained;
            }
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO namespace_permission_trees(manifest_id,root_id,volume_serial,sync_root_file_id,payload,phase,captured_count)
                VALUES($id,$root,$volume,$sync,$payload,0,0);
                """;
            insert.Parameters.AddWithValue("$id", definition.ManifestId.ToString("D"));
            insert.Parameters.AddWithValue("$root", definition.Anchor.Intent.RootId!.Value.Value.ToString("D"));
            insert.Parameters.AddWithValue("$volume", definition.Anchor.Baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$sync", definition.Anchor.Baseline.LocalObject.SyncRootFileId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(definition, TopologyJsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new(definition, MirrorPulseNamespacePermissionTreePhase.Capturing, 0);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists one bounded capture page without creating executable permission changes.</summary>
    /// <remarks>A replay must match the entire retained page. A failed later member rolls back the page.</remarks>
    public async Task<MirrorPulseNamespacePermissionTree> AppendNamespacePermissionTreeMembersAsync(Guid manifestId,
        IReadOnlyList<MirrorPulseNamespacePermissionTreeMember> members, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (manifestId == Guid.Empty || members.Count is < 1 or > PermissionTreePageLimit)
            throw new ArgumentException("A permission capture page must contain 1 to 4096 members.", nameof(members));
        MirrorPulseNamespacePermissionTreeMember[] captured = members.ToArray();
        if (captured.Any(member => member is null) || captured[0].Sequence < 0 ||
            captured.Where((member, index) => member.Sequence != (long)captured[0].Sequence + index).Any())
            throw new ArgumentException("The permission capture page must have contiguous nonnegative sequences.", nameof(members));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseNamespacePermissionTree tree = await ReadPermissionTreeCoreAsync(manifestId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission tree is not registered.");
            foreach (MirrorPulseNamespacePermissionTreeMember member in captured) ValidatePermissionTreeMember(tree.Definition, member);
            int first = captured[0].Sequence;
            long end = (long)first + captured.Length;
            if (first < tree.CapturedMembers)
            {
                if (end > tree.CapturedMembers) throw new InvalidOperationException("A replay cannot extend a partially retained page.");
                IReadOnlyList<MirrorPulseNamespacePermissionTreeMember> replay = await ReadPermissionTreeMembersCoreAsync(
                    tree.Definition, first - 1, captured.Length, cancellationToken, transaction).ConfigureAwait(false);
                if (!replay.SequenceEqual(captured)) throw new InvalidOperationException("Captured original permission evidence is immutable.");
                transaction.Commit();
                return tree;
            }
            if (tree.Phase != MirrorPulseNamespacePermissionTreePhase.Capturing || first != tree.CapturedMembers || end > tree.Definition.ExpectedMembers)
                throw new InvalidOperationException("The capture page does not follow the open permission tree.");
            foreach (MirrorPulseNamespacePermissionTreeMember member in captured)
            {
                await ValidatePermissionTreeCaptureCoreAsync(tree.Definition, member, transaction, cancellationToken).ConfigureAwait(false);
                await InsertPermissionTreeMemberCoreAsync(manifestId, member, transaction, cancellationToken).ConfigureAwait(false);
            }
            await using SqliteCommand advance = _connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = "UPDATE namespace_permission_trees SET captured_count=$count WHERE manifest_id=$id AND phase=0 AND captured_count=$previous;";
            advance.Parameters.AddWithValue("$id", manifestId.ToString("D"));
            advance.Parameters.AddWithValue("$count", end);
            advance.Parameters.AddWithValue("$previous", tree.CapturedMembers);
            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The permission capture cursor changed.");
            transaction.Commit();
            return tree with { CapturedMembers = (int)end };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Seals the complete captured evidence set. No ACL is written and no tree is admitted.</summary>
    public async Task<MirrorPulseNamespacePermissionTree> SealNamespacePermissionTreeAsync(Guid manifestId,
        DateTimeOffset sealedAt, CancellationToken cancellationToken = default)
    {
        if (manifestId == Guid.Empty || sealedAt == default) throw new ArgumentException("The permission tree seal is incomplete.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseNamespacePermissionTree tree = await ReadPermissionTreeCoreAsync(manifestId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission tree is not registered.");
            if (tree.CapturedMembers != tree.Definition.ExpectedMembers || sealedAt < tree.Definition.CreatedAt)
                throw new InvalidOperationException("The original permission capture is incomplete.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendPermissionTreeFingerprint(hash, JsonSerializer.Serialize(tree.Definition, TopologyJsonOptions));
            int count = 0;
            while (count < tree.CapturedMembers)
            {
                IReadOnlyList<MirrorPulseNamespacePermissionTreeMember> page = await ReadPermissionTreeMembersCoreAsync(
                    tree.Definition, count - 1, PermissionTreePageLimit, cancellationToken, transaction).ConfigureAwait(false);
                if (page.Count == 0) throw new InvalidDataException("The permission capture has a missing page.");
                foreach (MirrorPulseNamespacePermissionTreeMember member in page)
                {
                    if (member.Sequence != count++ || member.Preparation.Intent.PreparedAt > sealedAt)
                        throw new InvalidDataException("The permission capture has an invalid sequence or observation time.");
                    AppendPermissionTreeFingerprint(hash, JsonSerializer.Serialize(member, TopologyJsonOptions));
                }
            }
            if (count != tree.CapturedMembers) throw new InvalidDataException("The permission capture cursor does not match its members.");
            var seal = new MirrorPulseNamespacePermissionTreeSeal(count, Convert.ToHexString(hash.GetHashAndReset()), sealedAt);
            if (tree.Phase == MirrorPulseNamespacePermissionTreePhase.Sealed)
            {
                if (tree.Seal != seal) throw new InvalidOperationException("The retained permission seal is immutable.");
                transaction.Commit();
                return tree;
            }
            await using SqliteCommand write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "UPDATE namespace_permission_trees SET phase=1,seal=$seal WHERE manifest_id=$id AND phase=0 AND captured_count=$count;";
            write.Parameters.AddWithValue("$id", manifestId.ToString("D"));
            write.Parameters.AddWithValue("$seal", JsonSerializer.Serialize(seal, TopologyJsonOptions));
            write.Parameters.AddWithValue("$count", count);
            if (await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The permission tree could not be sealed.");
            transaction.Commit();
            return tree with { Phase = MirrorPulseNamespacePermissionTreePhase.Sealed, Seal = seal };
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespacePermissionTree?> ReadNamespacePermissionTreeAsync(Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPermissionTreeCoreAsync(manifestId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseNamespacePermissionTreeMember>> ReadNamespacePermissionTreeMembersAsync(
        Guid manifestId, int afterSequence = -1, int limit = PermissionTreePageLimit, CancellationToken cancellationToken = default)
    {
        if (manifestId == Guid.Empty || afterSequence < -1 || limit is < 1 or > PermissionTreePageLimit)
            throw new ArgumentException("The permission capture page request is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseNamespacePermissionTree tree = await ReadPermissionTreeCoreAsync(manifestId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission tree is not registered.");
            return await ReadPermissionTreeMembersCoreAsync(tree.Definition, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespacePermissionTree?> ReadPermissionTreeCoreAsync(Guid manifestId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload,phase,captured_count,seal,root_id,volume_serial,sync_root_file_id FROM namespace_permission_trees WHERE manifest_id=$id;";
        query.Parameters.AddWithValue("$id", manifestId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var definition = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionTreeDefinition>(reader.GetString(0), TopologyJsonOptions)
            ?? throw new InvalidDataException("The permission tree definition is missing.");
        ValidatePermissionTreeDefinition(definition);
        var phase = (MirrorPulseNamespacePermissionTreePhase)reader.GetInt32(1);
        int count = reader.GetInt32(2);
        var seal = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<MirrorPulseNamespacePermissionTreeSeal>(reader.GetString(3), TopologyJsonOptions);
        if (definition.ManifestId != manifestId || definition.Anchor.Intent.RootId!.Value.Value.ToString("D") != reader.GetString(4) ||
            definition.Anchor.Baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(5) ||
            definition.Anchor.Baseline.LocalObject.SyncRootFileId.ToString("D") != reader.GetString(6) ||
            !Enum.IsDefined(phase) || count < 0 || count > definition.ExpectedMembers ||
            (phase == MirrorPulseNamespacePermissionTreePhase.Sealed) != (seal is not null) || seal is not null &&
            (count != definition.ExpectedMembers || seal.MemberCount != count || seal.SealedAt < definition.CreatedAt ||
                seal.Fingerprint is null || seal.Fingerprint.Length != 64 || seal.Fingerprint.Any(character => !char.IsAsciiHexDigitUpper(character))))
            throw new InvalidDataException("The retained permission tree is inconsistent.");
        return new(definition, phase, count, seal);
    }

    private async Task<IReadOnlyList<MirrorPulseNamespacePermissionTreeMember>> ReadPermissionTreeMembersCoreAsync(
        MirrorPulseNamespacePermissionTreeDefinition definition, int afterSequence, int limit, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT sequence,evidence_id,operation_id,volume_serial,sync_root_file_id,local_file_id,
                relative_path,parent_evidence_id,payload,fingerprint,relative_path_key
            FROM namespace_permission_tree_members WHERE manifest_id=$id AND sequence>$after
            ORDER BY sequence LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$id", definition.ManifestId.ToString("D"));
        query.Parameters.AddWithValue("$after", afterSequence);
        query.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        var members = new List<MirrorPulseNamespacePermissionTreeMember>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            string payload = reader.GetString(8);
            var member = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionTreeMember>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original permission tree member is missing.");
            ValidatePermissionTreeMember(definition, member);
            MirrorPulseNamespacePermissionPreparation preparation = member.Preparation;
            MirrorPulseLocalFileBinding binding = preparation.Baseline.LocalObject;
            if (member.Sequence != reader.GetInt32(0) || preparation.Baseline.EvidenceId.ToString("D") != reader.GetString(1) ||
                preparation.Intent.OperationId.ToString("D") != reader.GetString(2) ||
                binding.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(3) ||
                binding.SyncRootFileId.ToString("D") != reader.GetString(4) || binding.LocalFileId.ToString("D") != reader.GetString(5) ||
                preparation.Intent.RelativePath != reader.GetString(6) || member.ParentEvidenceId?.ToString("D") != (reader.IsDBNull(7) ? null : reader.GetString(7)) ||
                !SHA256.HashData(Encoding.UTF8.GetBytes(payload)).AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(9)) ||
                !string.Equals(preparation.Intent.RelativePath.ToUpperInvariant(), reader.GetString(10), StringComparison.Ordinal))
                throw new InvalidDataException("The permission tree member's immutable indexes or fingerprint changed.");
            members.Add(member);
        }
        return members.AsReadOnly();
    }

    private async Task ValidatePermissionTreeCaptureCoreAsync(MirrorPulseNamespacePermissionTreeDefinition definition,
        MirrorPulseNamespacePermissionTreeMember member, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.ParentEvidenceId is not null)
        {
            await using SqliteCommand parent = _connection.CreateCommand();
            parent.Transaction = transaction;
            parent.CommandText = "SELECT sequence,payload FROM namespace_permission_tree_members WHERE manifest_id=$id AND evidence_id=$parent;";
            parent.Parameters.AddWithValue("$id", definition.ManifestId.ToString("D"));
            parent.Parameters.AddWithValue("$parent", member.ParentEvidenceId.Value.ToString("D"));
            await using SqliteDataReader reader = await parent.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The original parent directory must be captured first.");
            var retainedParent = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionTreeMember>(reader.GetString(1), TopologyJsonOptions)
                ?? throw new InvalidDataException("The original parent directory is missing.");
            ValidatePermissionTreeMember(definition, retainedParent);
            string path = member.Preparation.Intent.RelativePath;
            if (reader.GetInt32(0) >= member.Sequence || !retainedParent.Preparation.Baseline.IsDirectory ||
                path[..path.LastIndexOf('/')] != retainedParent.Preparation.Intent.RelativePath)
                throw new InvalidOperationException("The capture requires the original immediate parent directory.");
        }
        MirrorPulseNamespacePermissionPreparation preparation = member.Preparation;
        MirrorPulseNamespacePermissionBaseline? baseline = await ReadPermissionBaselineCoreAsync(
            preparation.Baseline.EvidenceId, token, transaction).ConfigureAwait(false);
        if (baseline is not null && baseline != preparation.Baseline)
            throw new InvalidOperationException("A permission capture cannot rewrite retained original evidence.");
        await using SqliteCommand conflict = _connection.CreateCommand();
        conflict.Transaction = transaction;
        conflict.CommandText = """
            SELECT 1 FROM namespace_permission_changes WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_permission_baselines
                WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file AND evidence_id<>$evidence LIMIT 1;
            """;
        conflict.Parameters.AddWithValue("$operation", preparation.Intent.OperationId.ToString("D"));
        conflict.Parameters.AddWithValue("$volume", preparation.Baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
        conflict.Parameters.AddWithValue("$sync", preparation.Baseline.LocalObject.SyncRootFileId.ToString("D"));
        conflict.Parameters.AddWithValue("$file", preparation.Baseline.LocalObject.LocalFileId.ToString("D"));
        conflict.Parameters.AddWithValue("$evidence", preparation.Baseline.EvidenceId.ToString("D"));
        if (await conflict.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A new permission capture cannot adopt executed or differently owned evidence.");
    }

    private async Task InsertPermissionTreeMemberCoreAsync(Guid manifestId, MirrorPulseNamespacePermissionTreeMember member,
        SqliteTransaction transaction, CancellationToken token)
    {
        string payload = JsonSerializer.Serialize(member, TopologyJsonOptions);
        MirrorPulseNamespacePermissionPreparation preparation = member.Preparation;
        MirrorPulseLocalFileBinding binding = preparation.Baseline.LocalObject;
        await using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO namespace_permission_tree_members(manifest_id,sequence,evidence_id,operation_id,volume_serial,
                sync_root_file_id,local_file_id,relative_path,relative_path_key,parent_evidence_id,payload,fingerprint)
            VALUES($id,$sequence,$evidence,$operation,$volume,$sync,$file,$path,$pathKey,$parent,$payload,$fingerprint);
            """;
        insert.Parameters.AddWithValue("$id", manifestId.ToString("D"));
        insert.Parameters.AddWithValue("$sequence", member.Sequence);
        insert.Parameters.AddWithValue("$evidence", preparation.Baseline.EvidenceId.ToString("D"));
        insert.Parameters.AddWithValue("$operation", preparation.Intent.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$volume", binding.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$sync", binding.SyncRootFileId.ToString("D"));
        insert.Parameters.AddWithValue("$file", binding.LocalFileId.ToString("D"));
        insert.Parameters.AddWithValue("$path", preparation.Intent.RelativePath);
        insert.Parameters.AddWithValue("$pathKey", preparation.Intent.RelativePath.ToUpperInvariant());
        insert.Parameters.AddWithValue("$parent", (object?)member.ParentEvidenceId?.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task ValidatePermissionTreeAdmissionAsync(MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent, SqliteTransaction transaction, CancellationToken token)
    {
        await using (SqliteCommand capture = _connection.CreateCommand())
        {
            capture.Transaction = transaction;
            capture.CommandText = """
                SELECT 1 FROM namespace_permission_trees WHERE phase=0 AND volume_serial=$volume
                    AND sync_root_file_id=$sync AND (root_id=$root OR $root IS NULL) LIMIT 1;
                """;
            capture.Parameters.AddWithValue("$volume", baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
            capture.Parameters.AddWithValue("$sync", baseline.LocalObject.SyncRootFileId.ToString("D"));
            capture.Parameters.AddWithValue("$root", (object?)intent.RootId?.Value.ToString("D") ?? DBNull.Value);
            if (await capture.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Permission capture must be sealed before changing this root or its sync-root parent.");
        }
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT t.phase,m.payload,m.fingerprint,m.sequence,m.evidence_id,m.operation_id,
                m.volume_serial,m.sync_root_file_id,m.local_file_id,m.relative_path,m.parent_evidence_id,m.relative_path_key
            FROM namespace_permission_tree_members m
            JOIN namespace_permission_trees t ON t.manifest_id=m.manifest_id
            WHERE m.operation_id=$id OR (m.volume_serial=$volume AND m.sync_root_file_id=$sync AND m.local_file_id=$file);
            """;
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        query.Parameters.AddWithValue("$volume", baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
        query.Parameters.AddWithValue("$sync", baseline.LocalObject.SyncRootFileId.ToString("D"));
        query.Parameters.AddWithValue("$file", baseline.LocalObject.LocalFileId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            string payload = reader.GetString(1);
            var member = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionTreeMember>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original permission tree member is missing.");
            ValidatePermissionTreePreparation(member.Preparation);
            MirrorPulseLocalFileBinding binding = member.Preparation.Baseline.LocalObject;
            if (!SHA256.HashData(Encoding.UTF8.GetBytes(payload)).AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(2)) ||
                member.Sequence != reader.GetInt32(3) || member.Preparation.Baseline.EvidenceId.ToString("D") != reader.GetString(4) ||
                member.Preparation.Intent.OperationId.ToString("D") != reader.GetString(5) ||
                binding.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(6) ||
                binding.SyncRootFileId.ToString("D") != reader.GetString(7) || binding.LocalFileId.ToString("D") != reader.GetString(8) ||
                member.Preparation.Intent.RelativePath != reader.GetString(9) || member.ParentEvidenceId?.ToString("D") != (reader.IsDBNull(10) ? null : reader.GetString(10)) ||
                !string.Equals(member.Preparation.Intent.RelativePath.ToUpperInvariant(), reader.GetString(11), StringComparison.Ordinal))
                throw new InvalidDataException("The original permission tree member's indexes or fingerprint changed.");
            if (reader.GetInt32(0) != (int)MirrorPulseNamespacePermissionTreePhase.Sealed ||
                member.Preparation.Intent.OperationId == intent.OperationId && member.Preparation != new MirrorPulseNamespacePermissionPreparation(baseline, intent))
                throw new InvalidOperationException("Original permission changes require a sealed capture of this object.");
        }
    }

    private static void ValidatePermissionTreeDefinition(MirrorPulseNamespacePermissionTreeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidatePermissionTreePreparation(definition.Anchor);
        if (definition.ManifestId == Guid.Empty || definition.CreatedAt == default || definition.ExpectedMembers is < 1 or > PermissionTreeMemberLimit ||
            !definition.Anchor.Baseline.IsDirectory || definition.Anchor.Intent.RootId is null ||
            definition.Anchor.Intent.RelativePath.Contains('/') || definition.Anchor.Intent.PreparedAt < definition.CreatedAt)
            throw new ArgumentException("The permission capture requires one existing first-level root and a bounded member count.");
    }

    private static void ValidatePermissionTreeMember(MirrorPulseNamespacePermissionTreeDefinition definition, MirrorPulseNamespacePermissionTreeMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        ValidatePermissionTreePreparation(member.Preparation);
        MirrorPulseNamespacePermissionPreparation anchor = definition.Anchor;
        MirrorPulseNamespacePermissionPreparation preparation = member.Preparation;
        if (member.Sequence < 0 || member.Sequence >= definition.ExpectedMembers ||
            preparation.Intent.RootId != anchor.Intent.RootId || preparation.Baseline.OwnerSid != anchor.Baseline.OwnerSid ||
            preparation.Baseline.LocalObject.VolumeSerialNumber != anchor.Baseline.LocalObject.VolumeSerialNumber ||
            preparation.Baseline.LocalObject.SyncRootFileId != anchor.Baseline.LocalObject.SyncRootFileId ||
            preparation.Intent.RoleSid != anchor.Intent.RoleSid || preparation.Intent.Kind != anchor.Intent.Kind ||
            preparation.Intent.PreparedAt < definition.CreatedAt ||
            (member.Sequence == 0 ? member.ParentEvidenceId is not null || preparation != anchor :
                member.ParentEvidenceId is null || member.ParentEvidenceId == Guid.Empty || member.ParentEvidenceId == preparation.Baseline.EvidenceId ||
                !preparation.Intent.RelativePath.StartsWith(anchor.Intent.RelativePath + "/", StringComparison.Ordinal)))
            throw new ArgumentException("The permission member does not belong to this original tree capture.");
    }

    private static void ValidatePermissionTreePreparation(MirrorPulseNamespacePermissionPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(preparation.Baseline);
        ValidatePermissionBaseline(preparation.Baseline);
        ValidatePermissionIntent(preparation.Intent);
        if (preparation.Baseline.EvidenceId != preparation.Intent.EvidenceId || preparation.Baseline.LocalObject != preparation.Intent.LocalObject ||
            preparation.Intent.PreparedAt < preparation.Baseline.CapturedAt)
            throw new ArgumentException("The permission capture must retain the original object and intent.");
    }

    private static void AppendPermissionTreeFingerprint(IncrementalHash hash, string payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
