using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespaceMemberOrigin { CapturedOriginal, ProtectedBirth }

/// <summary>Immutable membership provenance and its first registered location.</summary>
/// <remarks>
/// ProtectionId selects an original permission operation or a born protection epoch.
/// Registration is neither a fresh native check nor authorization to mutate an object.
/// A later relocation must retain this provenance and record its own verified location.
/// Born objects never acquire a fabricated pre-protection original descriptor.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberRegistration(int Version, Guid MemberId,
    Guid? ParentMemberId, MirrorPulseNamespaceMemberOrigin Origin, Guid OriginId, Guid ProtectionId,
    RootId? RootId, string RelativePath, MirrorPulseLocalFileBinding LocalObject,
    bool IsDirectory, string OwnerSid, DateTimeOffset RegisteredAt)
{
    /// <summary>The completed position of this stable parent before the child's first birth admission.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FirstParentPositionOperationId { get; init; }
}

/// <summary>A current member in a validated product namespace subtree.</summary>
public sealed record MirrorPulseNamespaceMemberTreeEntry(Guid MemberId, Guid? ParentMemberId,
    string RelativePath, bool IsDirectory, MirrorPulseLocalFileBinding LocalObject, int Depth);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Registers verified provenance and atomically transfers a born object's name reservation.</summary>
    /// <remarks>
    /// The Host must retain its shared namespace actor and root/ancestor guards and independently
    /// inspect the actual child and direct parent first. This method performs no filesystem or
    /// source operation. Exact replay is historical and does not refresh protection or occupancy.
    /// </remarks>
    public async Task<MirrorPulseNamespaceMemberRegistration> RegisterNamespaceMemberAsync(
        MirrorPulseNamespaceMemberRegistration member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ValidateNamespaceMember(member);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceMemberCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != member) throw new InvalidOperationException("Membership provenance is immutable.");
                transaction.Commit();
                return replay;
            }
            MirrorPulseNamespaceBirthIntent? birth = null;
            Guid? allowedTransfer = null;
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            {
                birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original birth admission is missing.");
                if (birth.TransferEntry is { } transfer)
                {
                    var copy = await ReadNamespaceTransferCoreAsync(transfer.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("The original target transfer is missing.");
                    if (copy.TargetBirth?.Birth != birth || member.MemberId != birth.OperationId)
                        throw new InvalidOperationException("A target registration requires its exact original transfer and stable birth member ID.");
                    await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, cancellationToken, admitted: true,
                        targetBirth: copy.TargetBirth).ConfigureAwait(false);
                    allowedTransfer = transfer.OperationId;
                }
            }
            if (member.RootId is { } rootId)
                await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, cancellationToken,
                    allowedTransfer: allowedTransfer).ConfigureAwait(false);
            var proof = await ReadNamespaceMemberProofAsync(member, false, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadNamespaceMemberProofAsync(member, true, transaction, cancellationToken).ConfigureAwait(false);
            if (current.ProtectionId != member.ProtectionId)
                throw new InvalidOperationException("New membership requires the latest protection epoch.");
            if (member.ParentMemberId is { } parentId)
            {
                var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The direct parent is not a registered member.");
                await RequireNamespaceMemberRelationshipAsync(member, parent, transaction, cancellationToken).ConfigureAwait(false);
                var lineage = await ReadNamespaceMemberProtectionLineageCoreAsync(parentId, transaction, cancellationToken,
                    requireUnfenced: false).ConfigureAwait(false);
                int separator = member.RelativePath.LastIndexOf('/');
                string parentPath = separator < 0 ? string.Empty : member.RelativePath[..separator];
                if (lineage[0].Location.RelativePath != parentPath ||
                    lineage[0].Location.CurrentPositionOperationId != member.FirstParentPositionOperationId)
                    throw new InvalidOperationException("New membership requires its direct parent's exact latest completed position.");
                foreach (var context in lineage)
                    if (context.Protection.RoleSid != proof.RoleSid || member.RegisteredAt < context.Protection.VerifiedAt ||
                        context.PositionVerification is { } position && member.RegisteredAt < position.ObservedAt)
                        throw new InvalidOperationException("Membership requires current protection throughout its actual parent lineage.");
            }
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            {
                birth ??= await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original birth admission is missing.");
                await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
                var parent = await ReadNamespaceMemberRowAsync(member.ParentMemberId!.Value, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The born object's direct member parent is missing.");
                if (parent.Origin != (birth.BornParent is null ? MirrorPulseNamespaceMemberOrigin.CapturedOriginal : MirrorPulseNamespaceMemberOrigin.ProtectedBirth) ||
                    parent.OriginId != NamespaceBirthParentId(birth) || parent.LocalObject != birth.ParentLocalObject)
                    throw new InvalidOperationException("Membership cannot substitute another stable direct birth parent.");
            }
            await RequireNamespaceMemberPendingNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(member, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("Membership provenance must be bounded.", nameof(member));
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO namespace_members(member_id,origin_kind,original_evidence_id,birth_operation_id,
                        permission_operation_id,birth_protection_id,parent_member_id,volume_serial,sync_root_file_id,
                        local_file_id,payload,fingerprint)
                    VALUES($id,$kind,$original,$birth,$permission,$protection,$parent,$volume,$sync,$file,$payload,$fingerprint);
                    """;
                AddNamespaceMemberParameters(insert, member);
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (member.ParentMemberId is not null)
            {
                await using SqliteCommand insertName = _connection.CreateCommand();
                insertName.Transaction = transaction;
                insertName.CommandText = """
                    INSERT INTO namespace_member_names(member_id,parent_member_id,child_name_key) VALUES($id,$parent,$name);
                    """;
                insertName.Parameters.AddWithValue("$id", member.MemberId.ToString("D"));
                insertName.Parameters.AddWithValue("$parent", member.ParentMemberId.Value.ToString("D"));
                insertName.Parameters.AddWithValue("$name", NamespaceMemberNameKey(member.RelativePath));
                await insertName.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (birth is not null)
            {
                await using SqliteCommand release = _connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = "DELETE FROM namespace_birth_reservations WHERE operation_id=$birth;";
                release.Parameters.AddWithValue("$birth", birth.OperationId.ToString("D"));
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("Membership lost its pending birth reservation.");
            }
            transaction.Commit();
            return member;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads immutable registration history, not an object's current native state.</summary>
    public async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return member;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Finds retained membership by the complete native binding, never by a path or a shortened file ID.</summary>
    /// <remarks>This reads historical ownership; it does not prove that the object still exists or occupies a name.</remarks>
    public async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberByBindingAsync(
        MirrorPulseLocalFileBinding binding, CancellationToken cancellationToken = default)
    {
        ValidatePermissionBinding(binding);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            await using SqliteCommand query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT member_id FROM namespace_members
                WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;
                """;
            AddNamespaceBirthBindingParameters(query, binding);
            object? value = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null) { transaction.Commit(); return null; }
            var member = await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(value), transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The native binding index lost its member provenance.");
            if (member.LocalObject != binding)
                throw new InvalidDataException("The native binding index disagrees with retained member provenance.");
            transaction.Commit();
            return member;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads one case-insensitive occupied direct child name without enumerating history.</summary>
    public async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberChildAsync(Guid parentMemberId,
        string childName, CancellationToken cancellationToken = default)
    {
        if (parentMemberId == Guid.Empty) throw new ArgumentException("A direct parent member is required.", nameof(parentMemberId));
        ValidatePermissionRelativePath(childName);
        if (string.IsNullOrEmpty(childName) || childName.Contains('/'))
            throw new ArgumentException("One direct child name is required.", nameof(childName));
        string nameKey = childName.ToUpperInvariant();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            await using SqliteCommand query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id FROM namespace_member_names WHERE parent_member_id=$parent AND child_name_key=$name;";
            query.Parameters.AddWithValue("$parent", parentMemberId.ToString("D"));
            query.Parameters.AddWithValue("$name", nameKey);
            object? value = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null) { transaction.Commit(); return null; }
            Guid id = ParseNamespaceMemberId(value);
            var member = await ReadNamespaceMemberCoreAsync(id, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The occupied child lost its member provenance.");
            var currentParent = await ReadNamespaceMemberRowAsync(parentMemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (currentParent is null || !currentParent.IsDirectory ||
                (currentParent.RootId is not null && currentParent.RootId != member.RootId) ||
                !await IsCurrentNamespaceMemberNameAsync(member, parentMemberId, nameKey,
                    transaction, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The occupied child index is inconsistent with its current managed parent.");
            transaction.Commit();
            return member;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the current registered subtree without native access or membership mutation.</summary>
    public async Task<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var entries = await ReadNamespaceMemberTreeCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return entries;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeCoreAsync(Guid memberId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var root = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The namespace member is not registered.");
        var children = new Dictionary<Guid, List<Guid>>();
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id,parent_member_id FROM namespace_member_names ORDER BY parent_member_id,member_id;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid child = ParseNamespaceMemberId(reader.GetString(0));
                Guid parent = ParseNamespaceMemberId(reader.GetString(1));
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(child);
            }
        }
        var entries = new List<MirrorPulseNamespaceMemberTreeEntry>();
        var visited = new HashSet<Guid>();
        var queue = new Queue<(MirrorPulseNamespaceMemberRegistration Member, int Depth, Guid? ExpectedParent)>();
        queue.Enqueue((root, 0, null));
        while (queue.Count != 0)
        {
            var (current, depth, expectedParent) = queue.Dequeue();
            if (!visited.Add(current.MemberId)) throw new InvalidDataException("The current namespace member tree contains a cycle.");
            var location = await ReadNamespaceMemberLocationCoreAsync(current, transaction, cancellationToken).ConfigureAwait(false);
            if (expectedParent is not null && location.ParentMemberId != expectedParent)
                throw new InvalidDataException("The current namespace member tree does not follow its committed parent locations.");
            await RequireNamespaceMemberNameAsync(current, transaction, cancellationToken).ConfigureAwait(false);
            entries.Add(new(current.MemberId, location.ParentMemberId, location.RelativePath,
                current.IsDirectory, current.LocalObject, depth));
            if (!current.IsDirectory && children.ContainsKey(current.MemberId))
                throw new InvalidDataException("A non-directory member owns registered children.");
            if (children.TryGetValue(current.MemberId, out var childIds))
                foreach (Guid childId in childIds)
                {
                    var child = await ReadNamespaceMemberCoreAsync(childId, transaction, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The current namespace member tree lost a child.");
                    queue.Enqueue((child, depth + 1, current.MemberId));
                }
        }
        return entries.AsReadOnly();
    }

    private async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _namespaceRecoveryReadSnapshots.TryGetValue(transaction, out var snapshot);
        if (snapshot?.Members.TryGetValue(id, out var retained) == true) return retained;
        var first = await ReadNamespaceMemberRowAsync(id, transaction, token).ConfigureAwait(false);
        if (first is null) return null;
        try
        {
            var child = first;
            var visited = new HashSet<Guid>();
            while (true)
            {
                if (!visited.Add(child.MemberId))
                    throw new InvalidDataException("The historical member lineage contains a cycle.");
                _ = await ReadNamespaceMemberProofAsync(child, false, transaction, token).ConfigureAwait(false);
                if (child.ParentMemberId is not { } parentId) break;
                var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The historical parent member is missing.");
                await RequireNamespaceMemberRelationshipAsync(child, parent, transaction, token).ConfigureAwait(false);
                child = parent;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("Membership lost its historical provenance or direct parent.", exception); }
        if (snapshot is not null) snapshot.Members[id] = first;
        return first;
    }

    private async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberForBirthCoreAsync(Guid birthId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_members WHERE birth_operation_id=$birth;";
        query.Parameters.AddWithValue("$birth", birthId.ToString("D"));
        object? value = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (value is null) return null;
        var member = await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(value), transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The born member index lost its provenance.");
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.OriginId != birthId)
            throw new InvalidDataException("The member index refers to another birth.");
        return member;
    }

    private static void ValidateNamespaceMember(MirrorPulseNamespaceMemberRegistration member)
    {
        ValidatePermissionBinding(member.LocalObject);
        ValidatePermissionSid(member.OwnerSid, role: false);
        ValidatePermissionRelativePath(member.RelativePath);
        if (member.Version is not (1 or 2) || member.MemberId == Guid.Empty || member.OriginId == Guid.Empty || member.ProtectionId == Guid.Empty ||
            !Enum.IsDefined(member.Origin) || member.ParentMemberId == Guid.Empty || member.ParentMemberId == member.MemberId ||
            member.RegisteredAt == default || member.RootId?.Value == Guid.Empty)
            throw new ArgumentException("Membership requires bounded original provenance and a distinct direct parent.", nameof(member));
        if (member.Version == 1 && member.FirstParentPositionOperationId is not null ||
            member.Version == 2 && (member.FirstParentPositionOperationId is null || member.FirstParentPositionOperationId == Guid.Empty))
            throw new ArgumentException("Located first-parent provenance requires a separate version and completed position.", nameof(member));
        if (member.Version == 2 && (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.ParentMemberId is null))
            throw new ArgumentException("Only a born member may retain a separate first-parent position.", nameof(member));
        if (member.ParentMemberId is null
            ? member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || member.RootId is not null ||
                member.RelativePath.Length != 0 || !member.IsDirectory || member.LocalObject.LocalFileId != member.LocalObject.SyncRootFileId
            : member.RootId is null || member.RelativePath.Length == 0 || member.LocalObject.LocalFileId == member.LocalObject.SyncRootFileId)
            throw new ArgumentException("Only the original sync-root anchor may omit a direct member parent.", nameof(member));
    }

    private async Task RequireNamespaceMemberRelationshipAsync(MirrorPulseNamespaceMemberRegistration child,
        MirrorPulseNamespaceMemberRegistration parent, SqliteTransaction transaction, CancellationToken token)
    {
        int separator = child.RelativePath.LastIndexOf('/');
        string path = separator < 0 ? string.Empty : child.RelativePath[..separator];
        string retainedParentPath = parent.RelativePath;
        if (child.FirstParentPositionOperationId is { } positionId)
        {
            var birth = await ReadNamespaceBirthCoreAsync(child.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The first-parent placement lost its original birth admission.");
            var position = await ReadNamespaceMemberPositionEvidenceCoreAsync(parent, positionId, birth.PreparedAt,
                transaction, token).ConfigureAwait(false);
            retainedParentPath = position.RelativePath;
        }
        if (child.ParentMemberId != parent.MemberId || !parent.IsDirectory || retainedParentPath != path ||
            parent.LocalObject.VolumeSerialNumber != child.LocalObject.VolumeSerialNumber ||
            parent.LocalObject.SyncRootFileId != child.LocalObject.SyncRootFileId || parent.OwnerSid != child.OwnerSid ||
            child.RegisteredAt < parent.RegisteredAt ||
            (parent.RootId is null ? separator >= 0 || !child.IsDirectory : child.RootId != parent.RootId))
            throw new InvalidOperationException("Membership requires its exact shorter direct directory parent and scope.");
    }

    private static string NamespaceMemberNameKey(string path) => path[(path.LastIndexOf('/') + 1)..].ToUpperInvariant();

    private static Guid ParseNamespaceMemberId(object value) => value is string text &&
        Guid.TryParseExact(text, "D", out Guid id) && id != Guid.Empty && text == id.ToString("D")
        ? id : throw new InvalidDataException("The member index contains an invalid ID.");
}
