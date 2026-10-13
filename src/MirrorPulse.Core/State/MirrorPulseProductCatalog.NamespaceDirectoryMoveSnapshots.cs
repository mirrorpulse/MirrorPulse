using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>One current product member sealed before a directory relocation.</summary>
public sealed record MirrorPulseNamespaceDirectoryMoveEntry(Guid MemberId, Guid? ParentMemberId,
    string RelativePath, bool IsDirectory, MirrorPulseLocalFileBinding LocalObject, string OwnerSid,
    Guid ProtectionId, DateTimeOffset ProtectionVerifiedAt, Guid? PreviousMoveOperationId,
    MirrorPulseNamespaceMemberIdentity Identity, int Depth);

/// <summary>An immutable product admission snapshot associated with opaque public CfSharp evidence.</summary>
/// <remarks>
/// This is neither a second Cloud Files item graph nor native movement evidence. Original
/// membership and permissions remain unchanged. The Host must decode the public evidence,
/// inspect every actual object, and settle all descendant positions before releasing its fence.
/// </remarks>
public sealed record MirrorPulseNamespaceDirectoryMoveSnapshot(int Version, Guid OperationId,
    string PublicEvidenceSha256, IReadOnlyList<MirrorPulseNamespaceDirectoryMoveEntry> Entries);

public sealed partial class MirrorPulseProductCatalog
{
    private const int NamespaceDirectoryMoveSnapshotLimit = 4 * 1024 * 1024;

