using System.Runtime.Versioning;
using System.Text;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public interface IMirrorPulseCloudRuntime : IAsyncDisposable
{
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
    internal Func<ICloudStateStoreFactory, ICloudStateStoreFactory>? StateStoreDecorator { get; init; }

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
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null)
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
            _mayDispatch, _conflicts, _notifications, _scheduler, _namespaceExecution, _namespaceActor, _namespaceWork);
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
        MirrorPulseNamespaceWorkAdmission? namespaceWork)
        : IMirrorPulseCloudRuntime
    {
        private MirrorPulseJournalUploadPump? _uploadPump;
        private MirrorPulseUploadConflictActions? _conflictActions;
        private MirrorPulseRemoteConflictActions? _remoteConflictActions;
        private MirrorPulseCloudProviderDiagnostics? _providerDiagnostics;
        private readonly MirrorPulseInstanceScheduler _scheduler = scheduler ?? new();
        private readonly bool _ownsScheduler = scheduler is null;
        private readonly MirrorPulseNamespaceExecutionSession? _namespaceExecution = namespaceExecution;
        private readonly MirrorPulseNamespaceActor? _namespaceActor = namespaceActor;
        private readonly MirrorPulseNamespacePermissionCoordinator? _permissions = catalog is null ? null : new(catalog);
        private MirrorPulseNamespaceInitializationResult? _initializationProtection;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private int _initializationClosing;
        private readonly object _disposal = new();
        private Task? _disposeTask;
        private MirrorPulseManagedRootRenameService? _rootRenames;
        private MirrorPulseNamespaceAnchorProtection? _scopedAnchorProtection;
        public IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> RootRenameRecoveryResults { get; private set; } = [];
        public MirrorPulseJournalPumpHealth? JournalHealth => _uploadPump?.Health;

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
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                await using var admission = await BeginInitializationWorkRecoveryAsync(operationId,
                    MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, cancellationToken, selectedRoot).ConfigureAwait(false);
                if (selectedRoot is not null && _scopedAnchorProtection is null &&
                    await catalog!.ReadNamespaceInitializationCompletionAsync(operationId, cancellationToken).ConfigureAwait(false) is null)
                    _scopedAnchorProtection = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router!,
                        _namespaceExecution!, _namespaceActor!, _permissions!).ProtectAsync(cancellationToken).ConfigureAwait(false);
                var coordinator = new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, _scheduler, _permissions!);
                var result = selectedRoot is { } root
                    ? await coordinator.RecoverRootAsync(operationId, root, cancellationToken).ConfigureAwait(false)
                    : await coordinator.RecoverAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (!result.IsHistoricalReplay)
                {
                    var prior = _initializationProtection; _initializationProtection = result;
                    if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
                    admission?.CompleteAfterFreshAudit();
                }
                return result.Completion;
            }
            finally { _initializationGate.Release(); }
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceInitializationCompletion> InitializeNamespaceProtectionAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                await using var admission = await BeginInitializationWorkRecoveryAsync(operationId,
                    MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory, cancellationToken).ConfigureAwait(false);
                var result = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, _scheduler, _permissions!).InitializeAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (!result.IsHistoricalReplay)
                {
                    var prior = _initializationProtection; _initializationProtection = result;
                    if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
                    admission?.CompleteAfterFreshAudit();
                }
                return result.Completion;
            }
            finally { _initializationGate.Release(); }
        }

        [SupportedOSPlatform("windows10.0.26100")]
        private async ValueTask<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> BeginInitializationWorkRecoveryAsync(Guid operationId,
            MirrorPulseNamespaceInitializationInventoryKind inventoryKind, CancellationToken token, RootId? selectedRoot = null)
        {
            var retained = await catalog!.ReadNamespaceInitializationAsync(operationId, token).ConfigureAwait(false);
            if (retained?.RequestedInventoryKind is { } requested && requested != inventoryKind)
                throw new InvalidOperationException("The retained initialization requires its originally selected recovery route.");
            if (retained is not null && (selectedRoot is { } selected ? retained.Version != 4 || retained.Roots.Count != 1 ||
                retained.Roots[0].RootId != selected : retained.Version == 4))
                throw new InvalidOperationException("The retained initializer requires its explicit complete or selected-root recovery route.");
            if (namespaceWork is null || await catalog!.ReadNamespaceInitializationCompletionAsync(operationId, token).ConfigureAwait(false) is not null)
                return null;
            var registrations = router!.Registrations.Where(root => selectedRoot is null || root.RootId == selectedRoot.Value)
                .OrderBy(root => root.RootId.Value).ToArray();
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
            var admission = await namespaceWork.BeginRecoveryAsync(registrations.Select(root => root.RootId), token).ConfigureAwait(false);
            try
            {
                if (retained is null)
                {
                    var selectedRoots = plannedRoots ?? throw new InvalidDataException("The initializer has no selected root provenance.");
                    if (inventoryKind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
                        await MirrorPulseNamespaceInitializationCoordinator.RequireOriginalGraphAsync(catalog, registrations, selectedRoots, token).ConfigureAwait(false);
                    else if (!selectedRoots.SequenceEqual(await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog, registrations, token).ConfigureAwait(false)))
                        throw new InvalidDataException("Current root provenance changed while earlier work drained.");
                    await catalog.PrepareNamespaceInitializationAsync(new(selectedRoot is not null ? 4 : selectedRoots.Count == 0 ? 3 : selectedRoots.Any(root => root.BornRootMemberId is not null) ? 2 : 1,
                        operationId, selectedRoots, DateTimeOffset.UtcNow)
                    { RequestedInventoryKind = inventoryKind }, token).ConfigureAwait(false);
                }
                await ReopenLibraryForNamespaceRecoveryAsync(token).ConfigureAwait(false);
                return admission;
            }
            catch { await admission.DisposeAsync().ConfigureAwait(false); throw; }
        }

        private async Task ReopenLibraryForNamespaceRecoveryAsync(CancellationToken token)
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
            await StartLibraryServicesAsync(token).ConfigureAwait(false);
        }

        private async ValueTask<T> WithCurrentLibraryAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            await _initializationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                return await operation(token).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
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
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                return await new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, _scheduler, _permissions!).RecoverAsync(initializationOperationId, memberId, cancellationToken).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
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
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                return await new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, _scheduler, _permissions!).RecoverAsync(initializationOperationId, actionId, memberId, cancellationToken).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceBirthProtection> VerifyNamespaceBornMemberRecoveryAsync(Guid initializationOperationId,
            Guid protectionId, Guid memberId, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _initializationClosing) != 0, this);
                return await new MirrorPulseNamespaceBornMemberReadbackCoordinator(fileSystem, catalog!, router!, _namespaceExecution!,
                    _namespaceActor!, _scheduler).VerifyAsync(initializationOperationId, protectionId, memberId, cancellationToken).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespacePermissionTreeResult> RecoverNamespaceOriginalTreeAsync(Guid manifestId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            return WithCurrentLibraryAsync(token => new MirrorPulseNamespaceOriginalTreeRecoveryCoordinator(fileSystem, catalog!, router!,
                _namespaceExecution!, _namespaceActor!, _scheduler, _permissions!).RecoverAsync(manifestId, token), cancellationToken);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberRegistration> CreateNamespaceMemberAsync(Guid operationId,
            Guid parentMemberId, string name, bool isDirectory, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled creation requires the Host namespace boundary.");
            var parent = await catalog.ReadNamespaceMemberAsync(parentMemberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The creation parent is not registered.");
            if (parent.RootId is null) throw new InvalidOperationException("Creation requires an Adapter's managed root.");
            var registration = router.Registrations.Single(root => root.RootId == parent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.CreateAsync(operationId, parentMemberId, name, isDirectory, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
            string name, string sourcePath, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            if (operationId == Guid.Empty || parentMemberId == Guid.Empty)
                throw new ArgumentException("Operation and parent member IDs are required.");
            var parent = await catalog!.ReadNamespaceMemberAsync(parentMemberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The import parent is not registered.");
            var registration = router!.Registrations.Single(root => root.RootId == parent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            // A source may hydrate through this same instance. Capture and verify before either lock.
            var content = await _namespaceExecution!.RunNormalUserOperationAsync(
                () => inputs.StageAsync(operationId, sourcePath, cancellationToken)).ConfigureAwait(false);
            await using var input = await _namespaceExecution.RunNormalUserOperationAsync(
                () => inputs.OpenLeaseAsync(operationId, content, cancellationToken)).ConfigureAwait(false);
            var coordinator = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor!);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.PrepareImportAsync(operationId, parentMemberId, name, input, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            var status = await ReadNamespaceImportStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
            var birth = (await catalog!.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false))!;
            var registration = router!.Registrations.Single(root => root.RootId == birth.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            var coordinator = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, _namespaceExecution!, _namespaceActor!);
            MirrorPulseNamespaceMemberRegistration result;
            if (!status.ContentRecorded && !status.Registered)
            {
                await using var input = await _namespaceExecution!.RunNormalUserOperationAsync(
                    () => inputs.OpenLeaseAsync(operationId, new(status.Length, status.Sha256), cancellationToken)).ConfigureAwait(false);
                result = await _scheduler.RunAsync(registration.InstanceId,
                    token => coordinator.ExecuteImportAsync(operationId, input, token), cancellationToken).ConfigureAwait(false);
            }
            else
                result = await _scheduler.RunAsync(registration.InstanceId,
                    token => coordinator.ExecuteImportAsync(operationId, null, token), cancellationToken).ConfigureAwait(false);
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
        public async ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareNamespaceReplacementAsync(Guid operationId, Guid memberId,
            string sourcePath, CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            var member = await catalog!.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The replacement member is not registered.");
            if (member.RootId is not { } rootId) throw new InvalidOperationException("Replacement requires a managed root member.");
            using var admission = namespaceWork?.Enter([rootId]);
            var inputs = new MirrorPulseLocalImportStagingStore(new(syncRootPath, dataRootPath));
            var preparation = new MirrorPulseNamespaceReplacementPreparationCoordinator(fileSystem, state, catalog!, router!,
                _namespaceExecution!, _namespaceActor!, _scheduler, inputs, mayDispatch ?? (_ => false));
            return await preparation.PrepareAsync(operationId, memberId, sourcePath, cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteNamespaceReplacementAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            RequireImportBoundary();
            var intent = await catalog!.ReadNamespaceReplacementAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The replacement admission is not registered.");
            using var admission = namespaceWork?.Enter([intent.RootId]);
            var paths = new MirrorPulseStoragePaths(syncRootPath, dataRootPath);
            var inputs = new MirrorPulseLocalImportStagingStore(paths);
            var executor = new MirrorPulseNamespaceReplacementExecutor(fileSystem, catalog!, router!, _namespaceExecution!,
                _namespaceActor!, _scheduler, inputs, new(paths));
            var result = await executor.ExecuteAsync(operationId, cancellationToken).ConfigureAwait(false);
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
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, _namespaceExecution, _namespaceActor)
                    .InspectAsync(relativePath, isDirectory, cancellationToken).ConfigureAwait(false);
            }
            finally { _initializationGate.Release(); }
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<IReadOnlyList<MirrorPulseNamespaceMemberTreeEntry>> ReadNamespaceMemberTreeAsync(Guid memberId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Namespace tree reads require the Host catalog.");
            return await catalog.ReadNamespaceMemberTreeAsync(memberId, cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveAsync(Guid operationId, Guid memberId,
            Guid targetParentMemberId, string targetName, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled move preparation requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The source member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.PrepareAsync(operationId, memberId, targetParentMemberId, targetName, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteNamespaceMemberMoveAsync(
            Guid operationId, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled member moves require the Host namespace boundary.");
            var intent = await catalog.ReadNamespaceMemberMoveAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The move admission is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == intent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(
                fileSystem, state, catalog, router, _namespaceExecution, _namespaceActor);
            // Drain earlier instance work before entering the namespace actor. Subsequent dispatch
            // rechecks the durable root fence after acquiring this same scheduler.
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.ExecuteAsync(operationId, token), cancellationToken).ConfigureAwait(false);
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
        public async ValueTask<MirrorPulseNamespaceRetirement> PrepareNamespaceMemberRetirementAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled retirement preparation requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.PrepareAsync(operationId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceRetirement> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Controlled retirements require the Host namespace boundary.");
            var retirement = await catalog.ReadNamespaceRetirementAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement admission is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == retirement.Intent.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.ExecuteAsync(operationId, token), cancellationToken).ConfigureAwait(false);
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
        public async ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareNamespaceRetirementBatchAsync(Guid operationId,
            Guid memberId, CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Subtree retirement requires the Host namespace boundary.");
            var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement member is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == member.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.PrepareBatchAsync(operationId, memberId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteNamespaceRetirementBatchAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Subtree retirement requires the Host namespace boundary.");
            var batch = await catalog.ReadNamespaceRetirementBatchAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is not registered.");
            var registration = router.Registrations.Single(root => root.RootId == batch.RootId);
            using var admission = namespaceWork?.Enter([registration.RootId]);
            var coordinator = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, _namespaceExecution, _namespaceActor);
            return await _scheduler.RunAsync(registration.InstanceId,
                token => coordinator.ExecuteBatchAsync(operationId, token), cancellationToken).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public async ValueTask<MirrorPulseNamespaceRetirementBatch> ReadNamespaceRetirementBatchAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            if (catalog is null) throw new NotSupportedException("Subtree receipts require the Host catalog.");
            return await catalog.ReadNamespaceRetirementBatchAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is not registered.");
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceBirthRecoveryResult> ExecuteNamespaceBirthRecoveryAsync(
            Guid recoveryId, CancellationToken requestCancellation)
        {
            if (router is null || catalog is null || _namespaceExecution is null || _namespaceActor is null)
                throw new NotSupportedException("Staged birth recovery requires the Host namespace boundary.");
            return WithCurrentLibraryAsync(async cancellationToken =>
            {
                var action = await catalog.ReadNamespaceBirthRecoveryActionAsync(recoveryId, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The recovery action is not registered.");
                var birth = await catalog.ReadNamespaceBirthAsync(action.BirthOperationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The birth admission is not registered.");
                int separator = birth.RelativePath.LastIndexOf('/');
                string parentPath = separator < 0 ? string.Empty : birth.RelativePath[..separator];
                CloudFile file = fileSystem.GetFile(birth.RelativePath);
                CloudDirectory parent = parentPath.Length == 0 ? fileSystem.Root : fileSystem.GetDirectory(parentPath);
                var coordinator = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, _namespaceExecution);
                var registration = router.Registrations.Single(root => root.RootId == birth.RootId);
                return await _scheduler.RunAsync(registration.InstanceId, async stop => await _namespaceActor.RunAsync(async token =>
                {
                    _namespaceActor.AssertAccess();
                    MirrorPulseNamespaceBirthRecoveryResult result = null!;
                    await _namespaceExecution.RunNamespaceOperationAsync(async () =>
                    {
                        result = await coordinator.ExecuteStagedFileAsync(recoveryId, file, parent, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    return result;
                }, stop).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }, requestCancellation);
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _providerDiagnostics = new(Path.Combine(dataRootPath, "logs", "cloud-files"));
            await StartLibraryAsync(cancellationToken).ConfigureAwait(false);
            await StartLibraryServicesAsync(cancellationToken).ConfigureAwait(false);
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

        private async Task StartLibraryServicesAsync(CancellationToken cancellationToken)
        {
            if (catalog is not null && router is not null)
            {
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router);
                _rootRenames = new(catalog, _scheduler, coordinator.RecoverAsync);
                // Recover original namespace evidence before upload dispatch starts.
                // Incomplete and legacy records keep their per-root durable fence.
                RootRenameRecoveryResults = await _rootRenames.RestorePendingAsync(cancellationToken).ConfigureAwait(false);
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
                _uploadPump = new MirrorPulseJournalUploadPump(feed, router, catalog, uploads, stats, state,
                    syncRootPath, dataRootPath, mayDispatch, completion, conflicts, notifications, mutations,
                    uploads as IMirrorPulseWorkerRangeTransport, uploads as IMirrorPulseWorkerDirectoryPageSource, fileSystem, _scheduler,
                    _namespaceActor, _namespaceExecution, namespaceWork);
                _conflictActions = new MirrorPulseUploadConflictActions(catalog, state, feed,
                    new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)),
                    new MirrorPulseStoragePaths(syncRootPath, dataRootPath), router, mutations);
                await _uploadPump.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            if (catalog is not null && conflicts is not null)
            {
                _remoteConflictActions = MirrorPulseRemoteConflictActions.For(fileSystem,
                    new MirrorPulseConflictCopyStore(new MirrorPulseStoragePaths(syncRootPath, dataRootPath)),
                    catalog, conflicts);
            }
        }

        public ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
            Guid operationId, CancellationToken cancellationToken) =>
            WithCurrentLibraryAsync(token => (_rootRenames ?? throw new NotSupportedException("The managed root recovery service is unavailable."))
                .RecoverAsync(operationId, token), cancellationToken);

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseRootRenameHistory> PrepareManagedRootRenameAsync(RootId rootId, string label,
            CancellationToken requestCancellation)
        {
            if (router is null || catalog is null) throw new NotSupportedException("Managed root rename requires the Host namespace boundary.");
            return WithCurrentLibraryAsync(async cancellationToken =>
            {
                var root = (await catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false)).Roots.Single(item => item.RootId == rootId);
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router);
                return await _scheduler.RunAsync(root.InstanceId, token => coordinator.PrepareAsync(rootId, label, token), cancellationToken).ConfigureAwait(false);
            }, requestCancellation);
        }

        public ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            CancellationToken cancellationToken) =>
            WithCurrentLibraryAsync(token => (_conflictActions ?? throw new InvalidOperationException(
                "The upload conflict action service has not started.")).ApplyAsync(
                    conflictId, action, token), cancellationToken);

        public ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            MirrorPulseProductCatalog catalog,
            CancellationToken requestCancellation)
        {
            return WithCurrentLibraryAsync(async cancellationToken =>
            {
                MirrorPulseRemoteConflictActions actions = _remoteConflictActions ?? throw new InvalidOperationException(
                    "The remote conflict action service has not started.");
                MirrorPulseConflictRecord conflict = (await catalog.ReadRemoteConflictProjectionsAsync(
                    cancellationToken).ConfigureAwait(false)).SingleOrDefault(item =>
                        item.ConflictId == conflictId) ?? throw new FileNotFoundException(
                            "The remote conflict was not found.");
                return await actions.ApplyAsync(Guid.NewGuid(), conflict, action,
                    action == MirrorPulseConflictAction.KeepBoth
                        ? MirrorPulseConflictPreservedSide.Local
                        : null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }, requestCancellation);
        }

        public async ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
            IEnumerable<InstanceId> instanceIds,
            CancellationToken cancellationToken)
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await MirrorPulseCloudStatusReader.ReadAsync(state.OpenStore, instanceIds, cancellationToken).ConfigureAwait(false); }
            finally { _initializationGate.Release(); }
        }

        public async ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
            InstanceId instanceId,
            CloudRemoteChangeBatch batch,
            MirrorPulseProductCatalog catalog,
            MirrorPulseConflictCenter center,
            MirrorPulseConflictNotificationBridge notifications,
            CloudRemoteApplyOptions? options,
            CancellationToken cancellationToken)
        {
            MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
            if (namespaceWork is not null)
            {
                if (router is null) throw new InvalidOperationException("Remote namespace admission requires current root routing.");
                var affected = batch.Changes.Count == 0 ? router.Registrations.Where(root => root.InstanceId == instanceId).Select(root => root.RootId) :
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
                                if (_scopedAnchorProtection is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                                    await _scopedAnchorProtection.DisposeAsync().ConfigureAwait(false);
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
        MirrorPulseNamespaceActor? namespaceActor = null)
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
                mayDispatch, conflicts, notifications, scheduler, namespaceExecution, namespaceActor),
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
                if (_runtime is not null)
                {
                    await _runtime.DisposeAsync().ConfigureAwait(false);
                }

                _runtime = null;
                _owner.Dispose();
                _disposed = true;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _runtime = null;
        }

        _owner.Dispose();
        _disposed = true;
    }
}
