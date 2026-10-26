using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseRootLabelCancellation?> ReadRootLabelCancellationRowAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,root_id,payload,fingerprint FROM root_label_unstarted_cancellations WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceMovePayload<MirrorPulseRootLabelCancellation>((byte[])reader[2], (byte[])reader[3],
            RootLabelCancellationPayloadLimit, ValidateRootLabelCancellation);
        if (value.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") || reader.GetString(1) != value.RootId.ToString())
            throw new InvalidDataException("The cancellation index disagrees with its original protected Label owner.");
        return value;
    }

    private async Task ValidateRootLabelCancellationReferencesAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseRootLabelCancellation cancellation, bool current, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.Version != 3 || intent.RemoteEntry is not null || cancellation.OperationId != intent.OperationId || cancellation.RootId != intent.RootId ||
            cancellation.PublicNotMovedAt < intent.PreparedAt || cancellation.AnchorMemberId != intent.Source.ParentMemberId ||
            intent.Source.ParentMemberId != intent.Target.ParentMemberId || cancellation.Anchor.LocalObject != intent.Source.ParentLocalObject ||
            cancellation.Anchor.OwnerSid != intent.OwnerSid)
            throw new InvalidOperationException("Cancellation requires its original local protected Label, physical anchor and owner.");
        var history = await ReadManagedRootRenameHistoryCoreAsync(intent.OperationId, token, transaction).ConfigureAwait(false);
        if (history.Proof is not { DirectoryMoveEvidence: not null } proof || history.Observation is not null ||
            proof.ItemId != intent.ItemId || proof.LocalObject != intent.LocalObject || proof.DirectoryMoveEvidence != intent.DirectoryMoveEvidence ||
            history.Intent.RootId != intent.RootId || history.Intent.SourceName != intent.Source.RelativePath || history.Intent.TargetName != intent.Target.RelativePath ||
            (current ? history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared || history.Intent.CancellationOperationId is not null :
                history.Intent.Phase != MirrorPulseRootRenamePhase.Cancelled || history.Intent.CancellationOperationId != intent.OperationId ||
                    history.Intent.UpdatedAt != cancellation.VerifiedAt))
            throw new InvalidOperationException("The original Label proof and unstarted cancellation must retain one immutable owner.");
        await using (var check = _connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT 1 FROM namespace_member_move_starts WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_observations WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_projections WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_directory_move_completions WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM namespace_remote_move_rejections WHERE operation_id=$operation
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            if (await check.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A first start, native/projection fact or another terminal outcome prevents unstarted cancellation.");
        }
        var snapshot = await ReadNamespaceDirectoryMoveSnapshotCoreAsync(intent.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original cancellation lost its complete sealed subtree.");
        if (snapshot.PublicEvidenceSha256 != cancellation.PublicEvidenceSha256 || snapshot.Entries.Count != cancellation.Members.Count)
            throw new InvalidOperationException("Cancellation must retain the exact original SDK proof and every sealed member.");
        var anchor = await ReadNamespaceMemberCoreAsync(cancellation.AnchorMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation lost its registered physical sync-root anchor.");
        if (anchor.RootId is not null || anchor.ParentMemberId is not null || !anchor.IsDirectory ||
            anchor.LocalObject != intent.Source.ParentLocalObject || anchor.OwnerSid != intent.OwnerSid)
            throw new InvalidOperationException("The cancellation anchor must retain the same original registered physical object.");
        await RequireRootCancellationProtectionAsync(anchor, cancellation.AnchorProtectionId, cancellation.Anchor, cancellation, current, transaction, token).ConfigureAwait(false);
        var observations = cancellation.Members.ToDictionary(member => member.MemberId);
        foreach (var entry in snapshot.Entries)
        {
            if (!observations.TryGetValue(entry.MemberId, out var observed))
                throw new InvalidOperationException("Cancellation cannot omit or substitute an original sealed member.");
            var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Cancellation lost an original registered source member.");
            if (member.LocalObject != entry.LocalObject || member.OwnerSid != entry.OwnerSid || member.RootId != intent.RootId ||
                member.IsDirectory != entry.IsDirectory || observed.Native.LocalObject != entry.LocalObject || observed.Native.OwnerSid != entry.OwnerSid)
                throw new InvalidOperationException("The cancellation must observe every original binding, owner and member kind.");
            await RequireRootCancellationProtectionAsync(member, observed.ProtectionId, observed.Native, cancellation, current, transaction, token).ConfigureAwait(false);
            if (current)
            {
                var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
                var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
                if (location.ParentMemberId != entry.ParentMemberId || location.RelativePath != entry.RelativePath ||
                    location.CompletedMoveOperationId != entry.PreviousMoveOperationId || location.PositionOperationId != entry.PreviousPositionOperationId || identity != entry.Identity)
                    throw new InvalidOperationException("The complete source placement and public identities must remain unchanged until cancellation commits.");
            }
        }
        if (current)
        {
            await using var topology = _connection.CreateCommand(); topology.Transaction = transaction;
            topology.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
            string? payload = (string?)await topology.ExecuteScalarAsync(token).ConfigureAwait(false);
            var root = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(root => root.RootId == intent.RootId);
            if (root is null || root.DirectoryName != intent.Source.RelativePath || root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
                throw new InvalidOperationException("Cancellation cannot reinterpret a changed original root mapping or lifecycle.");
        }
    }

    private async Task RequireRootCancellationProtectionAsync(MirrorPulseNamespaceMemberRegistration member, Guid protectionId,
        MirrorPulseNamespacePermissionVerification native, MirrorPulseRootLabelCancellation cancellation, bool current,
        SqliteTransaction transaction, CancellationToken token)
    {
        var proof = await ReadNamespaceMemberProofAsync(member, current, transaction, token, protectionId, cancellation.VerifiedAt).ConfigureAwait(false);
        var verification = await ReadNamespaceMoveRetainedVerificationAsync(member, protectionId, transaction, token).ConfigureAwait(false);
        if (proof.ProtectionId != protectionId || proof.VerifiedAt > cancellation.PublicNotMovedAt || native.LocalObject != member.LocalObject ||
            native.OwnerSid != member.OwnerSid || native.Dacl != verification.Dacl)
            throw new InvalidOperationException("Cancellation requires final guarded native facts matching each exact current or retained protection epoch.");
    }
}
