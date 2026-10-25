using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // Version 3 shares the original Label operation ID. There is no general
    // cross-root permission or path-based exception for ordinary member moves.
    private async Task<MirrorPulseRootRenameHistory> RequireManagedRootSubtreeRenameReferenceAsync(
        MirrorPulseNamespaceMemberMoveIntent intent, MirrorPulseNamespaceMemberRegistration anchor, bool current,
        SqliteTransaction transaction, CancellationToken token)
    {
        var history = await ReadManagedRootRenameHistoryCoreAsync(intent.OperationId, token, transaction).ConfigureAwait(false);
        if (history.Proof is not { DirectoryMoveEvidence: not null } proof || history.Intent.RootId != intent.RootId ||
            history.Intent.SourceName != intent.Source.RelativePath || history.Intent.TargetName != intent.Target.RelativePath ||
            proof.ItemId != intent.ItemId || proof.LocalObject != intent.LocalObject || proof.DirectoryMoveEvidence != intent.DirectoryMoveEvidence ||
            proof.CapturedAt > intent.PreparedAt || anchor.ParentMemberId is not null || anchor.RootId is not null ||
            anchor.RelativePath.Length != 0 || anchor.LocalObject.LocalFileId != anchor.LocalObject.SyncRootFileId ||
            anchor.LocalObject.SyncRootFileId != intent.LocalObject.SyncRootFileId ||
            anchor.LocalObject.VolumeSerialNumber != intent.LocalObject.VolumeSerialNumber)
            throw new InvalidOperationException("A root subtree move requires its original Label operation, public proof and sync-root anchor.");
        if (current)
        {
            await using var query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,root_id FROM managed_root_renames WHERE root_id=$root;";
            query.Parameters.AddWithValue("$root", intent.RootId.ToString());
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) ||
                ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)) != history.Intent ||
                history.Intent.Phase is not (MirrorPulseRootRenamePhase.Prepared or MirrorPulseRootRenamePhase.NativeObserved))
                throw new InvalidOperationException("Only this current original Label operation may execute its sealed subtree move.");
            await reader.DisposeAsync().ConfigureAwait(false);
            query.Parameters.Clear();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
            string? payload = (string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            var root = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(root => root.RootId == intent.RootId);
            if (root is null || root.DirectoryName != history.Intent.SourceName ||
                root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
                throw new InvalidOperationException("The root Label must retain its original mapping and lifecycle until atomic subtree settlement.");
        }
        return history;
    }

    private async Task CompleteManagedRootSubtreeRenameCoreAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceDirectoryMoveCompletion completion, SqliteTransaction transaction, CancellationToken token)
    {
        var anchor = await ReadNamespaceMemberCoreAsync(intent.Source.ParentMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root Label settlement lost its sync-root anchor.");
        var history = await RequireManagedRootSubtreeRenameReferenceAsync(intent, anchor, true, transaction, token).ConfigureAwait(false);
        var start = await ReadNamespaceMemberMoveStartCoreAsync(intent.OperationId, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root Label settlement lost its before-native start.");
        if (history.Intent.Phase != MirrorPulseRootRenamePhase.NativeObserved || history.Observation is not { } observation ||
            observation.ObservedAt < start.StartedAt || observation.ObservedAt > completion.PublicResult.ObservedAt ||
            observation.LocalObject != completion.Root.Child.LocalObject || observation.DirectoryName != completion.Root.RelativePath)
            throw new InvalidOperationException("Root Label settlement requires its independently retained original native observation before public reconciliation.");
        // The caller has inserted all descendant positions and transferred the
        // owned name in this same transaction. Any failure here rolls them back.
        await RenameManagedRootCoreAsync(intent.RootId, intent.Target.RelativePath, transaction, token).ConfigureAwait(false);
        await SaveManagedRootRenameCoreAsync(history.Intent with
        {
            Phase = MirrorPulseRootRenamePhase.Completed,
            UpdatedAt = completion.Root.VerifiedAt
        }, token, transaction).ConfigureAwait(false);
    }

    private async Task ValidateManagedRootSubtreeRenameReceiptAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceDirectoryMoveCompletion completion, SqliteTransaction transaction, CancellationToken token)
    {
        var history = await ReadManagedRootRenameHistoryCoreAsync(intent.OperationId, token, transaction).ConfigureAwait(false);
        var start = await ReadNamespaceMemberMoveStartCoreAsync(intent.OperationId, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root Label receipt lost its before-native start.");
        if (history.Intent.Phase != MirrorPulseRootRenamePhase.Completed || history.Intent.UpdatedAt != completion.Root.VerifiedAt ||
            history.Observation is not { } observation || observation.ObservedAt < start.StartedAt ||
            observation.ObservedAt > completion.PublicResult.ObservedAt || observation.LocalObject != completion.Root.Child.LocalObject ||
            observation.DirectoryName != completion.Root.RelativePath)
            throw new InvalidDataException("The root Label and whole-subtree receipts must retain the same original atomic settlement.");
        // A later Label generation may now be current. Historical reads validate
        // original receipts, never require or restore an obsolete live mapping.
    }
}
