using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateUnsealedRootLabelCancellation(MirrorPulseRootLabelUnsealedCancellation value)
    {
        ArgumentNullException.ThrowIfNull(value.Observation); ArgumentNullException.ThrowIfNull(value.Inventory);
        ValidateRootLabelCancellation(value.Observation);
        var observation = value.Observation; var inventory = value.Inventory;
        if (value.Version != 1 || value.RootMemberId == Guid.Empty || inventory.Version != 1 ||
            inventory.OperationId != observation.OperationId || inventory.PublicEvidenceSha256 != observation.PublicEvidenceSha256 ||
            inventory.Entries is null || inventory.Entries.Count != observation.Members.Count)
            throw new ArgumentException("Unsealed cancellation requires its complete original public preparation and guarded inventory.");
        var members = new Dictionary<Guid, MirrorPulseNamespaceDirectoryMoveEntry>();
        var native = observation.Members.ToDictionary(member => member.MemberId);
        foreach (var entry in inventory.Entries)
        {
            if (entry is null || entry.MemberId == Guid.Empty || entry.ParentMemberId is null || entry.ParentMemberId == Guid.Empty ||
                entry.ParentMemberId == entry.MemberId || entry.ProtectionId == Guid.Empty || entry.ProtectionVerifiedAt == default ||
                entry.ProtectionVerifiedAt > observation.PublicNotMovedAt || entry.PreviousMoveOperationId == Guid.Empty ||
                entry.PreviousMoveOperationId == observation.OperationId || entry.PreviousPositionOperationId == Guid.Empty ||
                entry.PreviousPositionOperationId == observation.OperationId || entry.Identity is null || entry.Identity.MemberId != entry.MemberId ||
                entry.Identity.ItemId == Guid.Empty || entry.Identity.OriginalItemId == Guid.Empty || entry.Identity.AcceptanceOperationId == Guid.Empty ||
                entry.OwnerSid != observation.Anchor.OwnerSid || entry.Depth < 0 || !members.TryAdd(entry.MemberId, entry) ||
                !native.TryGetValue(entry.MemberId, out var facts) || facts.ProtectionId != entry.ProtectionId ||
                facts.Native.LocalObject != entry.LocalObject || facts.Native.OwnerSid != entry.OwnerSid)
                throw new ArgumentException("Unsealed cancellation requires distinct complete member, identity, parent and current protection facts.");
            ValidatePermissionRelativePath(entry.RelativePath); ValidatePermissionBinding(entry.LocalObject);
            if (entry.LocalObject.VolumeSerialNumber != observation.Anchor.LocalObject.VolumeSerialNumber ||
                entry.LocalObject.SyncRootFileId != observation.Anchor.LocalObject.SyncRootFileId)
                throw new ArgumentException("The complete cancellation inventory must remain in the original physical sync root.");
        }
        if (!members.TryGetValue(value.RootMemberId, out var root) || !root.IsDirectory || root.Depth != 0 ||
            root.ParentMemberId != observation.AnchorMemberId || members.ContainsKey(observation.AnchorMemberId) ||
            root.LocalObject.LocalFileId == observation.Anchor.LocalObject.LocalFileId)
            throw new ArgumentException("The unsealed cancellation inventory must begin at its original first-level root.");
        foreach (var entry in inventory.Entries)
        {
            if (entry.MemberId == value.RootMemberId) continue;
            if (entry.Depth == 0 || !members.TryGetValue(entry.ParentMemberId!.Value, out var parent) || !parent.IsDirectory ||
                entry.Depth != parent.Depth + 1 || !entry.RelativePath.StartsWith(root.RelativePath + "/", StringComparison.Ordinal) ||
                entry.RelativePath.LastIndexOf('/') != parent.RelativePath.Length ||
                !entry.RelativePath.StartsWith(parent.RelativePath + "/", StringComparison.Ordinal))
                throw new ArgumentException("The complete cancellation inventory must retain every exact direct parent and root-relative position.");
        }
    }

    private async Task ValidateUnsealedRootLabelCancellationReferencesAsync(MirrorPulseRootLabelUnsealedCancellation cancellation,
        bool current, SqliteTransaction transaction, CancellationToken token)
    {
        var observation = cancellation.Observation;
        var history = await ReadManagedRootRenameHistoryCoreAsync(observation.OperationId, token, transaction).ConfigureAwait(false);
        var rootEntry = cancellation.Inventory.Entries.Single(entry => entry.MemberId == cancellation.RootMemberId);
        if (history.Proof is not { DirectoryMoveEvidence: { } evidence } proof || history.Observation is not null ||
            history.Intent.RootId != observation.RootId || proof.CapturedAt > observation.PublicNotMovedAt ||
            history.Intent.SourceName != rootEntry.RelativePath || proof.LocalObject != rootEntry.LocalObject ||
            rootEntry.Identity.ItemId is { } rootItemId && proof.ItemId != rootItemId ||
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(evidence))) != observation.PublicEvidenceSha256 ||
            history.Intent.CancellationOperationId is not null ||
            (current ? history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared || history.Intent.UnsealedCancellationOperationId is not null :
                history.Intent.Phase != MirrorPulseRootRenamePhase.Cancelled || history.Intent.UnsealedCancellationOperationId != observation.OperationId ||
                    history.Intent.UpdatedAt != observation.VerifiedAt))
            throw new InvalidOperationException("Unsealed cancellation must retain its original public Label, exact source and immutable terminal owner.");
        await RequireNoUnsealedCancellationMoveOwnershipAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
        var anchor = await ReadNamespaceMemberCoreAsync(observation.AnchorMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Unsealed cancellation lost its registered physical anchor.");
        if (anchor.ParentMemberId is not null || anchor.RootId is not null || !anchor.IsDirectory || anchor.RelativePath.Length != 0 ||
            anchor.LocalObject != observation.Anchor.LocalObject || anchor.LocalObject.LocalFileId != rootEntry.LocalObject.SyncRootFileId ||
            anchor.OwnerSid != rootEntry.OwnerSid || anchor.RegisteredAt > observation.PublicNotMovedAt)
            throw new InvalidOperationException("Unsealed cancellation cannot replace its original physical sync-root anchor.");
        await RequireRootCancellationProtectionAsync(anchor, observation.AnchorProtectionId, observation.Anchor, observation,
            current, transaction, token).ConfigureAwait(false);
        var natives = observation.Members.ToDictionary(member => member.MemberId);
        foreach (var entry in cancellation.Inventory.Entries)
        {
            var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Unsealed cancellation lost an original guarded member.");
            if (member.RootId != observation.RootId || member.LocalObject != entry.LocalObject || member.OwnerSid != entry.OwnerSid ||
                member.IsDirectory != entry.IsDirectory || member.RegisteredAt > observation.PublicNotMovedAt)
                throw new InvalidOperationException("Unsealed cancellation cannot adopt different registered member provenance.");
            await RequireNamespaceDirectoryMoveEntryHistoryAsync(entry, member, observation.RootId, observation.PublicNotMovedAt, transaction, token).ConfigureAwait(false);
            await RequireRootCancellationProtectionAsync(member, entry.ProtectionId, natives[entry.MemberId].Native, observation,
                current, transaction, token).ConfigureAwait(false);
            if (current)
            {
                var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
                var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
                var protection = await ReadNamespaceMemberProofAsync(member, true, transaction, token, entry.ProtectionId, observation.VerifiedAt).ConfigureAwait(false);
                if (location.ParentMemberId != entry.ParentMemberId || location.RelativePath != entry.RelativePath ||
                    location.CompletedMoveOperationId != entry.PreviousMoveOperationId || location.PositionOperationId != entry.PreviousPositionOperationId ||
                    identity != entry.Identity || protection.VerifiedAt != entry.ProtectionVerifiedAt)
                    throw new InvalidOperationException("Unsealed cancellation requires the same complete current position, identity and protection inventory.");
            }
        }
        if (!current) return;
        await RequireNamespaceMoveRootUnfencedAsync(observation.RootId, null, transaction, token,
            allowedRootRename: observation.OperationId).ConfigureAwait(false);
        var tree = await ReadNamespaceMemberTreeCoreAsync(cancellation.RootMemberId, transaction, token).ConfigureAwait(false);
        if (tree.Count != cancellation.Inventory.Entries.Count || !tree.Select(entry => entry.MemberId).ToHashSet().SetEquals(natives.Keys))
            throw new InvalidOperationException("Unsealed cancellation cannot omit or invent any current member.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        string? payload = (string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var registration = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(root => root.RootId == observation.RootId);
        if (registration is null || registration.DirectoryName != history.Intent.SourceName ||
            registration.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("Unsealed cancellation cannot reinterpret a changed root mapping or lifecycle.");
        query.Parameters.Clear(); query.CommandText = "SELECT payload,root_id FROM managed_root_renames WHERE root_id=$root;";
        query.Parameters.AddWithValue("$root", observation.RootId.ToString());
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)) != history.Intent)
            throw new InvalidOperationException("Only the exact current original Label owner can commit unsealed cancellation.");
    }

    private async Task RequireNoUnsealedCancellationMoveOwnershipAsync(Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT 1 FROM namespace_member_moves WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_starts WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_observations WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_projections WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_directory_move_snapshots WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_directory_move_completions WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_pending WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_reservations WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_predecessor_claims WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM root_label_unstarted_cancellations WHERE operation_id=$operation LIMIT 1;
            """;
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An unsealed cancellation cannot replace any sealed move ownership, progress or terminal receipt.");
    }
}
