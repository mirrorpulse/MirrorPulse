using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Applies explicit upload-conflict decisions while preserving CfSharp's journal authority.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseUploadConflictActions
{
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly CloudLocalChangeFeed _feed;
    private readonly MirrorPulseJournalUploadCompletion _completion;
    private readonly string _syncRootPath;
    private readonly MirrorPulseConflictCopyStore _copies;
    private readonly MirrorPulseRootRouter? _router;
    private readonly IMirrorPulseWorkerMutationTransport? _mutations;

    public MirrorPulseUploadConflictActions(
        MirrorPulseProductCatalog catalog,
        MirrorPulseCfSharpStateSession state,
        CloudLocalChangeFeed feed,
        BackoffPolicy retryPolicy,
        MirrorPulseStoragePaths paths,
        MirrorPulseRootRouter? router = null,
        IMirrorPulseWorkerMutationTransport? mutations = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _completion = new MirrorPulseJournalUploadCompletion(feed, state, retryPolicy);
        _syncRootPath = Path.GetFullPath(paths.SyncRootPath);
        _copies = new MirrorPulseConflictCopyStore(paths);
        _router = router;
        _mutations = mutations;
    }

    public async ValueTask<MirrorPulseConflictResolution> ApplyAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken = default)
    {
        MirrorPulseConflictRecord conflict = await _catalog.ReadUploadConflictAsync(
            conflictId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The upload conflict was not found.");
        if (!conflict.IsPending)
        {
            throw new InvalidOperationException("The upload conflict has already been resolved.");
        }

        string? preservedPath = null;
        if (!Enum.IsDefined(action) || action == MirrorPulseConflictAction.Defer)
            throw new ArgumentOutOfRangeException(nameof(action));
        Guid commandId = Guid.NewGuid();
        string actionName = action.ToString().ToLowerInvariant();
        await _catalog.SaveUserCommandAsync(new(commandId, actionName, conflictId.ToString("D"), "pending"), cancellationToken).ConfigureAwait(false);
        try
        {
            switch (action)
            {
                case MirrorPulseConflictAction.KeepLocal:
                    await AlignAcknowledgedRevisionAsync(conflict, cancellationToken).ConfigureAwait(false);
                    await _completion.PrepareRetryAsync(Guid.Parse(conflict.ChangeId), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case MirrorPulseConflictAction.Retry:
                    await _completion.PrepareRetryAsync(Guid.Parse(conflict.ChangeId), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case MirrorPulseConflictAction.KeepBoth:
                    preservedPath = await PreserveLocalCopyAsync(conflict, cancellationToken)
                        .ConfigureAwait(false);
                    await _feed.AcknowledgeAsync(
                        [new CloudLocalChangeAcknowledgement(Guid.Parse(conflict.ChangeId), null)],
                        cancellationToken).ConfigureAwait(false);
                    break;
                case MirrorPulseConflictAction.KeepRemote:
                case MirrorPulseConflictAction.DeleteLocal:
                    DeleteLocalCopy(conflict);
                    await _feed.AcknowledgeAsync(
                        [new CloudLocalChangeAcknowledgement(Guid.Parse(conflict.ChangeId), null)],
                        cancellationToken).ConfigureAwait(false);
                    break;
                case MirrorPulseConflictAction.DeleteRemote:
                    await DeleteRemoteAsync(conflict, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }

            await _catalog.SetUploadConflictStatusAsync(conflictId, MirrorPulseConflictStatus.Resolved,
                cancellationToken).ConfigureAwait(false);
            string commandState = action is MirrorPulseConflictAction.KeepLocal or MirrorPulseConflictAction.Retry or MirrorPulseConflictAction.DeleteRemote
                ? "pending" : "resolved";
            await _catalog.SaveUserCommandAsync(new(commandId, actionName, conflictId.ToString("D"), commandState), cancellationToken).ConfigureAwait(false);
            return new MirrorPulseConflictResolution(commandId, conflictId, action,
                preservedPath, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _catalog.SaveUserCommandAsync(new(commandId, actionName, conflictId.ToString("D"), "failed"), cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask AlignAcknowledgedRevisionAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conflict.RemoteRevision))
        {
            throw new InvalidOperationException("Keep-local requires a current remote revision.");
        }

        await using ICloudStateTransaction transaction = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState item = await transaction.Items.GetByRelativePathAsync(
            conflict.RelativePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The CfSharp item for the conflict was not found.");
        await transaction.Items.UpsertAsync(new CloudItemState(item.ItemId, item.RemoteId,
            item.RelativePath, item.Kind, conflict.RemoteRevision, item.LocalFileId,
            item.IsTombstone, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DeleteRemoteAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken)
    {
        if (_router is null || _mutations is null)
        {
            throw new NotSupportedException("Remote conflict deletion requires the active Adapter Worker.");
        }

        if (string.IsNullOrWhiteSpace(conflict.RemoteRevision))
        {
            throw new InvalidOperationException("Delete-remote requires a current remote revision.");
        }

        string callbackPath = Path.Combine(_syncRootPath,
            conflict.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        MirrorPulseRoutedItem routed = _router.ResolvePath(callbackPath);
        bool isDirectory = false;
        await using (ICloudStateTransaction transaction = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            CloudItemState? item = await transaction.Items.GetByRelativePathAsync(
                conflict.RelativePath, cancellationToken).ConfigureAwait(false);
            isDirectory = item?.Kind == CloudItemKind.Directory;
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        await _mutations.DeleteAsync(new MirrorPulseWorkerDeleteRequest(
            conflict.InstanceId, routed.RelativePath, conflict.RemoteRevision, isDirectory, Guid.Parse(conflict.ChangeId), routed.RootKey),
            cancellationToken).ConfigureAwait(false);
        await AlignRemoteDeletedAsync(conflict, cancellationToken).ConfigureAwait(false);
        await _completion.PrepareRetryAsync(Guid.Parse(conflict.ChangeId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask AlignRemoteDeletedAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState item = await transaction.Items.GetByRelativePathAsync(
            conflict.RelativePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The CfSharp item for the conflict was not found.");
        await transaction.Items.UpsertAsync(new CloudItemState(item.ItemId, item.RemoteId,
            item.RelativePath, item.Kind, null, item.LocalFileId,
            item.IsTombstone, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<string> PreserveLocalCopyAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken) => new(_copies.PreserveAsync(conflict,
            MirrorPulseConflictPreservedSide.Local, token => _copies.OpenLocalAsync(conflict, token), cancellationToken));

    private void DeleteLocalCopy(MirrorPulseConflictRecord conflict)
    {
        string path = ResolveLocalPath(conflict.RelativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string ResolveLocalPath(string relativePath)
    {
        string candidate = Path.GetFullPath(Path.Combine(_syncRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = _syncRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The conflict path escaped the sync root.");
        }

        return candidate;
    }
}
