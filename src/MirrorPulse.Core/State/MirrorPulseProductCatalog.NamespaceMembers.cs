using System.Security.Cryptography;
using System.Text.Json;
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
    bool IsDirectory, string OwnerSid, DateTimeOffset RegisteredAt);

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
            if (member.RootId is { } rootId)
                await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, cancellationToken).ConfigureAwait(false);
            var proof = await ReadNamespaceMemberProofAsync(member, false, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadNamespaceMemberProofAsync(member, true, transaction, cancellationToken).ConfigureAwait(false);
            if (current.ProtectionId != member.ProtectionId)
                throw new InvalidOperationException("New membership requires the latest protection epoch.");
            var child = member;
            while (child.ParentMemberId is { } parentId)
            {
                var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The direct parent is not a registered member.");
                RequireNamespaceMemberRelationship(child, parent);
                _ = await ReadNamespaceMemberProofAsync(parent, false, transaction, cancellationToken).ConfigureAwait(false);
                var parentProof = await ReadNamespaceMemberProofAsync(parent, true, transaction, cancellationToken).ConfigureAwait(false);
                if (parentProof.RoleSid != proof.RoleSid || member.RegisteredAt < parentProof.VerifiedAt)
                    throw new InvalidOperationException("Membership requires current protection throughout its parent lineage.");
                await RequireNamespaceMemberNameAsync(parent, transaction, cancellationToken).ConfigureAwait(false);
                child = parent;
            }
            MirrorPulseNamespaceBirthIntent? birth = null;
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            {
                birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
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
            if (member.ParentMemberId != parentMemberId || NamespaceMemberNameKey(member.RelativePath) != nameKey)
                throw new InvalidDataException("The occupied child index is inconsistent with its registered location.");
            transaction.Commit();
            return member;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        var first = await ReadNamespaceMemberRowAsync(id, transaction, token).ConfigureAwait(false);
        if (first is null) return null;
        try
        {
            var child = first;
            while (true)
            {
                _ = await ReadNamespaceMemberProofAsync(child, false, transaction, token).ConfigureAwait(false);
                if (child.ParentMemberId is not { } parentId) break;
                var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The historical parent member is missing.");
                RequireNamespaceMemberRelationship(child, parent);
                child = parent;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("Membership lost its historical provenance or direct parent.", exception); }
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
        if (member.Version != 1 || member.MemberId == Guid.Empty || member.OriginId == Guid.Empty || member.ProtectionId == Guid.Empty ||
            !Enum.IsDefined(member.Origin) || member.ParentMemberId == Guid.Empty || member.ParentMemberId == member.MemberId ||
            member.RegisteredAt == default || member.RootId?.Value == Guid.Empty)
            throw new ArgumentException("Membership requires bounded original provenance and a distinct direct parent.", nameof(member));
        if (member.ParentMemberId is null
            ? member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || member.RootId is not null ||
                member.RelativePath.Length != 0 || !member.IsDirectory || member.LocalObject.LocalFileId != member.LocalObject.SyncRootFileId
            : member.RootId is null || member.RelativePath.Length == 0 || member.LocalObject.LocalFileId == member.LocalObject.SyncRootFileId)
            throw new ArgumentException("Only the original sync-root anchor may omit a direct member parent.", nameof(member));
    }

    private static void RequireNamespaceMemberRelationship(MirrorPulseNamespaceMemberRegistration child,
        MirrorPulseNamespaceMemberRegistration parent)
    {
        int separator = child.RelativePath.LastIndexOf('/');
        string path = separator < 0 ? string.Empty : child.RelativePath[..separator];
        if (child.ParentMemberId != parent.MemberId || !parent.IsDirectory || parent.RelativePath != path ||
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
