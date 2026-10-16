using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Applies explicit upload-conflict decisions while preserving CfSharp's journal authority.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class MirrorPulseUploadConflictActions
{
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly CloudLocalChangeFeed _feed;
    private readonly MirrorPulseJournalUploadCompletion _completion;
    private readonly string _syncRootPath;
    private readonly MirrorPulseConflictCopyStore _copies;
    private readonly MirrorPulseRootRouter? _router;
    private readonly IMirrorPulseWorkerMutationTransport? _mutations;
    private readonly MirrorPulseConflictCenter? _conflictCenter;
    private readonly MirrorPulseInstanceScheduler? _scheduler;
    private readonly CloudFileSystem? _fileSystem;
    private readonly MirrorPulseNamespaceContentConfirmation? _contentConfirmation;
    private readonly MirrorPulseNamespaceExecutionSession? _namespaceExecution;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly MirrorPulseConflictDeletionLocalCoordinator? _localDeletion;
    private readonly IMirrorPulseWorkerStatTransport? _stats;
    private readonly Func<InstanceId, bool> _mayDispatch;
    private readonly MirrorPulseConflictNotificationBridge? _conflictNotifications;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _announcedDeletionConflicts = new();

    public MirrorPulseUploadConflictActions(
        MirrorPulseProductCatalog catalog,
        MirrorPulseCfSharpStateSession state,
        CloudLocalChangeFeed feed,
        BackoffPolicy retryPolicy,
        MirrorPulseStoragePaths paths,
        MirrorPulseRootRouter? router = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        MirrorPulseConflictCenter? conflictCenter = null,
        MirrorPulseInstanceScheduler? scheduler = null,
        CloudFileSystem? fileSystem = null,
        MirrorPulseNamespaceActor? namespaceActor = null,
        MirrorPulseNamespaceExecutionSession? namespaceExecution = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        IMirrorPulseWorkerStatTransport? stats = null,
        Func<InstanceId, bool>? mayDispatch = null,
        MirrorPulseConflictNotificationBridge? conflictNotifications = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _completion = new MirrorPulseJournalUploadCompletion(feed, state, retryPolicy);
        _syncRootPath = Path.GetFullPath(paths.SyncRootPath);
        _copies = new MirrorPulseConflictCopyStore(paths);
        _router = router;
        _mutations = mutations;
        _conflictCenter = conflictCenter;
        _scheduler = scheduler;
        _fileSystem = fileSystem;
        _namespaceExecution = namespaceExecution;
        _namespaceWork = namespaceWork;
        _stats = stats;
        _mayDispatch = mayDispatch ?? (_ => true);
        _conflictNotifications = conflictNotifications;
        _contentConfirmation = router is null ? null : new(catalog, router, namespaceActor, namespaceExecution);
        if (fileSystem is not null && router is not null && namespaceActor is not null && namespaceExecution is not null &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            _localDeletion = new(fileSystem, catalog, router, namespaceActor, namespaceExecution, _copies);
    }

    public async ValueTask<MirrorPulseConflictResolution> ApplyAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken = default)
    {
        MirrorPulseConflictRecord conflict = await _catalog.ReadUploadConflictAsync(
            conflictId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The upload conflict was not found.");
        return _scheduler is null ? await ApplyAdmittedAsync(conflict, action, cancellationToken).ConfigureAwait(false) :
            await _scheduler.RunAsync(conflict.InstanceId, token => ApplyAdmittedAsync(conflict, action, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseConflictResolution> ApplyAdmittedAsync(MirrorPulseConflictRecord conflict,
        MirrorPulseConflictAction action, CancellationToken cancellationToken)
    {
        Guid conflictId = conflict.ConflictId;
        conflict = await _catalog.ReadUploadConflictAsync(conflictId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The admitted upload conflict disappeared.");
        if (!conflict.IsPending)
        {
            throw new InvalidOperationException("The upload conflict has already been resolved.");
        }
        var group = Guid.TryParse(conflict.ChangeId, out Guid originalId) ?
            await _catalog.ReadJournalCoalescingOwnerAsync(originalId, cancellationToken).ConfigureAwait(false) : null;
        if (Guid.TryParse(conflict.ChangeId, out Guid deletionOriginal) &&
            await _catalog.ReadConflictDeletionOwnerAsync(deletionOriginal, cancellationToken).ConfigureAwait(false) is { } deletionOwner)
        {
            if (action != MirrorPulseConflictAction.DeleteRemote)
                throw new InvalidOperationException("This conflict already belongs to a retained deletion decision.");
            return await ApplyExistingDeletionAsync(deletionOwner, conflict, cancellationToken).ConfigureAwait(false);
        }
        if (group is not null && action is not (MirrorPulseConflictAction.Retry or MirrorPulseConflictAction.KeepLocal or MirrorPulseConflictAction.DeleteRemote))
            throw new NotSupportedException("This coalesced decision requires a complete local group operation; no original journal ID was acknowledged.");

        string? preservedPath = null;
        if (!Enum.IsDefined(action) || action == MirrorPulseConflictAction.Defer)
            throw new ArgumentOutOfRangeException(nameof(action));
        Guid commandId = Guid.NewGuid();
        string actionName = action.ToString().ToLowerInvariant();
        await _catalog.SaveUserCommandAsync(new(commandId, actionName, conflictId.ToString("D"), "pending"), cancellationToken).ConfigureAwait(false);
        try
        {
            if (action == MirrorPulseConflictAction.DeleteRemote)
            {
                await PrepareDeletionAdmittedAsync(commandId, conflict, group, cancellationToken).ConfigureAwait(false);
                return await ExecuteDeletionAdmittedAsync(commandId, cancellationToken).ConfigureAwait(false);
            }
            if (group is not null)
            {
                if (action == MirrorPulseConflictAction.KeepLocal &&
                    (await _catalog.ReadJournalCoalescingExecutionAsync(group.PlanId, cancellationToken).ConfigureAwait(false))!
                    .Steps.Any(step => step.Kind == MirrorPulseCoalescingStepKind.VerifyUnchanged))
                {
                    var content = await CaptureGroupKeepLocalContentAsync(group, cancellationToken).ConfigureAwait(false);
                    _ = await _catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(commandId, conflict.ConflictId,
                        content, cancellationToken).ConfigureAwait(false);
                }
                else
                    _ = await _catalog.AuthorizeJournalCoalescingRetryAsync(commandId, conflict.ConflictId,
                        action == MirrorPulseConflictAction.KeepLocal, cancellationToken).ConfigureAwait(false);
                if (_conflictCenter is not null)
                    foreach (var resolved in await _catalog.ReadUploadConflictsAsync(pendingOnly: false, cancellationToken).ConfigureAwait(false))
                        if (!resolved.IsPending && group.Members.Any(member => member.OperationId.ToString("D") == resolved.ChangeId))
                            _conflictCenter.Remove(resolved.ConflictId);
                foreach (var member in group.Members)
                    await _completion.PrepareRetryAsync(member.OperationId, cancellationToken).ConfigureAwait(false);
                return new(commandId, conflictId, action, null, DateTimeOffset.UtcNow);
            }
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
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }

            await _catalog.SetUploadConflictStatusAsync(conflictId, MirrorPulseConflictStatus.Resolved,
                cancellationToken).ConfigureAwait(false);
            string commandState = action is MirrorPulseConflictAction.KeepLocal or MirrorPulseConflictAction.Retry
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

    private async Task<MirrorPulseCoalescingContent> CaptureGroupKeepLocalContentAsync(MirrorPulseJournalCoalescingPlan group,
        CancellationToken token)
    {
        if (_fileSystem is null || _router is null || _contentConfirmation is null)
            throw new NotSupportedException("Keeping local verification content requires the active Cloud Files owner.");
        var root = _router.GetRegistration(group.InstanceId, group.RootKey);
        using var admission = _namespaceWork?.Enter([root.RootId]);
        if (_namespaceExecution is null) return await CaptureAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("Protected content capture requires Windows 11 version 24H2 or later.");
        return await _namespaceExecution.RunNormalUserOperationAsync(CaptureAsync).ConfigureAwait(false);

        async Task<MirrorPulseCoalescingContent> CaptureAsync()
        {
            string path = _router.ResolveUploadPath(group.InstanceId, group.RootKey, group.FinalPath);
            string relative = Path.GetRelativePath(_syncRootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            var file = _fileSystem.GetFile(relative);
            var before = await file.InspectAsync(token).ConfigureAwait(false);
            if (!before.Exists || before.IsTombstone || before.ItemId != group.ItemId ||
                before.IsPlaceholder && before.ContentAvailability != CloudContentAvailability.FullyAvailable)
                throw new InvalidOperationException("Keeping local content requires the original logical item with resident local bytes.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var binding = await _contentConfirmation.CaptureUploadBindingAsync(file, await file.InspectAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            var content = new MirrorPulseCoalescingContent(stream.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), binding);
            var after = await file.InspectAsync(token).ConfigureAwait(false);
            if (after.ItemId != group.ItemId || after.Length != content.Length ||
                await _contentConfirmation.CaptureUploadBindingAsync(file, after, token).ConfigureAwait(false) != binding)
                throw new InvalidDataException("The authorized local object changed during content capture.");
            return content;
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
