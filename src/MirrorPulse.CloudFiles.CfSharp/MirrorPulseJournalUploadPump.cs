using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Continuously delivers supported file operations from CfSharp's durable local journal to the
/// selected Worker. The CfSharp acknowledgement is written only after the Worker confirms its
/// conditional upload, so a crash leaves the same operation available after restart.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseJournalUploadPump : IAsyncDisposable
{
    private readonly CloudLocalChangeFeed _feed;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseJournalUploadSource _source;
    private readonly MirrorPulseJournalUploadCompletion _completion;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly IMirrorPulseWorkerUploadTransport _uploads;
    private readonly IMirrorPulseWorkerStatTransport _stats;
    private readonly IMirrorPulseWorkerMutationTransport? _mutations;
    private readonly MirrorPulseConflictCenter? _conflicts;
    private readonly MirrorPulseConflictNotificationBridge? _notifications;
    private readonly string _syncRootPath;
    private readonly LocalRollingLogWriter _log;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private readonly MirrorPulseJournalPumpRunner _runner = new();
    private readonly MirrorPulseMutationExecutor _mutationExecutor;
    private readonly MirrorPulseMutationReadback _readback;
    private readonly MirrorPulseFullRescanRecovery? _rescan;
    private readonly Func<InstanceId, bool> _mayDispatch;
    private readonly CloudFileSystem? _fileSystem;
    private readonly MirrorPulseInstanceScheduler _scheduler;
    private readonly bool _ownsScheduler;

    public MirrorPulseJournalPumpHealth Health => _runner.Health;

    public MirrorPulseJournalUploadPump(
        CloudLocalChangeFeed feed,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        IMirrorPulseWorkerUploadTransport uploads,
        IMirrorPulseWorkerStatTransport stats,
        MirrorPulseCfSharpStateSession state,
        string syncRootPath,
        string dataRootPath,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseJournalUploadCompletion completion,
        MirrorPulseConflictCenter? conflicts = null,
        MirrorPulseConflictNotificationBridge? notifications = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        IMirrorPulseWorkerRangeTransport? ranges = null,
        IMirrorPulseWorkerDirectoryPageSource? directories = null,
        CloudFileSystem? fileSystem = null,
        MirrorPulseInstanceScheduler? scheduler = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _mutations = mutations;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _mutationExecutor = new MirrorPulseMutationExecutor(catalog);
        _readback = new MirrorPulseMutationReadback(stats, ranges, directories);
        _fileSystem = fileSystem;
        _ownsScheduler = scheduler is null;
        _scheduler = scheduler ?? new MirrorPulseInstanceScheduler();
        if (fileSystem is not null)
            _rescan = new(catalog, new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog,
                uploads, stats, mayDispatch, mutations, ranges, directories, conflicts, notifications, _scheduler).ReconcileAsync,
                feed.AcknowledgeFullRescanAsync, ProjectRescanAsync);
        _conflicts = conflicts;
        _notifications = notifications;
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        _mayDispatch = mayDispatch;
        _syncRootPath = Path.GetFullPath(syncRootPath);
        _log = new LocalRollingLogWriter(Path.Combine(dataRootPath, "logs"));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _source = new MirrorPulseJournalUploadSource(feed, router, catalog, mayDispatch, _completion);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The local journal upload pump has already started.");
        }

        await _feed.StartAsync(cancellationToken).ConfigureAwait(false);
        _loop = Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                bool dispatched = await _runner.RunCycleAsync(ReadPendingAsync,
                    DispatchAsync, ReportFailureAsync, cancellationToken).ConfigureAwait(false);
                if (!dispatched || !Health.Healthy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<MirrorPulseJournalUploadBatch> ReadPendingAsync(CancellationToken cancellationToken)
    {
        var recovery = new MirrorPulseMutationProjectionRecovery(_catalog);
        IReadOnlyList<Guid> repaired = await recovery.RepairAsync(async (operationId, token) =>
        {
            await using ICloudStateTransaction transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
            bool pending = await transaction.Operations.GetAsync(operationId, token).ConfigureAwait(false) is not null;
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return pending;
        }, ProjectAcceptedAsync, cancellationToken).ConfigureAwait(false);
        foreach (Guid operationId in repaired) _runner.ClearRecoveredFault(operationId);
        IReadOnlyList<RootId> deferred = await _catalog.ReadDeferredRescanRootsAsync(cancellationToken).ConfigureAwait(false);
        bool enabledDeferredRoot = _router.Registrations.Any(root => root.State == RootRegistrationState.Active &&
            _mayDispatch(root.InstanceId) && deferred.Contains(root.RootId));
        MirrorPulseJournalUploadBatch batch = _source.RequiresFullRescan || enabledDeferredRoot ||
            await _catalog.ReadFullRescanAsync(cancellationToken).ConfigureAwait(false) is not null ? new([], 0, true) :
            await _source.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (!batch.RequiresFullRescan) return batch;
        foreach (RootRegistration root in _router.Registrations)
            await _catalog.SaveInstanceRuntimeStateAsync(new(root.InstanceId, "Reconciling", true, null), cancellationToken).ConfigureAwait(false);
        if (_rescan is null) throw new NotSupportedException("The Host requires a full rescan service.");
        await _rescan.RunAsync(new MirrorPulseLocalChangeSignal(true), cancellationToken).ConfigureAwait(false);
        _source.ClearFullRescanRequest();
        return new([], 0, false);
    }

    private async ValueTask ProjectRescanAsync(CancellationToken cancellationToken)
    {
        foreach (RootRegistration root in _router.Registrations.Where(root => root.State == RootRegistrationState.Active && _mayDispatch(root.InstanceId)))
            await _catalog.CompleteRootRescanAsync(root.RootId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RootId> deferred = await _catalog.ReadDeferredRescanRootsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var roots in _router.Registrations.GroupBy(root => root.InstanceId))
            await _catalog.SaveInstanceRuntimeStateAsync(new(roots.Key, _mayDispatch(roots.Key) ? "Connected" : "Offline",
                roots.Any(root => deferred.Contains(root.RootId)), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProjectAcceptedAsync(MirrorPulseMutationRecord record, CancellationToken cancellationToken) =>
        await _catalog.SaveInstanceRuntimeStateAsync(new(record.Intent.InstanceId, "Connected", false, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

    private ValueTask<bool> DispatchAsync(MirrorPulseWorkerChangeCommand command, CancellationToken cancellationToken) =>
        _scheduler.RunAsync(command.InstanceId, token => DispatchCoreAsync(command, token), cancellationToken);

    private async ValueTask<bool> DispatchCoreAsync(
        MirrorPulseWorkerChangeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Kind is MirrorPulseWorkerChangeKind.Move or MirrorPulseWorkerChangeKind.Delete)
        {
            if (_mutations is null)
            {
                return false;
            }

            return await DispatchMutationAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (command.IsDirectory || command.Kind is not
            (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate))
        {
            return false;
        }

        string? syncRootRelativePath = null;
        string dispatchPhase = "ResolvePath";
        try
        {
            string localPath = _router.ResolveUploadPath(command.InstanceId,
                command.RootKey, command.RelativePath);
            syncRootRelativePath = Path.GetRelativePath(_syncRootPath, localPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            dispatchPhase = "RecoverIntent";
            if (await CheckPreviousMutationAsync(command, cancellationToken).ConfigureAwait(false)) return true;
            if (!File.Exists(localPath))
            {
                await LogDispatchBoundaryAsync(command, "MissingLocalContent", null, cancellationToken).ConfigureAwait(false);
                return false;
            }
            dispatchPhase = "ResolveRevision";
            string? revision = await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(
                _state.OpenStore, _stats, command, syncRootRelativePath,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            dispatchPhase = "OpenContent";
            await using var content = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            if (_fileSystem is null) throw new NotSupportedException("Journal content uploads require the Cloud Files confirmation owner.");
            dispatchPhase = "InspectBinding";
            MirrorPulseUploadBinding binding = MirrorPulseContentConfirmation.CaptureUploadBinding(
                await _fileSystem.GetFile(syncRootRelativePath).InspectAsync(cancellationToken).ConfigureAwait(false));
            dispatchPhase = "HashContent";
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
            content.Position = 0;
            dispatchPhase = "ExecuteRemote";
            await _mutationExecutor.ExecuteAsync(Intent(command, revision, content.Length, hash, binding), async token =>
                await _uploads.UploadAsync(new MirrorPulseWorkerUploadRequest(command.InstanceId, command.RelativePath,
                    revision, content, content.Length, command.OperationId, hash), token).ConfigureAwait(false),
                async (accepted, token) =>
                {
                    await content.DisposeAsync().ConfigureAwait(false);
                    await AcknowledgeAsync(command.OperationId, accepted, token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MirrorPulseUploadConflictException conflict)
        {
            await LogDispatchBoundaryAsync(command, dispatchPhase, conflict, cancellationToken).ConfigureAwait(false);
            return await DeferConflictAsync(command, syncRootRelativePath!,
                conflict.ExpectedRevision, conflict.ActualRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MirrorPulseWorkerMutationConflictException conflict)
        {
            return await DeferConflictAsync(command, syncRootRelativePath!,
                conflict.ExpectedRevision, conflict.ActualRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogDispatchBoundaryAsync(command, dispatchPhase, exception, cancellationToken).ConfigureAwait(false);
            if (exception is MirrorPulseJournalAcknowledgementException) throw;
            await _completion.DeferFailedUploadAsync(command.OperationId, DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task LogDispatchBoundaryAsync(MirrorPulseWorkerChangeCommand command, string phase,
        Exception? exception, CancellationToken token)
    {
        try
        {
            await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "JournalDispatchBoundary", DateTimeOffset.UtcNow,
                [new("operationId", command.OperationId.ToString("D")), new("kind", command.Kind.ToString()),
                 new("dispatchPhase", phase), new("hasItemReference", (command.ItemId is not null).ToString()),
                 new("failureCategory", exception is null ? "IO" : SafeDiagnosticPolicy.ClassifyFailure(exception.GetBaseException())),
                 new("hresult", (exception?.GetBaseException().HResult ?? 0).ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
        }
        catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
    }

    private async ValueTask<bool> DispatchMutationAsync(
        MirrorPulseWorkerChangeCommand command,
        CancellationToken cancellationToken)
    {
        string localPath = _router.ResolveUploadPath(command.InstanceId,
            command.RootKey, command.RelativePath);
        string syncRootRelativePath = Path.GetRelativePath(_syncRootPath, localPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            if (await CheckPreviousMutationAsync(command, cancellationToken).ConfigureAwait(false)) return true;
            string? revision;
            if (command.Kind == MirrorPulseWorkerChangeKind.Delete)
            {
                revision = await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(
                    _state.OpenStore, _stats, command, syncRootRelativePath,
                    allowTombstone: true, allowMissingRemote: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await _mutationExecutor.ExecuteAsync(Intent(command, revision), token =>
                    _mutations!.DeleteAsync(new MirrorPulseWorkerDeleteRequest(command.InstanceId,
                        command.RelativePath, revision, command.IsDirectory, command.OperationId), token),
                    (_, token) => AcknowledgeAsync(command.OperationId, null, token), cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (string.IsNullOrWhiteSpace(command.PreviousRelativePath))
            {
                return false;
            }

            revision = await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(
                _state.OpenStore, _stats, command, syncRootRelativePath,
                remoteStatPath: command.PreviousRelativePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _mutationExecutor.ExecuteAsync(Intent(command, revision), async token =>
                await _mutations!.MoveAsync(new MirrorPulseWorkerMoveRequest(command.InstanceId,
                    command.PreviousRelativePath, command.RelativePath, revision, command.IsDirectory, command.OperationId), token).ConfigureAwait(false),
                (accepted, token) => AcknowledgeAsync(command.OperationId, accepted, token), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MirrorPulseUploadConflictException conflict)
        {
            return await DeferConflictAsync(command, syncRootRelativePath,
                conflict.ExpectedRevision, conflict.ActualRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MirrorPulseWorkerMutationConflictException conflict)
        {
            return await DeferConflictAsync(command, syncRootRelativePath,
                conflict.ExpectedRevision, conflict.ActualRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is MirrorPulseJournalAcknowledgementException) throw;
            await _completion.DeferFailedUploadAsync(command.OperationId, DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> DeferConflictAsync(
        MirrorPulseWorkerChangeCommand command,
        string syncRootRelativePath,
        string? expectedRevision,
        string? actualRevision,
        CancellationToken cancellationToken)
    {
        var record = new MirrorPulseConflictRecord(command.OperationId,
            command.InstanceId, command.OperationId.ToString("D"),
            syncRootRelativePath, MirrorPulseConflictReason.StaleRemoteRevision,
            MirrorPulseVersionComparison.Diverged, expectedRevision,
            actualRevision, DateTimeOffset.UtcNow);
        await _catalog.SaveUploadConflictAsync(record, cancellationToken).ConfigureAwait(false);
        _conflicts?.Upsert(record);
        if (_notifications is not null)
        {
            try
            {
                await _notifications.NotifyAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Notification delivery cannot turn a durable conflict into an upload retry.
            }
        }
        await _completion.DeferFailedUploadAsync(command.OperationId, DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask AcknowledgeAsync(Guid operationId, string? revision, CancellationToken cancellationToken)
    {
        string phase = "ReadIntent";
        try
        {
            MirrorPulseMutationRecord record = await _catalog.ReadMutationAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The accepted mutation intent disappeared.");
            if (!record.Intent.IsDirectory && record.Intent.Kind is MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate)
            {
                if (record.Intent.UploadBinding is null)
                {
                    await _catalog.SaveBlockedLocalOperationAsync(new(operationId, record.Intent.InstanceId, record.Intent.RelativePath,
                        MirrorPulseLocalOperationBlockReason.MissingUploadBinding, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                    throw new MirrorPulseMutationAmbiguousException("The historical journal upload has no upload-time binding.");
                }
                if (_fileSystem is null) throw new NotSupportedException("Content confirmation requires the Cloud Files owner.");
                MirrorPulseContentAcceptanceProof? proof = await _catalog.ReadContentAcceptanceProofAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (proof is null)
                {
                    phase = "ReadMetadata";
                    MirrorPulseWorkerDirectoryEntry remote = await _readback.ReadMetadataAsync(record.Intent, cancellationToken).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("The accepted remote file has no metadata.");
                    phase = "ValidateMetadata";
                    if (remote.IsDeleted || !string.Equals(remote.ItemKind, "File", StringComparison.OrdinalIgnoreCase) || remote.RemoteRevision != revision)
                        throw new MirrorPulseMutationAmbiguousException("Remote metadata does not match the accepted upload.");
                    CloudPlaceholderIdentity identity = MirrorPulsePlaceholderIdentity.Create(record.Intent.InstanceId, remote.RemoteId, revision).ToCfSharp();
                    proof = new(operationId, record.Intent.UploadBinding, identity.ItemId, identity.RemoteId, revision,
                        record.Intent.ContentLength!.Value, record.Intent.ContentSha256!);
                    phase = "SaveProof";
                    await _catalog.SaveContentAcceptanceProofAsync(proof, cancellationToken).ConfigureAwait(false);
                }
                string localPath = _router.ResolveUploadPath(record.Intent.InstanceId, record.Intent.RootKey, record.Intent.RelativePath);
                string relative = Path.GetRelativePath(_syncRootPath, localPath).Replace(Path.DirectorySeparatorChar, '/');
                await _feed.SuppressProviderEchoAsync(CloudStateOperationKind.MetadataUpdate, relative,
                    DateTimeOffset.UtcNow.AddSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
                phase = "ConfirmContent";
                MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(
                    _fileSystem.GetFile(relative), proof, cancellationToken).ConfigureAwait(false);
                phase = "SaveReceipt";
                await _catalog.SaveContentConfirmationReceiptAsync(operationId, receipt, CancellationToken.None).ConfigureAwait(false);
                if (!receipt.MayAcknowledge)
                    throw new MirrorPulseMutationAmbiguousException($"Local journal content confirmation requires recovery ({receipt.Outcome}).");
            }
            phase = "AcknowledgeFeed";
            await _completion.AcknowledgeSuccessfulUploadAsync(operationId, revision, cancellationToken).ConfigureAwait(false);
            phase = "ProjectProduct";
            await ProjectAcceptedAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "JournalAcknowledgementFailed", DateTimeOffset.UtcNow,
                    [new("operationId", operationId.ToString("D")), new("acknowledgementPhase", phase),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(exception.GetBaseException())),
                     new("hresult", exception.GetBaseException().HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception logException) when (logException is not OperationCanceledException) { }
            throw new MirrorPulseJournalAcknowledgementException("The accepted Worker result could not be acknowledged.", exception);
        }
    }

    private async ValueTask<bool> CheckPreviousMutationAsync(MirrorPulseWorkerChangeCommand command, CancellationToken cancellationToken)
    {
        MirrorPulseMutationRecord? record = await _catalog.ReadMutationAsync(command.OperationId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.State == MirrorPulseMutationState.Prepared) return false;
        if (record.State == MirrorPulseMutationState.Acknowledged) return true;
        await _mutationExecutor.ReconcileAsync(record, _readback.VerifyAsync,
            (revision, token) => AcknowledgeAsync(command.OperationId, revision, token), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static MirrorPulseMutationIntent Intent(MirrorPulseWorkerChangeCommand command, string? revision,
        long? length = null, string? hash = null, MirrorPulseUploadBinding? binding = null) => new(command.OperationId, command.InstanceId, command.RootKey,
            command.Kind, command.RelativePath, command.PreviousRelativePath, command.IsDirectory, revision,
            length, hash, MirrorPulseMutationOrigin.Journal, binding);

    private async ValueTask ReportFailureAsync(MirrorPulseWorkerChangeCommand? command, string code,
        Exception exception, CancellationToken cancellationToken)
    {
        await LogFailureAsync(code, exception, command, cancellationToken).ConfigureAwait(false);
        if (command is not null)
            await _catalog.SaveInstanceRuntimeStateAsync(new(command.InstanceId, "Journal failed", false, null, code),
                cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        await _feed.DisposeAsync().ConfigureAwait(false);
        if (_ownsScheduler) await _scheduler.DisposeAsync().ConfigureAwait(false);
        _log.Dispose();
        _shutdown.Dispose();
    }

    private async ValueTask LogFailureAsync(string message, Exception exception,
        MirrorPulseWorkerChangeCommand? command, CancellationToken cancellationToken)
    {
        try
        {
            var fields = new List<LogField>
            {
                new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(exception)),
                new("hresult", exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (command is not null)
            {
                fields.Add(new("operationId", command.OperationId.ToString("D")));
                fields.Add(new("kind", command.Kind.ToString()));
                if (await _catalog.ReadMutationAsync(command.OperationId, cancellationToken).ConfigureAwait(false) is { } record)
                {
                    fields.Add(new("mutationState", record.State.ToString()));
                    fields.Add(new("proofPresent", (await _catalog.ReadContentAcceptanceProofAsync(command.OperationId, cancellationToken).ConfigureAwait(false) is not null).ToString()));
                }
                MirrorPulseContentConfirmationReceipt? receipt = await _catalog.ReadContentConfirmationReceiptAsync(
                    command.OperationId, cancellationToken).ConfigureAwait(false);
                if (receipt is not null)
                {
                    fields.Add(new("confirmationOutcome", receipt.Outcome.ToString()));
                    fields.Add(new("confirmationStage", receipt.Stage.ToString()));
                    fields.Add(new("nativeApplied", receipt.NativeApplied.ToString()));
                    fields.Add(new("nativeVerified", receipt.NativeConfirmationVerified.ToString()));
                    fields.Add(new("projectionCommitted", receipt.DurableProjectionCommitted.ToString()));
                }
            }

            await _log.WriteAsync(new LogEntry(LogLevel.Warning, "CloudFiles.Upload",
                message, DateTimeOffset.UtcNow, fields), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception logException) when (logException is not OperationCanceledException)
        {
            // Local diagnostics must not stop the durable upload pump.
        }
    }
}
