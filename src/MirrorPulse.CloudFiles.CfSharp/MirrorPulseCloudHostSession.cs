using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public interface IMirrorPulseCloudRuntime : IAsyncDisposable
{
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string name, CancellationToken token) => ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(
            new NotSupportedException("This runtime does not prepare original file transfers."));
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> CopyNamespaceTransferAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not copy original file transfers."));
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> VerifyNamespaceTransferTargetAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not verify original transfer targets."));
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferTargetBirthAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not prepare original transfer target births."));
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> PublishNamespaceTransferTargetAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not publish original transfer targets."));
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceTransferCopy> DeleteNamespaceTransferSourceAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not delete original transfer sources."));
    ValueTask<MirrorPulseNamespaceTransferCopy> ReadNamespaceTransferAsync(Guid operationId, CancellationToken token) =>
        ValueTask.FromException<MirrorPulseNamespaceTransferCopy>(new NotSupportedException("This runtime does not read original file transfers."));

    IReadOnlyList<MirrorPulseNamespaceStartupRoot> NamespaceStartupResults => [];
    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceRootAsync(Guid operationId, RootId rootId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceInitializationCompletion>(
            new NotSupportedException("This runtime does not recover selected namespace roots."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceCurrentTreeAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceInitializationCompletion>(
            new NotSupportedException("This runtime does not recover the current registered namespace graph."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceBornMemberRecoveryResult> RecoverNamespaceBornMemberAsync(Guid initializationOperationId, Guid actionId,
        Guid memberId, CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceBornMemberRecoveryResult>(
            new NotSupportedException("This runtime does not execute registered born recovery."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceBirthProtection> VerifyNamespaceBornMemberRecoveryAsync(Guid initializationOperationId, Guid protectionId,
        Guid memberId, CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceBirthProtection>(
            new NotSupportedException("This runtime does not verify registered born member recovery."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseProtectedNamespacePermissionResult> RecoverNamespaceOriginalMemberAsync(Guid initializationOperationId, Guid memberId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseProtectedNamespacePermissionResult>(
            new NotSupportedException("This runtime does not recover registered original members."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceInitializationCompletion> InitializeNamespaceProtectionAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceInitializationCompletion>(
            new NotSupportedException("This runtime does not initialize original namespace protection."));

    ValueTask<MirrorPulseNamespaceInitializationStatus> ReadNamespaceInitializationStatusAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceInitializationStatus>(
            new NotSupportedException("This runtime does not read initialization status."));
    ValueTask<IReadOnlyList<MirrorPulseNamespacePermissionTree>> ReadNamespacePermissionTreesAsync(RootId rootId,
        Guid? afterManifestId, int limit, CancellationToken cancellationToken) =>
        ValueTask.FromException<IReadOnlyList<MirrorPulseNamespacePermissionTree>>(new NotSupportedException("This runtime does not discover original permission captures."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespacePermissionTreeResult> RecoverNamespaceOriginalTreeAsync(Guid manifestId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespacePermissionTreeResult>(
            new NotSupportedException("This runtime does not recover original permission trees."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementAsync(Guid operationId, Guid memberId,
        string sourcePath, CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceReplacementExecution>(
            new NotSupportedException("This Cloud Files runtime does not prepare controlled replacements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteNamespaceReplacementAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceReplacementCompletion>(
            new NotSupportedException("This Cloud Files runtime does not execute controlled replacements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceReplacementStatus> ReadNamespaceReplacementStatusAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceReplacementStatus>(
            new NotSupportedException("This Cloud Files runtime does not read replacement status."));
    MirrorPulseJournalPumpHealth? JournalHealth => null;
    IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> RootRenameRecoveryResults => [];
    ValueTask StartAsync(CancellationToken cancellationToken);

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberRegistration> CreateNamespaceMemberAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceMemberRegistration>(
            new NotSupportedException("This Cloud Files runtime does not create controlled local members."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
        string name, string sourcePath, CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceBirthPlan>(
            new NotSupportedException("This Cloud Files runtime does not prepare controlled imports."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceMemberRegistration>(
            new NotSupportedException("This Cloud Files runtime does not execute controlled imports."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceImportStatus> ReadNamespaceImportStatusAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceImportStatus>(
            new NotSupportedException("This Cloud Files runtime does not read import status."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberInspection> InspectNamespaceMemberAsync(string relativePath,
        bool isDirectory, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceMemberInspection>(
            new NotSupportedException("This Cloud Files runtime does not inspect namespace membership."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeAsync(Guid memberId,
        CancellationToken cancellationToken) => ValueTask.FromException<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>>(
            new NotSupportedException("This Cloud Files runtime does not read namespace trees."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string targetName, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceMemberMoveIntent>(
            new NotSupportedException("This Cloud Files runtime does not prepare controlled member moves."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteNamespaceMemberMoveAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceMemberMoveCompletion>(
            new NotSupportedException("This Cloud Files runtime does not execute controlled member moves."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceMemberMoveStatus> ReadNamespaceMemberMoveStatusAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceMemberMoveStatus>(
            new NotSupportedException("This Cloud Files runtime does not read controlled move status."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirement> PrepareNamespaceMemberRetirementAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirement>(
            new NotSupportedException("This Cloud Files runtime does not prepare controlled retirements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirement> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirement>(
            new NotSupportedException("This Cloud Files runtime does not execute controlled retirements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirement> ReadNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirement>(
            new NotSupportedException("This Cloud Files runtime does not read controlled retirement receipts."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirementBatch>(
            new NotSupportedException("This Cloud Files runtime does not prepare subtree retirements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirementBatch>(
            new NotSupportedException("This Cloud Files runtime does not execute subtree retirements."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceRetirementBatch> ReadNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseNamespaceRetirementBatch>(
            new NotSupportedException("This Cloud Files runtime does not read subtree retirement receipts."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseNamespaceBirthRecoveryResult> ExecuteNamespaceBirthRecoveryAsync(
        Guid recoveryId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceBirthRecoveryResult>(
            new NotSupportedException("This Cloud Files runtime does not execute staged birth recovery."));

    ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseManagedRootRenameRecovery>(
            new NotSupportedException("This Cloud Files runtime does not recover managed root renames."));

    [SupportedOSPlatform("windows10.0.26100")]
    ValueTask<MirrorPulseRootRenameHistory> PrepareManagedRootRenameAsync(RootId rootId, string label,
        CancellationToken cancellationToken) => ValueTask.FromException<MirrorPulseRootRenameHistory>(
            new NotSupportedException("This Cloud Files runtime does not prepare managed root renames."));

    ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
        IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseCloudStatusSnapshot>(
            new NotSupportedException("This Cloud Files runtime does not expose status."));

    ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
        InstanceId instanceId,
        CloudRemoteChangeBatch batch,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center,
        MirrorPulseConflictNotificationBridge notifications,
        CloudRemoteApplyOptions? options,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<CloudRemoteApplyResult>(
            new NotSupportedException("This Cloud Files runtime does not apply remote batches."));

    ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseConflictResolution>(
            new NotSupportedException("This Cloud Files runtime does not resolve upload conflicts."));

    ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        MirrorPulseProductCatalog catalog,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseRemoteConflictActionOutcome>(
            new NotSupportedException("This Cloud Files runtime does not resolve remote conflicts."));

    ValueTask<MirrorPulseRemoteDeleteResolution> RecoverRemoteDeleteResolutionAsync(Guid commandId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseRemoteDeleteResolution>(new NotSupportedException("This runtime does not recover explicit remote deletions."));

    ValueTask<MirrorPulseNamespaceRemoteUpdate> RecoverRemoteUpdateAsync(Guid operationId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseNamespaceRemoteUpdate>(new NotSupportedException("This runtime does not recover original remote updates."));
}

public interface IMirrorPulseCloudRuntimeFactory
{
    IMirrorPulseCloudRuntime Create(MirrorPulseStoragePaths paths);
}

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CfSharpMirrorPulseCloudRuntimeFactory : IMirrorPulseCloudRuntimeFactory
{
    private readonly ICloudDemandProvider? _provider;
    private readonly IMirrorPulseWorkerUploadTransport? _uploads;
    private readonly IMirrorPulseWorkerStatTransport? _stats;
    private readonly IMirrorPulseWorkerMutationTransport? _mutations;
    private readonly MirrorPulseRootRouter? _router;
    private readonly MirrorPulseProductCatalog? _catalog;
    private readonly Func<InstanceId, bool>? _mayDispatch;
    private readonly MirrorPulseConflictCenter? _conflicts;
    private readonly MirrorPulseConflictNotificationBridge? _notifications;
    private readonly MirrorPulseInstanceScheduler? _scheduler;
    private readonly MirrorPulseNamespaceExecutionSession? _namespaceExecution;
    private readonly MirrorPulseNamespaceActor? _namespaceActor;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly bool _initializeNamespaceOnStart;
    private readonly MirrorPulseTransferWorkerServices? _transfers;
    internal Func<ICloudStateStoreFactory, ICloudStateStoreFactory>? StateStoreDecorator { get; init; }
    internal Func<MirrorPulseTransferPublicationCheckpoint, CancellationToken, ValueTask>? TransferPublicationCheckpoint { get; init; }

    public CfSharpMirrorPulseCloudRuntimeFactory(
        ICloudDemandProvider? provider = null,
        IMirrorPulseWorkerUploadTransport? uploads = null,
        IMirrorPulseWorkerStatTransport? stats = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        MirrorPulseRootRouter? router = null,
        MirrorPulseProductCatalog? catalog = null,
        Func<InstanceId, bool>? mayDispatch = null,
        MirrorPulseConflictCenter? conflicts = null,
        MirrorPulseConflictNotificationBridge? notifications = null,
        MirrorPulseInstanceScheduler? scheduler = null,
        MirrorPulseNamespaceExecutionSession? namespaceExecution = null,
        MirrorPulseNamespaceActor? namespaceActor = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        bool initializeNamespaceOnStart = false,
        MirrorPulseTransferWorkerServices? transfers = null)
    {
        _provider = provider;
        _uploads = uploads;
        _stats = stats;
        _mutations = mutations;
        _router = router;
        _catalog = catalog;
        _mayDispatch = mayDispatch;
        _conflicts = conflicts;
        _notifications = notifications;
        _scheduler = scheduler;
        _namespaceExecution = namespaceExecution;
        _namespaceActor = namespaceActor;
        _namespaceWork = namespaceWork;
        _initializeNamespaceOnStart = initializeNamespaceOnStart;
        _transfers = transfers;
        if (initializeNamespaceOnStart && (namespaceWork is null || catalog is null || router is null || namespaceExecution is null || namespaceActor is null))
            throw new ArgumentException("Automatic namespace startup requires the complete shared Host namespace boundary.");
    }

    public IMirrorPulseCloudRuntime Create(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var state = new MirrorPulseCfSharpStateSession(paths);
        var provider = _provider ?? MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath);
        return new CfSharpRuntime(new MirrorPulseCloudFileSystemBuilder(paths)
            .WithStateStore(StateStoreDecorator?.Invoke(state) ?? state)
            .WithContentProvider(provider)
            .Build(), state, provider, StateStoreDecorator, paths.SyncRootPath, paths.DataRootPath, _uploads, _stats, _mutations, _router, _catalog,
            _mayDispatch, _conflicts, _notifications, _scheduler, _namespaceExecution, _namespaceActor, _namespaceWork, _initializeNamespaceOnStart, _transfers,
            TransferPublicationCheckpoint);
    }

    private sealed class CfSharpRuntime(
        CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state,
        ICloudDemandProvider provider,
        Func<ICloudStateStoreFactory, ICloudStateStoreFactory>? stateStoreDecorator,
        string syncRootPath,
        string dataRootPath,
        IMirrorPulseWorkerUploadTransport? uploads,
        IMirrorPulseWorkerStatTransport? stats,
        IMirrorPulseWorkerMutationTransport? mutations,
        MirrorPulseRootRouter? router,
        MirrorPulseProductCatalog? catalog,
        Func<InstanceId, bool>? mayDispatch,
        MirrorPulseConflictCenter? conflicts,
        MirrorPulseConflictNotificationBridge? notifications,
        MirrorPulseInstanceScheduler? scheduler,
        MirrorPulseNamespaceExecutionSession? namespaceExecution,
        MirrorPulseNamespaceActor? namespaceActor,
        MirrorPulseNamespaceWorkAdmission? namespaceWork,
        bool initializeNamespaceOnStart,
        MirrorPulseTransferWorkerServices? transfers,
        Func<MirrorPulseTransferPublicationCheckpoint, CancellationToken, ValueTask>? transferPublicationCheckpoint)
        : IMirrorPulseCloudRuntime
    {
        private MirrorPulseJournalUploadPump? _uploadPump;
        private MirrorPulseRetainedRemoteBatchRecoveryPump? _retainedRecovery;
        private MirrorPulseUploadConflictActions? _conflictActions;
        private MirrorPulseRemoteConflictActions? _remoteConflictActions;
        private MirrorPulseCloudProviderDiagnostics? _providerDiagnostics;
        private long _startupCheckpoint;
        private long _startupSequence;
        private readonly MirrorPulseInstanceScheduler _scheduler = scheduler ?? new();
        private readonly bool _ownsScheduler = scheduler is null;
        private readonly MirrorPulseNamespaceExecutionSession? _namespaceExecution = namespaceExecution;
        private readonly MirrorPulseNamespaceActor? _namespaceActor = namespaceActor;
        private readonly MirrorPulseNamespacePermissionCoordinator? _permissions = catalog is null ? null : new(catalog);
        private MirrorPulseNamespaceInitializationResult? _initializationProtection;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private readonly SemaphoreSlim _recoveryRequests = new(1, 1);
        private readonly CancellationTokenSource _admissionShutdown = new();
        private int _initializationClosing;
        private readonly object _disposal = new();
        private Task? _disposeTask;
        private MirrorPulseManagedRootRenameService? _rootRenames;
        private MirrorPulseNamespaceAnchorProtection? _scopedAnchorProtection;
        private MirrorPulseNamespaceAnchorProtection? _startupProtection;
        public IReadOnlyList<MirrorPulseNamespaceStartupRoot> NamespaceStartupResults { get; private set; } = [];
        public IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> RootRenameRecoveryResults { get; private set; } = [];
        public MirrorPulseJournalPumpHealth? JournalHealth => _uploadPump?.Health;

        [SupportedOSPlatform("windows10.0.26100")]
        private MirrorPulseNamespaceTransferCoordinator TransferCoordinator()
        {
            RequireImportBoundary();
            if (transfers is null) throw new NotSupportedException("Transfers require the Host's connected Worker boundary.");
            return new(fileSystem, state, catalog!, router!, _namespaceExecution!, _namespaceActor!,
                new MirrorPulseLocalTransferContentStore(new(syncRootPath, dataRootPath)), mayDispatch ?? (_ => false), transfers.ProtocolVersion)
            { TargetPublicationCheckpoint = transferPublicationCheckpoint };
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferAsync(Guid operationId, Guid memberId,
            Guid targetParentMemberId, string name, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (operationId == Guid.Empty || memberId == Guid.Empty || targetParentMemberId == Guid.Empty)
                throw new ArgumentException("Original transfer and member identities are required.");
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Contains('/') || name.Contains('\\')) throw new ArgumentException("A transfer requires one direct child name.", nameof(name));
            var historical = await catalog!.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            if (historical is not null)
            {
                if (historical.Plan.Source.Context.Member.MemberId != memberId ||
                    historical.Plan.TargetParent.Context.Member.MemberId != targetParentMemberId ||
                    historical.Plan.TargetRelativePath[(historical.Plan.TargetRelativePath.LastIndexOf('/') + 1)..] != name)
                    throw new InvalidOperationException("The original operation belongs to a different transfer request.");
                return historical;
            }
            var source = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The transfer source member is not registered.");
            var target = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(targetParentMemberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The transfer target parent is not registered.");
            if (source.Context.Member.IsDirectory || !target.Context.Member.IsDirectory ||
                source.Context.Member.RootId is not { } sourceRoot || target.Context.Member.RootId is not { } targetRoot || sourceRoot == targetRoot)
                throw new InvalidOperationException("A file transfer requires distinct managed source and target roots.");
            using var admission = namespaceWork?.Enter([sourceRoot, targetRoot]);
            await WithCurrentLibraryAsync(async stop =>
            {
                await TransferCoordinator().MaterializeSourceAsync(source, target, stop).ConfigureAwait(false); return true;
            }, token).ConfigureAwait(false);
            var instances = router!.Registrations.Where(root => root.RootId == sourceRoot || root.RootId == targetRoot)
                .Select(root => root.InstanceId).Distinct();
            return await WithAdmittedInstancesLibraryAsync(instances, (_, stop) =>
                TransferCoordinator().PrepareAsync(operationId, source, target, name, stop), token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceTransferCopy> CopyNamespaceTransferAsync(Guid operationId, CancellationToken token) =>
            RunTransferAsync(operationId, verifyTarget: false, token);

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceTransferCopy> VerifyNamespaceTransferTargetAsync(Guid operationId, CancellationToken token) =>
            RunTransferAsync(operationId, verifyTarget: true, token);

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferTargetBirthAsync(Guid operationId, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var copy = await ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            if (copy.TargetBirth is not null) return copy;
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(
                new[] { copy.Plan.SourceRoute.RootId, copy.Plan.TargetRoute.RootId }, token).ConfigureAwait(false);
            return await WithAdmittedInstancesLibraryAsync(new[] { copy.Plan.SourceRoute.InstanceId, copy.Plan.TargetRoute.InstanceId }.Distinct(),
                (_, stop) => TransferCoordinator().PrepareTargetBirthAsync(operationId, stop), token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceTransferCopy> PublishNamespaceTransferTargetAsync(Guid operationId, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var copy = await ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            if (copy.TargetPublication is not null) return copy;
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(
                new[] { copy.Plan.SourceRoute.RootId, copy.Plan.TargetRoute.RootId }, token).ConfigureAwait(false);
            return await WithAdmittedInstancesLibraryAsync(new[] { copy.Plan.SourceRoute.InstanceId, copy.Plan.TargetRoute.InstanceId }.Distinct(),
                (_, stop) => TransferCoordinator().PublishTargetAsync(operationId, stop), token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceTransferCopy> DeleteNamespaceTransferSourceAsync(Guid operationId, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var copy = await ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            if (copy.SourceDeletion?.AcceptedAt is not null) return copy;
            if (copy.SourceDeletion is not null) throw new MirrorPulseMutationAmbiguousException("The original source delete needs its owned correlated acceptance before recovery.");
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(
                new[] { copy.Plan.SourceRoute.RootId, copy.Plan.TargetRoute.RootId }, token).ConfigureAwait(false);
            return await WithAdmittedInstancesLibraryAsync(new[] { copy.Plan.SourceRoute.InstanceId, copy.Plan.TargetRoute.InstanceId }.Distinct(),
                (_, stop) => TransferCoordinator().DeleteSourceAsync(operationId, transfers!, stop), token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceTransferCopy> RunTransferAsync(Guid operationId, bool verifyTarget, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var copy = await ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            if (verifyTarget ? copy.TargetVerification is not null : copy.CopyAcceptedAt is not null) return copy;
            if (transfers is null) throw new NotSupportedException("Transfers require connected normal-user Worker transports.");
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(
                new[] { copy.Plan.SourceRoute.RootId, copy.Plan.TargetRoute.RootId }, token).ConfigureAwait(false);
            return await WithAdmittedInstancesLibraryAsync(new[] { copy.Plan.SourceRoute.InstanceId, copy.Plan.TargetRoute.InstanceId }.Distinct(),
                (_, stop) => verifyTarget ? TransferCoordinator().VerifyTargetAsync(operationId, transfers.Stats, transfers.Ranges, transfers.Directories, stop) :
                    TransferCoordinator().CopyAsync(operationId, transfers.Uploads, stop), token).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseNamespaceTransferCopy> ReadNamespaceTransferAsync(Guid operationId, CancellationToken token) =>
            catalog is null ? throw new NotSupportedException("Transfer status requires the Host catalog.") :
                await catalog.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original transfer is not registered.");

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceCurrentTreeAsync(Guid operationId,
            CancellationToken cancellationToken) => RecoverNamespaceCurrentTreeCoreAsync(operationId, null, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceRootAsync(Guid operationId, RootId rootId,
            CancellationToken cancellationToken) => RecoverNamespaceCurrentTreeCoreAsync(operationId, rootId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceCurrentTreeCoreAsync(Guid operationId,
            RootId? selectedRoot, CancellationToken cancellationToken)
        {
            if (operationId == Guid.Empty || selectedRoot?.Value == Guid.Empty)
                throw new ArgumentException("Current recovery requires a nonempty operation and selected root identity.");
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            return await RunNamespaceInitializationRecoveryAsync(operationId, MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory,
                selectedRoot, async (admission, instances, token) =>
            {
                if (selectedRoot is not null && _scopedAnchorProtection is null &&
                    await catalog!.ReadNamespaceInitializationCompletionAsync(operationId, token).ConfigureAwait(false) is null)
                    _scopedAnchorProtection = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router!,
                        _namespaceExecution!, _namespaceActor!, _permissions!).ProtectAsync(token).ConfigureAwait(false);
                var coordinator = new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, instances, _permissions!);
                var result = selectedRoot is { } root
                    ? await coordinator.RecoverRootAsync(operationId, root, token).ConfigureAwait(false)
                    : await coordinator.RecoverAsync(operationId, token).ConfigureAwait(false);
                if (!result.IsHistoricalReplay)
                {
                    var prior = _initializationProtection; _initializationProtection = result;
                    if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
                    admission?.CompleteAfterFreshAudit();
                }
                return result.Completion;
            }, cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceInitializationCompletion> InitializeNamespaceProtectionAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            return await RunNamespaceInitializationRecoveryAsync(operationId, MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory,
                null, async (admission, instances, token) =>
            {
                var result = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, instances, _permissions!).InitializeAsync(operationId, token).ConfigureAwait(false);
                if (!result.IsHistoricalReplay)
                {
                    var prior = _initializationProtection; _initializationProtection = result;
                    if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
                    admission?.CompleteAfterFreshAudit();
                }
                return result.Completion;
            }, cancellationToken).ConfigureAwait(false);
        }

        private sealed record InitializationWorkRecovery(MirrorPulseNamespaceInitializationIntent? Retained,
            IReadOnlyList<RootRegistration> Registrations, IReadOnlyList<MirrorPulseNamespaceInitializationRoot>? PlannedRoots);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceInitializationCompletion> RunNamespaceInitializationRecoveryAsync(Guid operationId,
            MirrorPulseNamespaceInitializationInventoryKind inventoryKind, RootId? selectedRoot,
            Func<MirrorPulseNamespaceWorkAdmission.RecoveryLease?, IMirrorPulseInstanceScheduler, CancellationToken,
                ValueTask<MirrorPulseNamespaceInitializationCompletion>> recover, CancellationToken token)
        {
            using var admissionToken = CancellationTokenSource.CreateLinkedTokenSource(token, _admissionShutdown.Token);
            return await MirrorPulseRuntimeInstanceDispatcher.RunRecoveryAsync(_recoveryRequests, _scheduler,
                router!.Registrations.Select(root => root.InstanceId).Distinct(),
                stop => WithCurrentLibraryAsync(current => PrepareInitializationWorkRecoveryAsync(operationId, inventoryKind, current, selectedRoot), stop),
                (preparation, admission, instances, stop) => WithCurrentLibraryAsync(async current =>
                {
                    if (admission is not null)
                        await FinishInitializationWorkRecoveryAsync(operationId, inventoryKind, selectedRoot, preparation, instances, current).ConfigureAwait(false);
                    return await recover(admission, instances, current).ConfigureAwait(false);
                }, stop), admissionToken.Token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<(InitializationWorkRecovery Preparation, Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> Drain)>
            PrepareInitializationWorkRecoveryAsync(Guid operationId,
            MirrorPulseNamespaceInitializationInventoryKind inventoryKind, CancellationToken token, RootId? selectedRoot = null)
        {
            var retained = await catalog!.ReadNamespaceInitializationAsync(operationId, token).ConfigureAwait(false);
            if (retained?.RequestedInventoryKind is { } requested && requested != inventoryKind)
                throw new InvalidOperationException("The retained initialization requires its originally selected recovery route.");
            if (retained is not null && (selectedRoot is { } selected ? retained.Version != 4 || retained.Roots.Count != 1 ||
                retained.Roots[0].RootId != selected : retained.Version == 4))
                throw new InvalidOperationException("The retained initializer requires its explicit complete or selected-root recovery route.");
            var registrations = router!.Registrations.Where(root => selectedRoot is null || root.RootId == selectedRoot.Value)
                .OrderBy(root => root.RootId.Value).ToArray();
            if (namespaceWork is null || await catalog!.ReadNamespaceInitializationCompletionAsync(operationId, token).ConfigureAwait(false) is not null)
                return (new(retained, registrations, null), Task.FromResult<MirrorPulseNamespaceWorkAdmission.RecoveryLease?>(null));
            if (selectedRoot is not null && registrations.Length != 1)
                throw new InvalidOperationException("The selected recovery root is not configured.");
            if (retained is not null && !retained.Roots.Select(root => root.RootId).ToHashSet().SetEquals(registrations.Select(root => root.RootId)))
                throw new InvalidDataException("The initialization root layout changed after admission.");
            IReadOnlyList<MirrorPulseNamespaceInitializationRoot>? plannedRoots = null;
            if (retained is null)
            {
                plannedRoots = inventoryKind == MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory
                    ? await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog, registrations, token).ConfigureAwait(false)
                    : await MirrorPulseNamespaceInitializationCoordinator.SelectOriginalRootsAsync(catalog, registrations, false, token).ConfigureAwait(false);
                if (inventoryKind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
                    await MirrorPulseNamespaceInitializationCoordinator.RequireOriginalGraphAsync(catalog, registrations, plannedRoots, token).ConfigureAwait(false);
            }
            // Calling BeginRecovery closes the selected roots synchronously. Its asynchronous
            // tail is awaited only after releasing the library needed by accepted work.
            static async Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> DrainAsync(Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease> drain) =>
                await drain.ConfigureAwait(false);
            return (new(retained, registrations, plannedRoots), DrainAsync(namespaceWork.BeginRecoveryAsync(
                registrations.Select(root => root.RootId), token).AsTask()));
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async Task FinishInitializationWorkRecoveryAsync(Guid operationId,
            MirrorPulseNamespaceInitializationInventoryKind inventoryKind, RootId? selectedRoot, InitializationWorkRecovery preparation,
            IMirrorPulseInstanceScheduler instances, CancellationToken token)
        {
            if (preparation.Retained is null)
            {
                var selectedRoots = preparation.PlannedRoots ?? throw new InvalidDataException("The initializer has no selected root provenance.");
                if (inventoryKind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
                    await MirrorPulseNamespaceInitializationCoordinator.RequireOriginalGraphAsync(catalog!, preparation.Registrations, selectedRoots, token).ConfigureAwait(false);
                else if (!selectedRoots.SequenceEqual(await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog!, preparation.Registrations, token).ConfigureAwait(false)))
                    throw new InvalidDataException("Current root provenance changed while earlier work drained.");
                await catalog!.PrepareNamespaceInitializationAsync(new(selectedRoot is not null ? 4 : selectedRoots.Count == 0 ? 3 : selectedRoots.Any(root => root.BornRootMemberId is not null) ? 2 : 1,
                    operationId, selectedRoots, DateTimeOffset.UtcNow)
                { RequestedInventoryKind = inventoryKind }, token).ConfigureAwait(false);
            }
            await ReopenLibraryForNamespaceRecoveryAsync(instances, token).ConfigureAwait(false);
        }

        private async Task ReopenLibraryForNamespaceRecoveryAsync(IMirrorPulseInstanceScheduler instances, CancellationToken token)
        {
            // Application leases do not cover the native transfer and official projection
            // after FetchChildren returns. Public disposal drains that complete library tail.
            if (_uploadPump is not null)
            {
                await _uploadPump.DisposeAsync().ConfigureAwait(false);
                _uploadPump = null;
            }
            _conflictActions = null; _remoteConflictActions = null; _rootRenames = null;
            await fileSystem.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var paths = new MirrorPulseStoragePaths(syncRootPath, dataRootPath);
            state = new(paths);
            fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(stateStoreDecorator?.Invoke(state) ?? state)
                .WithContentProvider(provider).Build();
            await StartLibraryAsync(token).ConfigureAwait(false);
            await StartLibraryServicesAsync(token, instances).ConfigureAwait(false);
        }

        private async ValueTask<T> WithCurrentLibraryAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            using var admissionToken = CancellationTokenSource.CreateLinkedTokenSource(token, _admissionShutdown.Token);
            await _initializationGate.WaitAsync(admissionToken.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                return await operation(token).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
        }

        private async ValueTask<T> WithInstanceLibraryAsync<T>(InstanceId instance, Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken token)
        {
            using var admissionToken = CancellationTokenSource.CreateLinkedTokenSource(token, _admissionShutdown.Token);
            return await MirrorPulseRuntimeInstanceDispatcher.RunAsync(_scheduler, instance,
                WithCurrentLibraryAsync, operation, admissionToken.Token).ConfigureAwait(false);
        }

        private ValueTask<T> WithAdmittedInstanceLibraryAsync<T>(InstanceId instance,
            Func<IMirrorPulseInstanceScheduler, CancellationToken, ValueTask<T>> operation, CancellationToken token) =>
            WithAdmittedInstancesLibraryAsync([instance], operation, token);

        private async ValueTask<T> WithAdmittedInstancesLibraryAsync<T>(IEnumerable<InstanceId> instances,
            Func<IMirrorPulseInstanceScheduler, CancellationToken, ValueTask<T>> operation, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            using var admissionToken = CancellationTokenSource.CreateLinkedTokenSource(token, _admissionShutdown.Token);
            await using var admitted = await _scheduler.AcquireAsync(instances, admissionToken.Token).ConfigureAwait(false);
            return await WithCurrentLibraryAsync(current => operation(admitted, current), admissionToken.Token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<T> WithMemberRecoveryLibraryAsync<T>(Guid memberId,
            Func<IMirrorPulseInstanceScheduler, CancellationToken, ValueTask<T>> operation, CancellationToken token)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var member = await catalog!.ReadNamespaceMemberAsync(memberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The recovery member is not registered.");
            var root = router!.Registrations.Single(candidate => candidate.RootId == member.RootId);
            return await WithAdmittedInstanceLibraryAsync(root.InstanceId, operation, token).ConfigureAwait(false);
        }

        public ValueTask<MirrorPulseNamespaceInitializationStatus> ReadNamespaceInitializationStatusAsync(Guid operationId,
            CancellationToken cancellationToken) => catalog is null ? ValueTask.FromException<MirrorPulseNamespaceInitializationStatus>(
                new NotSupportedException("Initialization status requires the Host catalog.")) :
                new(catalog.ReadNamespaceInitializationStatusAsync(operationId, cancellationToken));

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseProtectedNamespacePermissionResult> RecoverNamespaceOriginalMemberAsync(Guid initializationOperationId,
            Guid memberId, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            return await WithMemberRecoveryLibraryAsync(memberId, (instances, token) =>
                new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, instances, _permissions!).RecoverAsync(initializationOperationId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<MirrorPulseNamespacePermissionTree>> ReadNamespacePermissionTreesAsync(RootId rootId,
            Guid? afterManifestId, int limit, CancellationToken cancellationToken) =>
            catalog is null ? ValueTask.FromException<IReadOnlyList<MirrorPulseNamespacePermissionTree>>(
                new NotSupportedException("Original permission capture discovery requires the Host catalog.")) :
                new(catalog.ReadNamespacePermissionTreesAsync(rootId, afterManifestId, limit, cancellationToken));

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceBornMemberRecoveryResult> RecoverNamespaceBornMemberAsync(Guid initializationOperationId,
            Guid actionId, Guid memberId, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            return await WithMemberRecoveryLibraryAsync(memberId, (instances, token) =>
                new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, instances, _permissions!).RecoverAsync(initializationOperationId, actionId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceBirthProtection> VerifyNamespaceBornMemberRecoveryAsync(Guid initializationOperationId,
            Guid protectionId, Guid memberId, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            return await WithMemberRecoveryLibraryAsync(memberId, (instances, token) =>
                new MirrorPulseNamespaceBornMemberReadbackCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, instances).VerifyAsync(initializationOperationId, protectionId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespacePermissionTreeResult> RecoverNamespaceOriginalTreeAsync(Guid manifestId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var tree = await catalog!.ReadNamespacePermissionTreeAsync(manifestId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original recovery tree is not registered.");
            var root = router!.Registrations.Single(candidate => candidate.RootId == tree.Definition.Anchor.Intent.RootId);
            return await WithAdmittedInstanceLibraryAsync(root.InstanceId, (instances, token) =>
                new MirrorPulseNamespaceOriginalTreeRecoveryCoordinator(fileSystem, catalog!, router!,
                    _namespaceExecution!, _namespaceActor!, instances, _permissions!).RecoverAsync(manifestId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceMemberRegistration> CreateNamespaceMemberAsync(Guid operationId,
            Guid parentMemberId, string name, bool isDirectory, CancellationToken cancellationToken) =>
            CreateNamespaceMemberCoreAsync(operationId, parentMemberId, name, isDirectory, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceMemberRegistration> CreateNamespaceMemberCoreAsync(Guid operationId,
            Guid parentMemberId, string name, bool isDirectory, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled creation requires the Host namespace boundary.");
            var parent = await catalog.ReadNamespaceMemberAsync(parentMemberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The creation parent is not registered.");
            if (parent.RootId is null) throw new InvalidOperationException("Creation requires an Adapter's managed root.");
            var registration = router.Registrations.Single(root => root.RootId == parent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor)
                    .CreateAsync(operationId, parentMemberId, name, isDirectory, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
            string name, string sourcePath, CancellationToken cancellationToken) =>
            PrepareNamespaceImportCoreAsync(operationId, parentMemberId, name, sourcePath, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportCoreAsync(Guid operationId, Guid parentMemberId,
            string name, string sourcePath, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            RequireImportBoundary();
            if (operationId == Guid.Empty || parentMemberId == Guid.Empty)
                throw new ArgumentException("Operation and parent member IDs are required.");
            var parent = await catalog!.ReadNamespaceMemberAsync(parentMemberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The import parent is not registered.");
            var registration = router!.Registrations.Single(root => root.RootId == parent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            // A source may hydrate through this SDK owner. Retain it while capturing input,
            // then release that scope before waiting for the instance's execution admission.
            var content = await WithCurrentLibraryAsync(async token => await _namespaceExecution!.RunNormalUserOperationAsync(
                () => inputs.StageAsync(operationId, sourcePath, token)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await using var input = await _namespaceExecution!.RunNormalUserOperationAsync(
                () => inputs.OpenLeaseAsync(operationId, content, cancellationToken)).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor!)
                    .PrepareImportAsync(operationId, parentMemberId, name, input, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportAsync(Guid operationId,
            CancellationToken cancellationToken) => ExecuteNamespaceImportCoreAsync(operationId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportCoreAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            RequireImportBoundary();
            var status = await ReadNamespaceImportStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
            var birth = (await catalog!.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false))!;
            var registration = router!.Registrations.Single(root => root.RootId == birth.RootId);
            await using var admission = namespaceWork is null ? null :
                await namespaceWork.EnterPendingOperationAsync(registration.RootId, cancellationToken).ConfigureAwait(false);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            MirrorPulseNamespaceMemberRegistration result;
            if (!status.ContentRecorded && !status.Registered)
            {
                await using var input = await _namespaceExecution!.RunNormalUserOperationAsync(
                    () => inputs.OpenLeaseAsync(operationId, new(status.Length, status.Sha256), cancellationToken)).ConfigureAwait(false);
                result = await WithInstanceLibraryAsync(registration.InstanceId, token =>
                    new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution!, _namespaceActor!)
                        .ExecuteImportAsync(operationId, input, token), cancellationToken).ConfigureAwait(false);
            }
            else
                result = await WithInstanceLibraryAsync(registration.InstanceId, token =>
                    new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution!, _namespaceActor!)
                        .ExecuteImportAsync(operationId, null, token), cancellationToken).ConfigureAwait(false);
            // Membership is durable before reclaiming input; closing its lease precedes deletion.
            await _namespaceExecution!.RunNormalUserOperationAsync(() => { inputs.RemoveCompleted(operationId); return Task.CompletedTask; }).ConfigureAwait(false);
            return result;
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceImportStatus> ReadNamespaceImportStatusAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            // Large imports retain the actor while copying. Historical status must remain queryable then.
            return new(catalog!.ReadNamespaceImportStatusAsync(operationId, cancellationToken));
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private void RequireImportBoundary()
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled imports require the Host namespace boundary.");
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementAsync(Guid operationId, Guid memberId,
            string sourcePath, CancellationToken cancellationToken) =>
            PrepareNamespaceReplacementCoreAsync(operationId, memberId, sourcePath, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementCoreAsync(Guid operationId, Guid memberId,
            string sourcePath, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var historical = await catalog!.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (historical is not null)
            {
                if (historical.Intent.MemberId != memberId || historical.Action is null)
                    throw new InvalidOperationException("The existing replacement admission cannot replay this preparation.");
                return historical;
            }
            var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The replacement member is not registered.");
            if (original.Context.Member.IsDirectory || original.Context.Member.RootId is not { } rootId)
                throw new InvalidOperationException("Replacement requires a managed root file.");
            var root = router!.Registrations.Single(candidate => candidate.RootId == rootId);
            using var admission = namespaceWork?.Enter([rootId]);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            var materialized = await WithCurrentLibraryAsync(token => new MirrorPulseNamespaceReplacementPreparationCoordinator(fileSystem,
                state, catalog, router, _namespaceExecution!, _namespaceActor!, _scheduler, inputs, mayDispatch ?? (_ => false))
                .MaterializeAsync(operationId, original, sourcePath, token), cancellationToken).ConfigureAwait(false);
            return await WithAdmittedInstanceLibraryAsync(root.InstanceId, (instances, token) =>
                new MirrorPulseNamespaceReplacementPreparationCoordinator(fileSystem, state, catalog, router, _namespaceExecution!,
                    _namespaceActor!, instances, inputs, mayDispatch ?? (_ => false))
                    .PrepareMaterializedAsync(operationId, memberId, materialized, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteNamespaceReplacementAsync(Guid operationId,
            CancellationToken cancellationToken) => ExecuteNamespaceReplacementCoreAsync(operationId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteNamespaceReplacementCoreAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var intent = await catalog!.ReadNamespaceReplacementAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The replacement admission is not registered.");
            await using var admission = namespaceWork is null ? null :
                await namespaceWork.EnterPendingOperationAsync(intent.RootId, cancellationToken).ConfigureAwait(false);
            var paths = new MirrorPulseStoragePaths(syncRootPath, dataRootPath);
            var inputs = new MirrorPulseLocalImportStagingStore(paths);
            var root = router!.Registrations.Single(candidate => candidate.RootId == intent.RootId);
            var result = await WithAdmittedInstanceLibraryAsync(root.InstanceId, (instances, token) =>
                new MirrorPulseNamespaceReplacementExecutor(fileSystem, catalog!, router, _namespaceExecution!,
                    _namespaceActor!, instances, inputs, new(paths)).ExecuteAsync(operationId, token), cancellationToken).ConfigureAwait(false);
            await _namespaceExecution!.RunNormalUserOperationAsync(() => { inputs.RemoveCompleted(operationId); return Task.CompletedTask; }).ConfigureAwait(false);
            return result;
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceReplacementStatus> ReadNamespaceReplacementStatusAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Replacement status requires the Host catalog.");
            return new(catalog.ReadNamespaceReplacementStatusAsync(operationId, cancellationToken));
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberInspection> InspectNamespaceMemberAsync(string relativePath,
            bool isDirectory, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Membership inspection requires the Host namespace boundary.");
            return await WithCurrentLibraryAsync(token => new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                .InspectAsync(relativePath, isDirectory, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeAsync(Guid memberId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Namespace tree reads require the Host catalog.");
            return await catalog.ReadNamespaceMemberTreeAsync(memberId, cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveAsync(Guid operationId, Guid memberId,
            Guid targetParentMemberId, string targetName, CancellationToken cancellationToken) =>
            PrepareNamespaceMemberMoveCoreAsync(operationId, memberId, targetParentMemberId, targetName, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveCoreAsync(Guid operationId, Guid memberId,
            Guid targetParentMemberId, string targetName, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled move preparation requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The source member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor)
                    .PrepareAsync(operationId, memberId, targetParentMemberId, targetName, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteNamespaceMemberMoveAsync(Guid operationId,
            CancellationToken cancellationToken) => ExecuteNamespaceMemberMoveCoreAsync(operationId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteNamespaceMemberMoveCoreAsync(
            Guid operationId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled member moves require the Host namespace boundary.");
            var intent = await catalog.ReadNamespaceMemberMoveAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The move admission is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == intent.RootId);
            await using var admission = namespaceWork is null ? null :
                await namespaceWork.EnterPendingOperationAsync(registration.RootId, cancellationToken).ConfigureAwait(false);
            // Drain earlier instance work before entering the namespace actor. Subsequent dispatch
            // rechecks the durable root fence after acquiring this same scheduler.
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor)
                    .ExecuteAsync(operationId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberMoveStatus> ReadNamespaceMemberMoveStatusAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Move status requires the Host catalog.");
            return await catalog.ReadNamespaceMemberMoveStatusAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The move operation is not registered.");
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceRetirement> PrepareNamespaceMemberRetirementAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken) =>
            PrepareNamespaceMemberRetirementCoreAsync(operationId, memberId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceRetirement> PrepareNamespaceMemberRetirementCoreAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled retirement preparation requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                    .PrepareAsync(operationId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceRetirement> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
            CancellationToken cancellationToken) => ExecuteNamespaceMemberRetirementCoreAsync(operationId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceRetirement> ExecuteNamespaceMemberRetirementCoreAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled retirements require the Host namespace boundary.");
            var retirement = await catalog.ReadNamespaceRetirementAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement admission is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == retirement.Intent.RootId);
            await using var admission = namespaceWork is null ? null :
                await namespaceWork.EnterPendingOperationAsync(registration.RootId, cancellationToken).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                    .ExecuteAsync(operationId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceRetirement> ReadNamespaceMemberRetirementAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Retirement receipts require the Host catalog.");
            return await catalog.ReadNamespaceRetirementAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement operation is not registered.");
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken) =>
            PrepareNamespaceRetirementBatchCoreAsync(operationId, memberId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchCoreAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Subtree retirement requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                    .PrepareBatchAsync(operationId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteNamespaceRetirementBatchAsync(Guid operationId,
            CancellationToken cancellationToken) => ExecuteNamespaceRetirementBatchCoreAsync(operationId, cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteNamespaceRetirementBatchCoreAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Subtree retirement requires the Host namespace boundary.");
            var batch = await catalog.ReadNamespaceRetirementBatchAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == batch.RootId);
            await using var admission = namespaceWork is null ? null :
                await namespaceWork.EnterPendingOperationAsync(registration.RootId, cancellationToken).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(registration.InstanceId, token =>
                new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                    .ExecuteBatchAsync(operationId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceRetirementBatch> ReadNamespaceRetirementBatchAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Subtree receipts require the Host catalog.");
            var batch = await catalog.ReadNamespaceRetirementBatchAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is not registered.");
            return MirrorPulseNamespaceRetirementCoordinator.RequireControlledBatchReceipt(batch);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceBirthRecoveryResult> ExecuteNamespaceBirthRecoveryAsync(
            Guid recoveryId, CancellationToken requestCancellation)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Staged birth recovery requires the Host namespace boundary.");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var action = await catalog.ReadNamespaceBirthRecoveryActionAsync(recoveryId, requestCancellation).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The recovery action is not registered.");
            var birth = await catalog.ReadNamespaceBirthAsync(action.BirthOperationId, requestCancellation).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The birth admission is not registered.");
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(birth.RootId, requestCancellation).ConfigureAwait(false);
            var registration = router.Registrations.Single(root => root.RootId == birth.RootId);
            return await WithInstanceLibraryAsync(registration.InstanceId, stop => _namespaceActor.RunAsync(async token =>
            {
                int separator = birth.RelativePath.LastIndexOf('/');
                string parentPath = separator < 0 ? string.Empty : birth.RelativePath[..separator];
                CloudFile file = fileSystem.GetFile(birth.RelativePath);
                CloudDirectory parent = parentPath.Length == 0 ? fileSystem.Root : fileSystem.GetDirectory(parentPath);
                var coordinator = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, _namespaceExecution);
                _namespaceActor.AssertAccess();
                MirrorPulseNamespaceBirthRecoveryResult result = null!;
                await _namespaceExecution.RunNamespaceOperationAsync(async () =>
                {
                    result = await coordinator.ExecuteStagedFileAsync(recoveryId, file, parent, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return result;
            }, stop), requestCancellation).ConfigureAwait(false);
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _providerDiagnostics = new(Path.Combine(dataRootPath, "logs", "cloud-files"));
            NoteStartup("LibraryStartBegin");
            await StartLibraryAsync(cancellationToken).ConfigureAwait(false);
            NoteStartup("LibraryStartCompleted");
            NoteStartup("LibraryServicesBegin");
            await StartLibraryServicesAsync(cancellationToken).ConfigureAwait(false);
            NoteStartup("LibraryServicesCompleted");
            if (initializeNamespaceOnStart)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                    throw new PlatformNotSupportedException("Automatic namespace startup requires Windows 11 24H2 or later.");
                NoteStartup("NamespaceStartupBegin");
                await WithAdmittedInstancesLibraryAsync(router!.Registrations.Select(root => root.InstanceId).Distinct(),
                    BootstrapNamespaceAsync, cancellationToken).ConfigureAwait(false);
                NoteStartup("NamespaceStartupCompleted");
            }
            if (catalog is not null && router is not null && conflicts is not null && notifications is not null)
            {
                _retainedRecovery = new(catalog, mayDispatch, (instance, token) => WithInstanceLibraryAsync(instance,
                    current => RetainedRemoteBatches(catalog, conflicts, notifications).ResumeAsync(instance, current), token));
                NoteStartup("RetainedRemoteRecoveryBegin");
                await _retainedRecovery.RecoverOnceAsync(cancellationToken).ConfigureAwait(false);
                NoteStartup("RetainedRemoteRecoveryCompleted");
                _retainedRecovery.Start();
            }
            NoteStartup("Started");
        }

        private void NoteStartup(string stage)
        {
            long now = Stopwatch.GetTimestamp();
            long elapsed = _startupCheckpoint == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_startupCheckpoint, now).TotalMilliseconds;
            _startupCheckpoint = now;
            _providerDiagnostics?.RecordStartup(stage, ++_startupSequence, elapsed);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<bool> BootstrapNamespaceAsync(IMirrorPulseInstanceScheduler instances, CancellationToken token)
        {
            // Reconcile already committed SDK update outcomes before rotating the old
            // coherent role graph. Uncertain starts retain their original closed root.
            var updateRecovery = new MirrorPulseRemoteUpdateCoordinator(fileSystem, state, catalog!, router!, _namespaceExecution!, _namespaceActor!);
            foreach (var root in router!.Registrations)
                foreach (var update in await catalog!.ReadPendingNamespaceRemoteUpdatesAsync(root.RootId, token).ConfigureAwait(false))
                {
                    try { await updateRecovery.SettleRecordedAsync(update, null, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        // Startup's validated pending-operation discovery leaves this root
                        // closed without replacing its owner with a new initializer.
                    }
                }
            var started = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                _namespaceActor!, instances, _permissions!, namespaceWork!, NoteStartup, RecoverRemoteDeleteBeforeAuditAsync).StartAsync(token).ConfigureAwait(false);
            _startupProtection = started.Guard; NamespaceStartupResults = started.Roots;
            return true;
        }

        private async ValueTask RecoverRemoteDeleteBeforeAuditAsync(RootRegistration root, CancellationToken token)
        {
            var pending = await catalog!.ReadPendingRemoteDeleteResolutionsAsync(root.RootId, limit: 1, token: token).ConfigureAwait(false);
            if (pending.Count == 0) return;
            var intent = pending[0].Plan.Decision;
            try
            {
                _ = await (_remoteConflictActions ?? throw new InvalidOperationException("The protected remote conflict service has not started."))
                    .ApplyAsync(intent.CommandId, intent.Conflict, intent.Action, cancellationToken: token).ConfigureAwait(false);
            }
            catch (KeyNotFoundException error)
            {
                throw new InvalidDataException("The admitted remote deletion requires its original SDK outcome before startup can continue.", error);
            }
            if ((await catalog.ReadPendingRemoteDeleteResolutionsAsync(root.RootId, limit: 1, token: token).ConfigureAwait(false)).Count != 0)
                throw new IOException("The root still has a bounded explicit Delete recovery page before its current audit.");
        }

        private async Task StartLibraryAsync(CancellationToken cancellationToken)
        {
            if (namespaceWork is not null && provider is MirrorPulseDemandProvider demand)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                    throw new PlatformNotSupportedException("Protected source population requires Windows 11 24H2 or later.");
                RequireImportBoundary();
                demand.NamespacePublisher = new(() => fileSystem, catalog!, router!, _namespaceExecution!, _namespaceActor!);
            }
            if (_namespaceExecution is not null)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                    throw new PlatformNotSupportedException("Host namespace execution requires Windows 11 24H2 or later.");
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, _namespaceExecution, cancellationToken).ConfigureAwait(false);
            }
            else
                await fileSystem.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task StartLibraryServicesAsync(CancellationToken cancellationToken, IMirrorPulseInstanceScheduler? admittedInstances = null)
        {
            if (catalog is not null && router is not null)
            {
                _rootRenames = new(catalog, _scheduler, (operation, token) => WithCurrentLibraryAsync(
                    current => new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router).RecoverAsync(operation, current), token));
                // Recover original namespace evidence before upload dispatch starts.
                // Incomplete and legacy records keep their per-root durable fence.
                var restoration = admittedInstances is null ? _rootRenames : new MirrorPulseManagedRootRenameService(catalog, admittedInstances,
                    (operation, token) => new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router).RecoverAsync(operation, token));
                RootRenameRecoveryResults = await restoration.RestorePendingAsync(cancellationToken).ConfigureAwait(false);
            }
            if (catalog is not null && conflicts is not null && notifications is not null)
                await new MirrorPulseRemoteConflictProjector(fileSystem, catalog, conflicts, notifications)
                    .RestoreAsync(cancellationToken).ConfigureAwait(false);
            if (uploads is not null && stats is not null && router is not null &&
                catalog is not null && mayDispatch is not null)
            {
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                var completion = new MirrorPulseJournalUploadCompletion(
                    feed, state, new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)));
                _conflictActions = new MirrorPulseUploadConflictActions(catalog, state, feed,
                    new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)),
                    new MirrorPulseStoragePaths(syncRootPath, dataRootPath), router, mutations, conflicts, _scheduler,
                    fileSystem, _namespaceActor, _namespaceExecution, namespaceWork, stats, mayDispatch, notifications,
                    uploads as IMirrorPulseWorkerRangeTransport, uploads as IMirrorPulseWorkerDirectoryPageSource);
                _uploadPump = new MirrorPulseJournalUploadPump(feed, router, catalog, uploads, stats, state,
                    syncRootPath, dataRootPath, mayDispatch, completion, conflicts, notifications, mutations,
                    uploads as IMirrorPulseWorkerRangeTransport, uploads as IMirrorPulseWorkerDirectoryPageSource, fileSystem, _scheduler,
                    _namespaceActor, _namespaceExecution, namespaceWork, _conflictActions);
                await _uploadPump.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            if (catalog is not null && conflicts is not null)
            {
                Func<MirrorPulseRemoteConflictActionIntent, CancellationToken, ValueTask<CloudRemoteApplyEntryStatus>>? resolveOwned = null;
                if (router is not null && _namespaceExecution is not null && _namespaceActor is not null)
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                        throw new PlatformNotSupportedException("Protected remote conflict decisions require Windows 11 24H2 or later.");
                    resolveOwned = new MirrorPulseRemoteDeleteResolutionCoordinator(fileSystem, state, catalog, router,
                        _namespaceExecution, _namespaceActor).ResolveAsync;
                }
                _remoteConflictActions = MirrorPulseRemoteConflictActions.For(fileSystem,
                    new MirrorPulseConflictCopyStore(new MirrorPulseStoragePaths(syncRootPath, dataRootPath)),
                    catalog, conflicts, resolveOwned);
            }
        }

        public async ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
            Guid operationId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var service = _rootRenames ?? throw new NotSupportedException("The managed root recovery service is unavailable.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _admissionShutdown.Token);
            var intent = (await catalog!.ReadManagedRootRenameHistoryAsync(stop.Token).ConfigureAwait(false))
                .SingleOrDefault(history => history.Intent.OperationId == operationId)?.Intent
                ?? throw new FileNotFoundException("The root rename history is not registered.");
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(intent.RootId, stop.Token).ConfigureAwait(false);
            return await service.RecoverAsync(operationId, stop.Token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseRootRenameHistory> PrepareManagedRootRenameAsync(RootId rootId, string label,
            CancellationToken requestCancellation)
        {
            if (router is null || catalog is null) throw new NotSupportedException("Managed root rename requires the Host namespace boundary.");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var root = (await catalog.ReadAdapterTopologyAsync(requestCancellation).ConfigureAwait(false)).Roots.Single(item => item.RootId == rootId);
            using var admission = namespaceWork?.Enter([rootId]);
            return await WithInstanceLibraryAsync(root.InstanceId, token =>
                new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router).PrepareAsync(rootId, label, token), requestCancellation).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            var service = _conflictActions ?? throw new InvalidOperationException("The upload conflict action service has not started.");
            await using var admission = await service.RetainRootAsync(conflictId, action, cancellationToken).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(admission.Conflict.InstanceId, token =>
                (_conflictActions ?? throw new InvalidOperationException("The upload conflict action service has not started."))
                    .ApplyRetainedAsync(admission, action, token), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseRemoteDeleteResolution> RecoverRemoteDeleteResolutionAsync(Guid commandId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected remote deletion recovery requires Windows 11 24H2 or later.");
            RequireImportBoundary();
            var retained = await catalog!.ReadRemoteDeleteResolutionAsync(commandId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original explicit Delete command is not registered.");
            var intent = retained.Plan.Decision;
            var root = retained.Plan.Entries[0].Original.Context.Member.RootId!.Value;
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(root, cancellationToken).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(intent.Conflict.InstanceId, async token =>
            {
                try
                {
                    _ = await (_remoteConflictActions ?? throw new InvalidOperationException("The remote conflict action service has not started."))
                        .ApplyAsync(intent.CommandId, intent.Conflict, intent.Action, cancellationToken: token).ConfigureAwait(false);
                }
                catch (MirrorPulseRemoteTreeSettlementPendingException pending) when (pending.OperationId == commandId)
                {
                    // One actual member page committed; return its retained progress to the caller.
                }
                return await catalog.ReadRemoteDeleteResolutionAsync(commandId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The continued explicit Delete lost its original command.");
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseNamespaceRemoteUpdate> RecoverRemoteUpdateAsync(Guid operationId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected remote update recovery requires Windows 11 24H2 or later.");
            RequireImportBoundary();
            var retained = await catalog!.ReadNamespaceRemoteUpdateAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote update operation is not registered.");
            if (retained.Outcome is not null || retained.StartedAt is null) return retained;
            var root = retained.Plan.Child.Context.Member.RootId!.Value;
            await using var admission = namespaceWork is null ? null : await namespaceWork.EnterPendingOperationAsync(root, cancellationToken).ConfigureAwait(false);
            return await WithInstanceLibraryAsync(retained.Plan.InstanceId, async token =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                    throw new PlatformNotSupportedException("Protected remote update recovery requires Windows 11 24H2 or later.");
                var projector = conflicts is null || notifications is null ? null :
                    new MirrorPulseRemoteConflictProjector(fileSystem, catalog, conflicts, notifications);
                await new MirrorPulseRemoteUpdateCoordinator(fileSystem, state, catalog, router!, _namespaceExecution!, _namespaceActor!)
                    .SettleRecordedAsync(retained, projector, token).ConfigureAwait(false);
                return await catalog.ReadNamespaceRemoteUpdateAsync(operationId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovered remote update lost its original operation.");
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            MirrorPulseProductCatalog catalog,
            CancellationToken requestCancellation)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            MirrorPulseConflictRecord conflict = (await catalog.ReadRemoteConflictProjectionsAsync(
                requestCancellation).ConfigureAwait(false)).SingleOrDefault(item =>
                    item.ConflictId == conflictId) ?? throw new FileNotFoundException(
                        "The remote conflict was not found.");
            var originalDelete = await catalog.ReadRemoteDeleteResolutionForConflictAsync(conflictId, requestCancellation).ConfigureAwait(false);
            if (originalDelete is not null && (action != MirrorPulseConflictAction.KeepRemote || originalDelete.Plan.Decision.Conflict != conflict))
                throw new InvalidOperationException("This conflict already has an original protected Delete decision requiring its own continuation.");
            Guid commandId = originalDelete?.Plan.Decision.CommandId ?? Guid.NewGuid();
            MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
            IAsyncDisposable? pendingAdmission = null;
            if (namespaceWork is not null)
            {
                var route = router!.ResolvePath(conflict.RelativePath);
                if (route.InstanceId != conflict.InstanceId) throw new InvalidDataException("The conflict belongs to another routed instance.");
                var rootId = router.GetRegistration(route.InstanceId, route.RootKey).RootId;
                if (originalDelete is null) admission = namespaceWork.Enter([rootId]);
                else pendingAdmission = await namespaceWork.EnterPendingOperationAsync(rootId, requestCancellation).ConfigureAwait(false);
            }
            using (admission)
            await using (pendingAdmission)
                return await WithInstanceLibraryAsync(conflict.InstanceId, async cancellationToken =>
                {
                    MirrorPulseRemoteConflictActions actions = _remoteConflictActions ?? throw new InvalidOperationException(
                        "The remote conflict action service has not started.");
                    return await actions.ApplyAsync(commandId, conflict, action,
                        action == MirrorPulseConflictAction.KeepBoth
                            ? MirrorPulseConflictPreservedSide.Local
                            : null, cancellationToken: cancellationToken).ConfigureAwait(false);
                }, requestCancellation).ConfigureAwait(false);
        }

        public ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
            IEnumerable<InstanceId> instanceIds,
            CancellationToken cancellationToken)
            => WithCurrentLibraryAsync(async token => await MirrorPulseCloudStatusReader.ReadAsync(
                state.OpenStore, instanceIds, router?.Registrations ?? [], token).ConfigureAwait(false), cancellationToken);

        public ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
            InstanceId instanceId,
            CloudRemoteChangeBatch batch,
            MirrorPulseProductCatalog catalog,
            MirrorPulseConflictCenter center,
            MirrorPulseConflictNotificationBridge notifications,
            CloudRemoteApplyOptions? options,
            CancellationToken cancellationToken)
            => WithCurrentLibraryAsync(async token =>
            {
                if (router is null) return await ApplyRemoteBatchCoreAsync(instanceId, batch, catalog, center, notifications, options, token).ConfigureAwait(false);
                CloudRemoteApplyResult? result = null;
                var retained = new MirrorPulseRetainedRemoteBatchCoordinator(catalog, state.OpenStore, router,
                    async (instance, original, policy, stop) =>
                    {
                        result = await ApplyRemoteBatchCoreAsync(instance, original, catalog, center, notifications, policy, stop).ConfigureAwait(false);
                        return MirrorPulseRemotePollApplyOutcome.FromResult(result);
                    });
                await retained.ApplyAsync(instanceId, batch, options ?? CloudRemoteApplyOptions.Default, token).ConfigureAwait(false);
                return result ?? throw new InvalidDataException("The incoming batch did not reach its admitted apply boundary.");
            }, cancellationToken);

        private MirrorPulseRetainedRemoteBatchCoordinator RetainedRemoteBatches(MirrorPulseProductCatalog owner,
            MirrorPulseConflictCenter center, MirrorPulseConflictNotificationBridge notifications) =>
            new(owner, state.OpenStore, router!, async (instance, batch, policy, token) =>
                MirrorPulseRemotePollApplyOutcome.FromResult(await ApplyRemoteBatchCoreAsync(instance, batch, owner, center, notifications, policy, token).ConfigureAwait(false)));

        private async ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchCoreAsync(InstanceId instanceId,
            CloudRemoteChangeBatch batch, MirrorPulseProductCatalog catalog, MirrorPulseConflictCenter center,
            MirrorPulseConflictNotificationBridge notifications, CloudRemoteApplyOptions? options, CancellationToken cancellationToken)
        {
            var pollingRoot = MirrorPulseRemotePollBatchScope.RequireCurrentMapping(instanceId, batch, router);
            MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
            if (namespaceWork is not null)
            {
                if (router is null) throw new InvalidOperationException("Remote namespace admission requires current root routing.");
                var affected = batch.Changes.Count == 0 ? pollingRoot is { } selected ? new[] { selected } :
                    router.Registrations.Where(root => root.InstanceId == instanceId).Select(root => root.RootId) :
                    batch.Changes.SelectMany(change => change.PreviousRelativePath is { } previous ? new[] { change.RelativePath, previous } : [change.RelativePath])
                        .Select(path =>
                        {
                            var route = router.ResolveCurrentPath(path);
                            if (route.InstanceId != instanceId) throw new InvalidDataException("The remote batch belongs to another Adapter instance.");
                            return router.GetRegistration(route.InstanceId, route.RootKey).RootId;
                        });
                admission = namespaceWork.Enter(affected.Distinct());
            }
            using (admission)
            {
                var projector = new MirrorPulseRemoteConflictProjector(fileSystem, catalog, center, notifications);
                if (initializeNamespaceOnStart)
                {
                    if (router is null || _namespaceExecution is null || _namespaceActor is null)
                        throw new InvalidOperationException("Managed remote batches require the complete Host namespace boundary.");
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                        throw new PlatformNotSupportedException("Managed remote batches require Windows 11 24H2 or later.");
                    await new MirrorPulseRemoteNamespaceFence(router, catalog, allowOriginalMoveRecovery: true)
                        .EnsureApplyAllowedAsync(instanceId, batch, cancellationToken).ConfigureAwait(false);
                    var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor);
                    return await new MirrorPulseRemoteBatchCoordinator(fileSystem, state, projector, driver)
                        .ApplyAsync(instanceId, batch, options, cancellationToken).ConfigureAwait(false);
                }
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, projector);
                if (_namespaceActor is null) return await coordinator.ApplyAsync(instanceId, batch, options, cancellationToken).ConfigureAwait(false);
                return await _namespaceActor.RunAsync(async token =>
                {
                    if (_namespaceExecution is null) return await coordinator.ApplyAsync(instanceId, batch, options, token).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                        throw new PlatformNotSupportedException("Remote namespace execution requires Windows 11 24H2 or later.");
                    CloudRemoteApplyResult result = null!;
                    await _namespaceExecution.RunNamespaceOperationAsync(async () =>
                        result = await coordinator.ApplyAsync(instanceId, batch, options, token).ConfigureAwait(false)).ConfigureAwait(false);
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposal) return new(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            Interlocked.Exchange(ref _initializationClosing, 1);
            try { await _admissionShutdown.CancelAsync().ConfigureAwait(false); }
            finally
            {
                // Stop queued recovery before taking any gate that an active recovery may need.
                if (_retainedRecovery is not null) await _retainedRecovery.DisposeAsync().ConfigureAwait(false);
                // Recovery drains roots outside the library. Cancel its queued admissions
                // and await that owner before taking the library for final native disposal.
                await _recoveryRequests.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _initializationGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // Close every live work entry before cancellation. Library disposal can then
                        // release provider bodies while the application drain remains pending.
                        Task? workDrained = namespaceWork?.DisposeAsync().AsTask();
                        try
                        {
                            try
                            {
                                if (_uploadPump is not null) await _uploadPump.DisposeAsync().ConfigureAwait(false);
                            }
                            finally { if (_ownsScheduler) await _scheduler.DisposeAsync().ConfigureAwait(false); }
                        }
                        finally
                        {
                            try
                            {
                                try
                                {
                                    // A public placeholder creation may still need this live library
                                    // for its first binding and protected membership registration.
                                    try
                                    {
                                        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
                                            provider is MirrorPulseDemandProvider { NamespacePublisher: { } publication })
                                            await publication.CloseAndDrainAsync().ConfigureAwait(false);
                                    }
                                    finally { await fileSystem.DisposeAsync().ConfigureAwait(false); }
                                }
                                finally { if (workDrained is not null) await workDrained.ConfigureAwait(false); }
                            }
                            finally
                            {
                                try { if (_permissions is not null) await _permissions.DisposeAsync().ConfigureAwait(false); }
                                finally
                                {
                                    try
                                    {
                                        if (_initializationProtection is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                                            await _initializationProtection.DisposeAsync().ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            if (_scopedAnchorProtection is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                                                await _scopedAnchorProtection.DisposeAsync().ConfigureAwait(false);
                                        }
                                        finally
                                        {
                                            if (_startupProtection is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                                                await _startupProtection.DisposeAsync().ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        _initializationGate.Release(); _initializationGate.Dispose();
                        if (_providerDiagnostics is not null) await _providerDiagnostics.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _recoveryRequests.Release(); _recoveryRequests.Dispose(); _admissionShutdown.Dispose();
                }
            }
        }
    }
}

/// <summary>
/// Owns one current-user Cloud Files process session. Persistent registration and SQLite state
/// survive ordinary shutdown; only explicit account removal unregisters the root.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseCloudHostSession : IAsyncDisposable
{
    private static readonly Guid ProviderId = new("d194eacd-c38c-49df-a238-49871f9c8d1b");
    private static readonly byte[] RootIdentity = Encoding.UTF8.GetBytes("MirrorPulse/cloud-files/v1");

    private readonly MirrorPulseStoragePaths _paths;
    private readonly MirrorPulseSyncRootRegistrationCoordinator _registration;
    private readonly IMirrorPulseCloudRuntimeFactory _runtimeFactory;
    private readonly MirrorPulseSyncRootOwner _owner;
    private readonly MirrorPulseSyncRootDefinition _definition;
    private readonly MirrorPulseShellRegistrationProfile _profile;
    private IMirrorPulseCloudRuntime? _runtime;
    public MirrorPulseJournalPumpHealth? JournalHealth => _runtime?.JournalHealth;
    private bool _started;
    private bool _disposed;
    private readonly object _sessionDisposal = new();
    private Task? _sessionDisposeTask;

    public MirrorPulseCloudHostSession(
        MirrorPulseStoragePaths paths,
        MirrorPulseSyncRootRegistrationCoordinator registration,
        IMirrorPulseCloudRuntimeFactory runtimeFactory,
        MirrorPulseSyncRootOwner owner,
        string currentUserSid,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(owner);
        _paths = paths;
        _registration = registration;
        _runtimeFactory = runtimeFactory;
        _owner = owner;
        _definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", ProviderId, RootIdentity);
        _profile = MirrorPulseShellSyncRootRegistrar.CreateProfile(_definition, currentUserSid, displayName);
    }

    public MirrorPulseCloudHostSession(
        MirrorPulseStoragePaths paths,
        MirrorPulseSyncRootRegistrationCoordinator registration,
        IMirrorPulseCloudRuntimeFactory runtimeFactory,
        MirrorPulseSyncRootOwner owner,
        string currentUserSid,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations)
        : this(paths, registration, runtimeFactory, owner, currentUserSid,
            MirrorPulseSyncRootDisplayName.Resolve(instances, registrations))
    {
    }

    public MirrorPulseSyncRootDefinition Definition => _definition;

    public MirrorPulseShellRegistrationProfile ShellProfile => _profile;
    public IReadOnlyList<MirrorPulseNamespaceStartupRoot> NamespaceStartupResults => _runtime?.NamespaceStartupResults ?? [];

    public static MirrorPulseCloudHostSession CreateDefault(MirrorPulseStoragePaths paths, string displayName)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(
            paths,
            MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(),
            MirrorPulseSyncRootOwner.CreateDefault(),
            sid,
            displayName);
    }

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations) =>
        CreateDefault(paths, MirrorPulseSyncRootDisplayName.Resolve(instances, registrations));

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations,
        ICloudDemandProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(paths, MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(provider),
            MirrorPulseSyncRootOwner.CreateDefault(), sid, instances, registrations);
    }

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations,
        ICloudDemandProvider provider,
        IMirrorPulseWorkerUploadTransport uploads,
        IMirrorPulseWorkerStatTransport stats,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseConflictCenter? conflicts = null,
        MirrorPulseConflictNotificationBridge? notifications = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        MirrorPulseInstanceScheduler? scheduler = null,
        MirrorPulseNamespaceExecutionSession? namespaceExecution = null,
        MirrorPulseNamespaceActor? namespaceActor = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        bool initializeNamespaceOnStart = false,
        MirrorPulseTransferWorkerServices? transfers = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(paths, MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(provider, uploads, stats, mutations, router, catalog,
                mayDispatch, conflicts, notifications, scheduler, namespaceExecution, namespaceActor, namespaceWork, initializeNamespaceOnStart, transfers),
            MirrorPulseSyncRootOwner.CreateDefault(), sid, instances, registrations);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The Cloud Files Host session has already started.");
        }

        if (!_owner.TryAcquire(TimeSpan.Zero))
        {
            throw new InvalidOperationException("Another MirrorPulse Host owns this user's Cloud Files root.");
        }

        try
        {
            _owner.EnsureHeld();
            await _registration.EnsureRegisteredAsync(_definition, _profile, cancellationToken).ConfigureAwait(false);
            _runtime = _runtimeFactory.Create(_paths);
            await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        catch (Exception startFailure)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Cloud Files startup and resource cleanup both failed.",
                    startFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberRegistration> CreateNamespaceMemberAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.CreateNamespaceMemberAsync(operationId, parentMemberId, name, isDirectory, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string name, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceTransferAsync(operationId, memberId, targetParentMemberId, name, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> CopyNamespaceTransferAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.CopyNamespaceTransferAsync(operationId, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> VerifyNamespaceTransferTargetAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.VerifyNamespaceTransferTargetAsync(operationId, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferTargetBirthAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceTransferTargetBirthAsync(operationId, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> PublishNamespaceTransferTargetAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PublishNamespaceTransferTargetAsync(operationId, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceTransferCopy> DeleteNamespaceTransferSourceAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.DeleteNamespaceTransferSourceAsync(operationId, token);
    }

    public ValueTask<MirrorPulseNamespaceTransferCopy> ReadNamespaceTransferAsync(Guid operationId, CancellationToken token = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceTransferAsync(operationId, token);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
        string name, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceImportAsync(operationId, parentMemberId, name, sourcePath, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceImportAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceImportStatus> ReadNamespaceImportStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceImportStatusAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberInspection> InspectNamespaceMemberAsync(string relativePath,
        bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.InspectNamespaceMemberAsync(relativePath, isDirectory, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementAsync(Guid operationId, Guid memberId,
        string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceReplacementAsync(operationId, memberId, sourcePath, cancellationToken);
    }

    public ValueTask<IReadOnlyList<MirrorPulseNamespacePermissionTree>> ReadNamespacePermissionTreesAsync(RootId rootId,
        Guid? afterManifestId = null, int limit = 256, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespacePermissionTreesAsync(rootId, afterManifestId, limit, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceInitializationCompletion> InitializeNamespaceProtectionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.InitializeNamespaceProtectionAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceCurrentTreeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverNamespaceCurrentTreeAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceInitializationCompletion> RecoverNamespaceRootAsync(Guid operationId, RootId rootId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverNamespaceRootAsync(operationId, rootId, cancellationToken);
    }

    public ValueTask<MirrorPulseNamespaceInitializationStatus> ReadNamespaceInitializationStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceInitializationStatusAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespacePermissionTreeResult> RecoverNamespaceOriginalTreeAsync(Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverNamespaceOriginalTreeAsync(manifestId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteNamespaceReplacementAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceReplacementAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceReplacementStatus> ReadNamespaceReplacementStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceReplacementStatusAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceMemberTreeAsync(memberId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string targetName, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceMemberMoveAsync(operationId, memberId, targetParentMemberId, targetName, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteNamespaceMemberMoveAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceMemberMoveAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceMemberMoveStatus> ReadNamespaceMemberMoveStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceMemberMoveStatusAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirement> PrepareNamespaceMemberRetirementAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceMemberRetirementAsync(operationId, memberId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirement> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceMemberRetirementAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirement> ReadNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceMemberRetirementAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareNamespaceRetirementBatchAsync(operationId, memberId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceRetirementBatchAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceRetirementBatch> ReadNamespaceRetirementBatchAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ReadNamespaceRetirementBatchAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseNamespaceBirthRecoveryResult> ExecuteNamespaceBirthRecoveryAsync(
        Guid recoveryId, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.ExecuteNamespaceBirthRecoveryAsync(recoveryId, cancellationToken);
    }

    public ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
        IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ReadStatusAsync(instanceIds, cancellationToken);
    }

    public ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverManagedRootRenameAsync(operationId, cancellationToken);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    public ValueTask<MirrorPulseRootRenameHistory> PrepareManagedRootRenameAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.PrepareManagedRootRenameAsync(rootId, label, cancellationToken);
    }

    public ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
        InstanceId instanceId,
        CloudRemoteChangeBatch batch,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center,
        MirrorPulseConflictNotificationBridge notifications,
        CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyRemoteBatchAsync(instanceId, batch, catalog, center, notifications,
            options, cancellationToken);
    }

    public ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyUploadConflictAsync(conflictId, action, cancellationToken);
    }

    public ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        MirrorPulseProductCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyRemoteConflictAsync(conflictId, action, catalog, cancellationToken);
    }

    public ValueTask<MirrorPulseRemoteDeleteResolution> RecoverRemoteDeleteResolutionAsync(Guid commandId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverRemoteDeleteResolutionAsync(commandId, cancellationToken);
    }

    public ValueTask<MirrorPulseNamespaceRemoteUpdate> RecoverRemoteUpdateAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null) throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverRemoteUpdateAsync(operationId, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sessionDisposal) return new(_sessionDisposeTask ??= DisposeSessionCoreAsync());
    }

    private async Task DisposeSessionCoreAsync()
    {
        _disposed = true;
        _started = false;
        var runtime = _runtime;
        _runtime = null;
        await MirrorPulseHostResourceCleanup.RunAsync([
            () => runtime?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => { _owner.Dispose(); return ValueTask.CompletedTask; }
        ]).ConfigureAwait(false);
    }
}
