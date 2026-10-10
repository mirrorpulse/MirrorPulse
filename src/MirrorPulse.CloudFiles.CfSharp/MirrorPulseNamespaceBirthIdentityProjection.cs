using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Publishes the retained logical birth identity before native creation can emit watcher events.</summary>
/// <remarks>
/// The Host holds its namespace admission gate and keeps the birth offline until native observation,
/// current protection and content completion succeed. This row has no native binding or acceptance
/// claim. CfSharp owns the item and journal repositories and their encodings. MP never synthesizes
/// a watcher journal payload. The caller records the native start only after this transaction commits.
/// </remarks>
public static class MirrorPulseNamespaceBirthIdentityProjection
{
    public static async Task<bool> PrepareAsync(MirrorPulseProductCatalog catalog, ICloudStateStore store,
        Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);
        var birth = await catalog.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        var start = await catalog.ReadNamespaceBirthStartAsync(operationId, cancellationToken).ConfigureAwait(false);
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        _ = identity.Encode();
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState? atPath = await transaction.Items.GetByRelativePathAsync(birth.RelativePath, cancellationToken).ConfigureAwait(false);
        CloudItemState? atId = await transaction.Items.GetByItemIdAsync(plan.ItemId, cancellationToken).ConfigureAwait(false);
        CloudItemState? atRemote = await transaction.Items.GetByRemoteIdAsync(plan.RemoteId, cancellationToken).ConfigureAwait(false);
        CloudItemKind kind = birth.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File;
        if (atPath is not null || atId is not null || atRemote is not null)
        {
            if (atPath is null || atId is null || atRemote is null || !Matches(atPath) || !Matches(atId) || !Matches(atRemote))
                throw new InvalidDataException("The planned birth cannot adopt or replace a different official item projection.");
            // Preserve its original timestamp and all forward progress. Replaying this row never
            // authorizes another native creation, including after the immutable start is recorded.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (start is not null)
            throw new InvalidOperationException("A started birth with a missing official projection requires native recovery.");
        await transaction.Items.UpsertAsync(new CloudItemState(plan.ItemId, plan.RemoteId, birth.RelativePath,
            kind, plan.RemoteRevision, localFileId: null, isTombstone: false, plan.PreparedAt), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;

        bool Matches(CloudItemState row) => row.ItemId == plan.ItemId && row.RemoteId == plan.RemoteId &&
            row.RelativePath == birth.RelativePath && row.Kind == kind && row.RemoteRevision == plan.RemoteRevision &&
            !row.IsTombstone && (start is not null || row.LocalFileId is null);
    }
}
