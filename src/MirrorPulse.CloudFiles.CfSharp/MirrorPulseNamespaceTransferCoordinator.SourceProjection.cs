using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    private async Task<MirrorPulseNamespaceTransferSourceProjection> CaptureOriginalSourceProjectionAsync(
        MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceMemberProtectionSnapshot source, CloudFile file, CancellationToken token)
    {
        actor.AssertAccess();
        var candidate = await ReadSourceProjectionAsync(file, source, token).ConfigureAwait(false);
        // InspectAsync uses the official store itself. Never call it while holding a store transaction.
        var native = await file.InspectAsync(token).ConfigureAwait(false); RequireBinding(native, copy.Plan.Source);
        await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await read.Items.GetByItemIdAsync(copy.Plan.Source.Identity.ItemId!.Value, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original live SDK source projection disappeared before retirement.");
        if (row.ItemId != candidate.ItemId || row.RemoteId != candidate.RemoteId || row.RelativePath != candidate.RelativePath ||
            row.RemoteRevision != candidate.RemoteRevision || row.RemoteRevision != copy.Plan.SourceRemoteRevision)
            throw new InvalidDataException("The original SDK generation changed during pre-deletion capture.");
        if (native.IsPlaceholder)
        {
            var identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
            if (identity.ItemId != row.ItemId || identity.RemoteId != row.RemoteId ||
                !MatchesSourceRevision(native, identity, row.RemoteRevision))
                throw new InvalidDataException("The guarded native source disagrees with its original SDK generation.");
        }
        var binding = native.LocalBinding!;
        var projection = new MirrorPulseNamespaceTransferSourceProjection(1, copy.Plan.OperationId, row.ItemId,
            row.RemoteId, row.RelativePath, row.RemoteRevision, row.LocalFileId,
            new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), DateTimeOffset.UtcNow);
        RequireOriginalSourceProjection(projection, row, tombstone: false);
        await read.RollbackAsync(token).ConfigureAwait(false);
        return projection;
    }

    private static bool MatchesSourceRevision(CloudItemSnapshot native, CloudPlaceholderIdentity identity,
        string? originalRevision) =>
        (identity.RemoteRevision ?? string.Empty) == (originalRevision ?? string.Empty) ||
        // Public local preparation retains the acknowledged SDK revision but cannot
        // introduce native remote acceptance. The guarded original fully resident dirty
        // object retains that conditional baseline; deletion independently checks it.
        identity.RemoteRevision is null && !string.IsNullOrEmpty(originalRevision) &&
        native.SynchronizationState == CloudSynchronizationState.NotInSync && native.ContentAvailability == CloudContentAvailability.FullyAvailable;

    internal static void RequireOriginalSourceProjection(MirrorPulseNamespaceTransferSourceProjection original,
        CloudItemState? current, bool tombstone, CloudItemKind kind = CloudItemKind.File)
    {
        if (current is null || current.ItemId != original.ItemId || current.RemoteId != original.RemoteId ||
            current.RelativePath != original.RelativePath || current.Kind != kind ||
            current.RemoteRevision != original.RemoteRevision || current.LocalFileId != original.OfficialLocalFileId ||
            current.IsTombstone != tombstone)
            throw new InvalidDataException("The public SDK source no longer matches its retained original generation.");
    }
}