    /// <summary>Reads retained admission facts without rebuilding a missing legacy snapshot.</summary>
    public async Task<MirrorPulseNamespaceDirectoryMoveSnapshot?> ReadNamespaceDirectoryMoveSnapshotAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A directory move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceDirectoryMoveSnapshotCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task SealNamespaceDirectoryMoveCoreAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        var tree = await ReadNamespaceMemberTreeCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false);
        if (tree.Count > 65_536) throw new InvalidOperationException("The directory move subtree is too large.");
        var entries = new List<MirrorPulseNamespaceDirectoryMoveEntry>(tree.Count);
        foreach (var item in tree)
        {
            token.ThrowIfCancellationRequested();
            var member = await ReadNamespaceMemberCoreAsync(item.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory move subtree lost a registered member.");
            var proof = await ReadNamespaceMemberProofAsync(member, true, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (member.RootId != intent.RootId || member.OwnerSid != intent.OwnerSid || proof.RoleSid != intent.RoleSid ||
                proof.VerifiedAt > intent.PreparedAt || member.RegisteredAt > intent.PreparedAt ||
                location.RelativePath != item.RelativePath || location.ParentMemberId != item.ParentMemberId)
                throw new InvalidOperationException("Every sealed descendant requires the same root, owner and current protection role before admission.");
            if (member.IsDirectory)
            {
                await using SqliteCommand pending = _connection.CreateCommand();
                pending.Transaction = transaction;
                pending.CommandText = "SELECT 1 FROM namespace_birth_reservations WHERE parent_kind=$kind AND parent_id=$parent LIMIT 1;";
                pending.Parameters.AddWithValue("$kind", (int)member.Origin);
                pending.Parameters.AddWithValue("$parent", member.OriginId.ToString("D"));
                if (await pending.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("An unresolved descendant birth must settle before directory move admission.");
            }
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
            var entry = new MirrorPulseNamespaceDirectoryMoveEntry(member.MemberId, item.ParentMemberId, item.RelativePath, member.IsDirectory,
                member.LocalObject, member.OwnerSid, proof.ProtectionId, proof.VerifiedAt,
                location.CompletedMoveOperationId, identity, item.Depth);
            await RequireNamespaceDirectoryMoveEntryHistoryAsync(entry, member, intent, transaction, token).ConfigureAwait(false);
            entries.Add(entry);
        }
        var snapshot = new MirrorPulseNamespaceDirectoryMoveSnapshot(1, intent.OperationId,
            DirectoryMoveEvidenceFingerprint(intent), entries.AsReadOnly());
        ValidateNamespaceDirectoryMoveSnapshot(snapshot, intent);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, TopologyJsonOptions);
        if (payload.Length > NamespaceDirectoryMoveSnapshotLimit)
            throw new InvalidOperationException("The directory move subtree evidence is too large.");
        await using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO namespace_directory_move_snapshots(operation_id,payload,fingerprint) VALUES($id,$payload,$fingerprint);";
        insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceDirectoryMoveSnapshot?> ReadNamespaceDirectoryMoveSnapshotCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot;
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint FROM namespace_directory_move_snapshots WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            if (intent is null || intent.DirectoryMoveEvidence is null || reader.GetString(0) != operationId.ToString("D"))
                throw new InvalidDataException("The directory move snapshot lost its original operation.");
            snapshot = ReadNamespaceMovePayload<MirrorPulseNamespaceDirectoryMoveSnapshot>((byte[])reader[1], (byte[])reader[2],
                NamespaceDirectoryMoveSnapshotLimit, value => ValidateNamespaceDirectoryMoveSnapshot(value, intent));
        }
        try
        {
            foreach (var entry in snapshot.Entries)
            {
                token.ThrowIfCancellationRequested();
                var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The sealed directory move member is missing.");
                var proof = await ReadNamespaceMemberProofAsync(member, false, transaction, token,
                    entry.ProtectionId, intent!.PreparedAt).ConfigureAwait(false);
                if (member.RootId != intent.RootId || member.OwnerSid != entry.OwnerSid || member.IsDirectory != entry.IsDirectory ||
                    member.LocalObject != entry.LocalObject || proof.RoleSid != intent.RoleSid || proof.VerifiedAt != entry.ProtectionVerifiedAt ||
                    member.RegisteredAt > intent.PreparedAt)
                    throw new InvalidDataException("The sealed directory member lost its original object or protection evidence.");
                await RequireNamespaceDirectoryMoveEntryHistoryAsync(entry, member, intent, transaction, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The directory move snapshot lost its historical references.", error); }
        return snapshot;
    }

    private async Task RequireNamespaceDirectoryMoveEntryHistoryAsync(MirrorPulseNamespaceDirectoryMoveEntry entry,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (entry.PreviousMoveOperationId is { } previousId)
        {
            var previous = await ReadNamespaceMemberMoveCoreAsync(previousId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The sealed member lost its previous relocation.");
            var completion = await ReadNamespaceMemberMoveCompletionCoreAsync(previousId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The sealed member relocation is incomplete.");
            if (previous.MemberId != member.MemberId || previous.Target.ParentMemberId != entry.ParentMemberId ||
                completion.RelativePath != entry.RelativePath || completion.VerifiedAt > intent.PreparedAt)
                throw new InvalidDataException("The sealed member position disagrees with its completed predecessor.");
        }
        else if (member.RelativePath != entry.RelativePath || member.ParentMemberId != entry.ParentMemberId)
            throw new InvalidDataException("The sealed member position has no retained relocation provenance.");
        var original = await ReadOriginalNamespaceMemberIdentityAsync(member, transaction, token).ConfigureAwait(false);
        Guid? itemId = original.ItemId; string? remoteId = original.RemoteId;
        if (entry.Identity.AcceptanceOperationId is { } acceptanceId)
        {
            var accepted = await ReadNamespaceIdentityAcceptanceCoreAsync(acceptanceId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The sealed member lost its accepted identity.");
            if (accepted.MemberId != member.MemberId || accepted.RootId != intent.RootId ||
                accepted.Proof.UploadBinding.LocalObject != member.LocalObject || accepted.Confirmation.ObservedAt > intent.PreparedAt)
                throw new InvalidDataException("The sealed identity must belong to the same object before admission.");
            itemId = accepted.Proof.AcceptedItemId; remoteId = accepted.Proof.RemoteId;
        }
        if (entry.Identity.ItemId != itemId || entry.Identity.RemoteId != remoteId ||
            entry.Identity.OriginalItemId != original.ItemId || entry.Identity.OriginalRemoteId != original.RemoteId)
            throw new InvalidDataException("The sealed public identity disagrees with its retained history.");
    }

    private async Task RequireCurrentNamespaceDirectoryMoveSnapshotAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        var snapshot = await ReadNamespaceDirectoryMoveSnapshotCoreAsync(intent.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original directory move has no retained subtree admission; execution requires recovery.");
        var currentTree = await ReadNamespaceMemberTreeCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false);
        if (currentTree.Count != snapshot.Entries.Count)
            throw new InvalidOperationException("The current directory membership differs from its sealed admission.");
        var currentById = currentTree.ToDictionary(entry => entry.MemberId);
        foreach (var entry in snapshot.Entries)
        {
            if (!currentById.TryGetValue(entry.MemberId, out var current) || current.ParentMemberId != entry.ParentMemberId ||
                current.RelativePath != entry.RelativePath || current.LocalObject != entry.LocalObject ||
                current.IsDirectory != entry.IsDirectory || current.Depth != entry.Depth)
                throw new InvalidOperationException("The current directory member no longer occupies its sealed position.");
            var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The sealed directory member disappeared.");
            var protection = await ReadNamespaceMemberProofAsync(member, true, transaction, token).ConfigureAwait(false);
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (protection.ProtectionId != entry.ProtectionId || protection.VerifiedAt != entry.ProtectionVerifiedAt ||
                protection.RoleSid != intent.RoleSid || identity != entry.Identity)
                throw new InvalidOperationException("Directory execution requires every member's sealed protection and identity epochs.");
        }
    }

    private static void ValidateNamespaceDirectoryMoveSnapshot(MirrorPulseNamespaceDirectoryMoveSnapshot snapshot,
        MirrorPulseNamespaceMemberMoveIntent intent)
    {
        if (snapshot.Version != 1 || snapshot.OperationId != intent.OperationId || snapshot.Entries is null ||
            snapshot.Entries.Count == 0 || snapshot.Entries.Count > 65_536 || intent.DirectoryMoveEvidence is null ||
            snapshot.PublicEvidenceSha256 != DirectoryMoveEvidenceFingerprint(intent))
            throw new ArgumentException("The subtree snapshot must retain its exact original public move evidence.");
        var members = new Dictionary<Guid, MirrorPulseNamespaceDirectoryMoveEntry>();
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Identity is null || entry.MemberId == Guid.Empty || entry.ParentMemberId is null ||
                entry.ParentMemberId == Guid.Empty || entry.ProtectionId == Guid.Empty || entry.ProtectionVerifiedAt == default ||
                entry.ProtectionVerifiedAt > intent.PreparedAt || entry.PreviousMoveOperationId == Guid.Empty ||
                entry.PreviousMoveOperationId == intent.OperationId || entry.Identity.MemberId != entry.MemberId ||
                entry.Identity.AcceptanceOperationId == Guid.Empty || entry.Identity.ItemId == Guid.Empty ||
                entry.Identity.OriginalItemId == Guid.Empty || entry.OwnerSid != intent.OwnerSid || entry.Depth < 0 ||
                !members.TryAdd(entry.MemberId, entry))
                throw new ArgumentException("The subtree snapshot requires unique members and complete retained evidence.");
            ValidatePermissionRelativePath(entry.RelativePath);
            ValidatePermissionBinding(entry.LocalObject);
            if (entry.LocalObject.VolumeSerialNumber != intent.LocalObject.VolumeSerialNumber ||
                entry.LocalObject.SyncRootFileId != intent.LocalObject.SyncRootFileId)
                throw new ArgumentException("Every subtree member must retain the original sync root binding.");
        }
        if (!members.TryGetValue(intent.MemberId, out var root) || !root.IsDirectory || root.Depth != 0 ||
            root.RelativePath != intent.Source.RelativePath || root.ParentMemberId != intent.Source.ParentMemberId ||
            root.LocalObject != intent.LocalObject || root.ProtectionId != intent.ProtectionId ||
            root.PreviousMoveOperationId != intent.PreviousMoveOperationId || members.ContainsKey(root.ParentMemberId!.Value) ||
            members.ContainsKey(intent.Target.ParentMemberId) || root.Identity.AcceptanceOperationId != intent.IdentityAcceptanceOperationId ||
            root.Identity.ItemId is not null && (root.Identity.ItemId != intent.ItemId || root.Identity.RemoteId != intent.RemoteId))
            throw new ArgumentException("The sealed subtree must start at the original source and move outside itself.");
        foreach (var entry in snapshot.Entries)
        {
            if (entry.MemberId == root.MemberId) continue;
            if (entry.Depth == 0 || !members.TryGetValue(entry.ParentMemberId!.Value, out var parent) || !parent.IsDirectory ||
                entry.Depth != parent.Depth + 1 || entry.RelativePath.LastIndexOf('/') <= 0 || ParentMovePath(entry.RelativePath) != parent.RelativePath)
                throw new ArgumentException("Every sealed descendant must follow its direct parent's path and depth.");
        }
    }

    private static string DirectoryMoveEvidenceFingerprint(MirrorPulseNamespaceMemberMoveIntent intent) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(intent.DirectoryMoveEvidence!)));
}
