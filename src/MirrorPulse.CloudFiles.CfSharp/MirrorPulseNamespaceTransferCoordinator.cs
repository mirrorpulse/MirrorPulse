using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>The Host's normal-user Worker transports and actual connected protocol inventory.</summary>
public sealed record MirrorPulseTransferWorkerServices(IMirrorPulseWorkerUploadTransport Uploads,
    IMirrorPulseWorkerStatTransport Stats, IMirrorPulseWorkerRangeTransport Ranges,
    IMirrorPulseWorkerDirectoryPageSource Directories, Func<InstanceId, int> ProtocolVersion,
    IMirrorPulseWorkerMutationTransport? Mutations = null)
{
    internal Func<MirrorPulseDirectoryTransferCopyCheckpoint, int, CancellationToken, ValueTask>? DirectoryCopyCheckpoint { get; init; }
}

/// <summary>Executes original file transfer phases through the Host namespace boundary.</summary>
/// <remarks>The caller retains both root work leases and ordered instance admissions. Target publication
/// creates its separate protected online placeholder. Source retirement uses its dedicated conditional
/// remote acceptance and write-excluding data guard; preparation's read handle is not a deletion guard.
/// All phases keep both durable fences and retain private bytes; none acknowledges the original journal.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed partial class MirrorPulseNamespaceTransferCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, MirrorPulseLocalTransferContentStore inputs, Func<InstanceId, bool> mayDispatch,
    Func<InstanceId, int> protocolVersion)
{
    internal Func<MirrorPulseTransferPublicationCheckpoint, CancellationToken, ValueTask>? TargetPublicationCheckpoint { get; init; }
    internal CloudLocalChangeFeed? JournalFeed { get; init; }

    /// <summary>Materializes a cold source before taking the instance gates needed by its hydration provider.</summary>
    public Task MaterializeSourceAsync(MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespaceMemberProtectionSnapshot targetParent, CancellationToken token) =>
        execution.RunNormalUserOperationAsync(async () =>
        {
            // The Host dispatch callback may describe its startup inventory. Read both actual
            // catalog scopes before a cold source can enter the hydration/Worker boundary.
            var topology = await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false);
            _ = CaptureRoute(topology, original);
            var targetRoute = CaptureRoute(topology, targetParent);
            if (targetRoute.ProtocolVersion != 2)
                throw new InvalidOperationException("A transfer requires a negotiated V2 target before source materialization.");
            await MaterializeFileAsync(original, token).ConfigureAwait(false);
        });

    private async Task MaterializeFileAsync(MirrorPulseNamespaceMemberProtectionSnapshot original, CancellationToken token)
    {
        var file = fileSystem.GetFile(original.Context.Location.RelativePath);
        var before = await file.InspectAsync(token).ConfigureAwait(false);
        RequireBinding(before, original, requireResident: false);
        if (!before.IsPlaceholder || before.ContentAvailability == CloudContentAvailability.FullyAvailable) return;
        await file.HydrateAsync(CloudFileRange.WholeFile, token).ConfigureAwait(false);
        var after = await file.InspectAsync(token).ConfigureAwait(false);
        RequireBinding(after, original);
        if (after.ContentAvailability != CloudContentAvailability.FullyAvailable)
            throw new IOException("The transfer source is not fully resident.");
    }

    public async ValueTask<MirrorPulseNamespaceTransferCopy> PrepareAsync(Guid operationId,
        MirrorPulseNamespaceMemberProtectionSnapshot original, MirrorPulseNamespaceMemberProtectionSnapshot targetParent,
        string name, CancellationToken token)
    {
        if (operationId == Guid.Empty || original.Context.Member.IsDirectory || !targetParent.Context.Member.IsDirectory ||
            original.Context.Member.RootId is null || targetParent.Context.Member.RootId is null ||
            original.Context.Member.RootId == targetParent.Context.Member.RootId)
            throw new ArgumentException("A transfer requires an operation, source file and target directory.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('/') || name.Contains('\\')) throw new ArgumentException("A transfer requires one direct child name.", nameof(name));
        var command = new MirrorPulseNamespaceTransferCommandOrigin(1, operationId, original.Context.Member.MemberId,
            targetParent.Context.Member.MemberId, name, DateTimeOffset.UtcNow);
        // CfSharp validates the direct child name before any private input or catalog admission.
        string targetPath = fileSystem.GetDirectory(targetParent.Context.Location.RelativePath).GetFile(name).RelativePath.Replace('\\', '/');
        return await actor.RunAsync(async stop =>
        {
            var historical = await catalog.ReadNamespaceTransferAsync(operationId, stop).ConfigureAwait(false);
            if (historical is not null)
            {
                if (historical.Plan.Source.Context.Member.MemberId != original.Context.Member.MemberId ||
                    historical.Plan.TargetParent.Context.Member.MemberId != targetParent.Context.Member.MemberId || historical.Plan.TargetRelativePath != targetPath)
                    throw new InvalidOperationException("The original operation already belongs to a different transfer request.");
                return historical;
            }
            var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Location.ParentMemberId
                ?? throw new InvalidDataException("The source file has no current parent."), stop).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original source parent is missing.");
            return await WithGuardsAsync(original, parent, targetParent, targetPath, async (file, metadata) =>
            {
                var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
                var sourceRoute = CaptureRoute(topology, original); var targetRoute = CaptureRoute(topology, targetParent);
                var projection = await ReadSourceProjectionAsync(file, original, stop).ConfigureAwait(false);
                Guid contentId = Guid.NewGuid();
                return await execution.RunNormalUserOperationAsync(async () =>
                {
                    // Keep the original source read lease through capture and atomic admission.
                    // An existing journal owner rejects the whole new admission before either fence.
                    await using var source = OpenSource(file);
                    var content = await inputs.StageRetainedAsync(contentId, source, stop).ConfigureAwait(false);
                    var plan = new MirrorPulseNamespaceTransferPlan(2, operationId, sourceRoute, targetRoute,
                        original, parent, targetParent, targetPath, projection.RemoteRevision, contentId,
                        content.Length, content.Sha256, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
                    { CommandOrigin = command };
                    var journal = JournalFeed ?? throw new InvalidOperationException("The Host's running original journal is unavailable.");
                    var claim = await new MirrorPulseTransferJournalCapture(journal).CaptureAsync(plan, stop).ConfigureAwait(false);
                    return await catalog.PrepareNamespaceTransferWithJournalAsync(plan, claim, stop).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }, stop).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    public ValueTask<MirrorPulseNamespaceTransferCopy> CopyAsync(Guid operationId,
        IMirrorPulseWorkerUploadTransport uploads, CancellationToken token) => CopyAsync(operationId, uploads, null, null, null, token);

    internal ValueTask<MirrorPulseNamespaceTransferCopy> CopyAsync(Guid operationId, MirrorPulseTransferWorkerServices workers,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token) =>
        CopyAsync(operationId, workers.Uploads, workers.Mutations, recovery, workers, token);

    private async ValueTask<MirrorPulseNamespaceTransferCopy> CopyAsync(Guid operationId,
        IMirrorPulseWorkerUploadTransport uploads, IMirrorPulseWorkerMutationTransport? mutations,
        MirrorPulseFileTransferRecovery? recovery, MirrorPulseTransferWorkerServices? workers, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.CopyAcceptedAt is not null) return copy;
        if (copy.CopyStartedAt is not null && !copy.CopyUsesWorkerPreparation)
            throw new MirrorPulseMutationAmbiguousException("The original transfer copy has no durable acceptance receipt.");
        return await ExecuteGuardedAsync(copy.Plan, async file =>
        {
            await using var source = MirrorPulseTransferSourceDataLease.Open(file.FullPath);
            await source.VerifyAsync(copy.Plan.Length, copy.Plan.Sha256, token).ConfigureAwait(false);
            if (workers is not null && copy.Plan.SourceRemoteRevision is null)
                await RequireLocalOnlySourceAbsentAsync(copy, new(catalog, workers.Stats, workers.Ranges, workers.Directories),
                    workers.Stats, token).ConfigureAwait(false);
            await using var input = await inputs.OpenLeaseAsync(copy.Plan, token).ConfigureAwait(false);
            if (copy.JournalClaim is null)
            {
                if (copy.Plan.CommandOrigin is null || recovery is not null)
                    throw new InvalidOperationException("A legacy transfer cannot acquire retrospective journal ownership.");
                var journal = JournalFeed ?? throw new InvalidOperationException("The Host's running original journal is unavailable.");
                var claim = await new MirrorPulseTransferJournalCapture(journal).CaptureAsync(copy.Plan, token).ConfigureAwait(false);
                copy = await catalog.PrepareNamespaceTransferJournalClaimAsync(claim, token).ConfigureAwait(false);
            }
            await source.VerifyAsync(copy.Plan.Length, copy.Plan.Sha256, token).ConfigureAwait(false);
            return await new MirrorPulseTransferCopyExecutor(catalog, uploads) { Mutations = mutations, Recovery = recovery }
                .CopyAsync(operationId, input.Data, token).ConfigureAwait(false);
        }, token, recovery).ConfigureAwait(false);
    }

    public ValueTask<MirrorPulseNamespaceTransferCopy> VerifyTargetAsync(Guid operationId, IMirrorPulseWorkerStatTransport stats,
        IMirrorPulseWorkerRangeTransport ranges, IMirrorPulseWorkerDirectoryPageSource directories, CancellationToken token) =>
        VerifyTargetAsync(operationId, stats, ranges, directories, null, token);

    internal async ValueTask<MirrorPulseNamespaceTransferCopy> VerifyTargetAsync(Guid operationId, IMirrorPulseWorkerStatTransport stats,
        IMirrorPulseWorkerRangeTransport ranges, IMirrorPulseWorkerDirectoryPageSource directories,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.TargetVerification is not null) return copy;
        if (copy.CopyAcceptedAt is null)
            throw new MirrorPulseMutationAmbiguousException("A target readback cannot provide a lost original copy receipt.");
        return await ExecuteGuardedAsync(copy.Plan, _ => new MirrorPulseTransferTargetVerifier(catalog, stats, ranges, directories) { Recovery = recovery }
            .VerifyAsync(operationId, token), token, recovery).ConfigureAwait(false);
    }

    /// <summary>Selects the separate target birth and SDK identity before any native creation.</summary>
    public ValueTask<MirrorPulseNamespaceTransferCopy> PrepareTargetBirthAsync(Guid operationId, CancellationToken token) =>
        PrepareTargetBirthAsync(operationId, null, token);

    internal async ValueTask<MirrorPulseNamespaceTransferCopy> PrepareTargetBirthAsync(Guid operationId,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.TargetBirth is not null) return copy;
        if (copy.TargetVerification is null)
            throw new MirrorPulseMutationAmbiguousException("A target birth requires the original accepted and whole-verified copy.");
        return await ExecuteGuardedAsync(copy.Plan, async file =>
        {
            // Another request may have selected the immutable birth while waiting for the shared actor.
            var current = await ReadAsync(operationId, token).ConfigureAwait(false);
            if (current.TargetBirth is not null) return current;
            var verification = current.TargetVerification!;
            var parent = recovery is null ? current.Plan.TargetParent :
                (await catalog.ReadFileTransferRecoveryMembersAsync(operationId, recovery, token).ConfigureAwait(false)).TargetParent;
            var identity = await router.CreateCurrentFileIdentityAsync(current.Plan.TargetRoute.InstanceId, current.Plan.TargetRoute.RootKey,
                verification.RemoteId, current.AcceptedRevision, token).ConfigureAwait(false);
            _ = identity.Encode();
            Guid birthId = Guid.NewGuid(); var preparedAt = DateTimeOffset.UtcNow;
            var birth = new MirrorPulseNamespaceBirthIntent(4, birthId, current.Plan.TargetRoute.RootId,
                parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty,
                parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty, parent.Context.Member.LocalObject,
                current.Plan.TargetRelativePath, false, MirrorPulseNamespaceBirthOrigin.TransferTarget,
                parent.Context.Protection.RoleSid, preparedAt)
            { BornParent = parent.BornParent, TransferEntry = MirrorPulseNamespaceTransferBirthReference.Create(current) };
            var plan = new MirrorPulseNamespaceBirthPlan(2, birthId, identity.ItemId, identity.RemoteId, identity.RemoteRevision, preparedAt)
            { Content = new(current.Plan.Length, current.Plan.Sha256) };
            return await catalog.PrepareNamespaceTransferTargetBirthAsync(new(recovery is null ? 1 : 2, operationId, birth, plan)
            {
                SourceRecoveryAuditId = recovery?.SourceAuditId,
                TargetRecoveryAuditId = recovery?.TargetAuditId,
                RecoveredParent = recovery is null ? null : parent
            }, token).ConfigureAwait(false);
        }, token, recovery).ConfigureAwait(false);
    }

    public ValueTask<MirrorPulseNamespaceTransferCopy> PublishTargetAsync(Guid operationId, CancellationToken token) =>
        PublishTargetAsync(operationId, null, token);

    internal async ValueTask<MirrorPulseNamespaceTransferCopy> PublishTargetAsync(Guid operationId,
        MirrorPulseFileTransferRecovery? activation, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.TargetPublication is not null) return copy;
        return await actor.RunAsync(stop => PublishTargetUnderActorAsync(operationId, settleObserved: false, activation, stop), token).ConfigureAwait(false);
    }

    internal async ValueTask SettleObservedFileTransferTargetAsync(Guid operationId, CancellationToken token) =>
        _ = await actor.RunAsync(stop => PublishTargetUnderActorAsync(operationId, settleObserved: true, null, stop), token).ConfigureAwait(false);

    private async ValueTask<MirrorPulseNamespaceTransferCopy> PublishTargetUnderActorAsync(Guid operationId, bool settleObserved,
        MirrorPulseFileTransferRecovery? activation, CancellationToken token)
    {
        var copy = await catalog.ValidateNamespaceTransferTargetPublicationAsync(operationId, activation, token).ConfigureAwait(false);
        if (copy.TargetPublication is not null) return copy;
        // Startup settlement is limited to local evidence. It does not need a live Worker or dispatch a remote effect.
        if (!settleObserved && (protocolVersion(copy.Plan.SourceRoute.InstanceId) != copy.Plan.SourceRoute.ProtocolVersion ||
            protocolVersion(copy.Plan.TargetRoute.InstanceId) != copy.Plan.TargetRoute.ProtocolVersion))
            throw new InvalidOperationException("The negotiated transfer protocol changed before target publication.");
        var recovery = activation ?? copy.TargetRecovery;
        var members = recovery is null ? null :
            await catalog.ReadFileTransferRecoveryMembersAsync(operationId, recovery, token, activatingTarget: activation is not null).ConfigureAwait(false);
        var source = members?.Source ?? copy.Plan.Source;
        var parent = members?.TargetParent ?? copy.Plan.TargetParent;
        return await WithGuardsAsync(source, members?.SourceParent ?? copy.Plan.SourceParent, parent,
            copy.Plan.TargetRelativePath, async (file, _) =>
            {
                var projection = await ReadSourceProjectionAsync(file, source, token).ConfigureAwait(false);
                if (projection.RemoteRevision != copy.Plan.SourceRemoteRevision)
                    throw new InvalidDataException("The original transfer source projection changed before target publication.");
                var publisher = new MirrorPulseNamespaceTransferTargetPublisher(fileSystem, state, catalog, router, actor, TargetPublicationCheckpoint);
                return settleObserved
                    ? await publisher.SettleObservedFileUnderActorAsync(copy, parent, token).ConfigureAwait(false)
                    : await publisher.PublishUnderActorAsync(copy, parent, activation, token).ConfigureAwait(false);
            }, token, allowOwnedTargetBirth: true, retainedPublicationRole: settleObserved ? copy.TargetRoleSid : null).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceTransferCopy> ExecuteGuardedAsync(MirrorPulseNamespaceTransferPlan plan,
        Func<CloudFile, Task<MirrorPulseNamespaceTransferCopy>> operation, CancellationToken token, MirrorPulseFileTransferRecovery? recovery = null) =>
        await actor.RunAsync(async stop =>
        {
            var members = recovery is null ? null : await catalog.ReadFileTransferRecoveryMembersAsync(plan.OperationId, recovery, stop).ConfigureAwait(false);
            var source = members?.Source ?? plan.Source;
            return await WithGuardsAsync(source, members?.SourceParent ?? plan.SourceParent, members?.TargetParent ?? plan.TargetParent,
                plan.TargetRelativePath, async (file, metadata) =>
            {
                if (protocolVersion(plan.SourceRoute.InstanceId) != plan.SourceRoute.ProtocolVersion ||
                    protocolVersion(plan.TargetRoute.InstanceId) != plan.TargetRoute.ProtocolVersion)
                    throw new InvalidOperationException("The negotiated transfer protocol changed.");
                var projection = await ReadSourceProjectionAsync(file, source, stop).ConfigureAwait(false);
                if (projection.RemoteRevision != plan.SourceRemoteRevision)
                    throw new InvalidDataException("The original source projection changed after transfer admission.");
                return await execution.RunNormalUserOperationAsync(() => operation(file)).ConfigureAwait(false);
            }, stop).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

    private async Task<T> WithGuardsAsync<T>(MirrorPulseNamespaceMemberProtectionSnapshot source,
        MirrorPulseNamespaceMemberProtectionSnapshot sourceParent, MirrorPulseNamespaceMemberProtectionSnapshot targetParent,
        string targetPath, Func<CloudFile, MirrorPulseWindowsNamespacePermissionLease, Task<T>> operation, CancellationToken token,
        bool allowOwnedTargetBirth = false, bool allowSourceRetirement = false, string? retainedPublicationRole = null)
    {
        actor.AssertAccess(); T result = default!;
        if (retainedPublicationRole is null)
            await execution.RunNamespaceOperationAsync(GuardAsync).ConfigureAwait(false);
        else
            await execution.RunNormalUserOperationAsync(GuardAsync).ConfigureAwait(false);
        return result;

        async Task GuardAsync()
        {
            foreach (var retained in new[] { source, sourceParent, targetParent })
            {
                RequireLiveRoute(retained);
                if (retained.Context.Protection.RoleSid != (retainedPublicationRole ?? execution.RoleSid.Value) ||
                    retained.Context.Member.OwnerSid != execution.OwnerSid.Value ||
                    await catalog.ReadNamespaceMemberProtectionSnapshotAsync(retained.Context.Member.MemberId, token).ConfigureAwait(false) != retained)
                    throw new InvalidOperationException("Transfer execution requires unchanged protection and original member positions.");
            }
            var file = fileSystem.GetFile(source.Context.Location.RelativePath);
            var destination = fileSystem.GetFile(targetPath);
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(sourceParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(targetParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            var parentBefore = await parentLease.InspectAsync(token).ConfigureAwait(false);
            var targetBefore = await targetLease.InspectAsync(token).ConfigureAwait(false);
            RequireNative(parentBefore, sourceParent); RequireNative(targetBefore, targetParent);
            RequireNative(await childLease.InspectAsync(token).ConfigureAwait(false), source);
            RequireBinding(await file.InspectAsync(token).ConfigureAwait(false), source);
            if (!allowOwnedTargetBirth && (await destination.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("An existing native object occupies the transfer destination.");
            result = await operation(file, childLease).ConfigureAwait(false);
            if (!allowSourceRetirement) RequireNative(await childLease.InspectAsync(token).ConfigureAwait(false), source);
            else if ((await file.InspectAsync(CancellationToken.None).ConfigureAwait(false)).Exists)
                throw new InvalidDataException("The guarded transfer source still exists after its owned retirement.");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentLease.InspectAsync(token).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(token).ConfigureAwait(false));
            if (!allowOwnedTargetBirth && (await destination.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The native transfer destination changed during the original operation.");
        }
    }

    private async Task<MirrorPulseNamespaceMemberLocationCandidate> ReadSourceProjectionAsync(CloudFile file,
        MirrorPulseNamespaceMemberProtectionSnapshot source, CancellationToken token)
    {
        var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
            source.Context.Member.MemberId, actor, token).ConfigureAwait(false);
        RequireSourceProjection(source, projection);
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false); RequireBinding(snapshot, source);
        if (snapshot.IsPlaceholder)
        {
            var identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
            if (identity.ItemId != projection.ItemId || identity.RemoteId != projection.RemoteId ||
                !MatchesSourceRevision(snapshot, identity, projection.RemoteRevision))
                throw new InvalidDataException("The native transfer source disagrees with its official public identity: " +
                    $"itemMatches={identity.ItemId == projection.ItemId}; remoteMatches={identity.RemoteId == projection.RemoteId}; " +
                    $"nativeRevisionPresent={identity.RemoteRevision is not null}; officialRevisionPresent={projection.RemoteRevision is not null}.");
        }
        return projection;
    }

    internal static void RequireSourceProjection(MirrorPulseNamespaceMemberProtectionSnapshot source,
        MirrorPulseNamespaceMemberLocationCandidate projection)
    {
        if (projection.State != MirrorPulseNamespaceMemberLocationState.Projected || projection.ItemId != source.Identity.ItemId ||
            projection.RemoteId != source.Identity.RemoteId || projection.RelativePath?.Replace('\\', '/') != source.Context.Location.RelativePath)
            throw new InvalidDataException("The transfer source lost its original public SDK projection.");
    }

    private MirrorPulseTransferRoute CaptureRoute(MirrorPulseAdapterTopology topology, MirrorPulseNamespaceMemberProtectionSnapshot member)
    {
        var root = RequireLiveRoute(member);
        var registered = topology.Roots.Single(item => item.RootId == root.RootId);
        if (registered != root) throw new InvalidOperationException("The routed transfer root differs from its current catalog registration.");
        return MirrorPulseTransferRoute.Capture(topology.Instances.Single(item => item.InstanceId == root.InstanceId),
            root, protocolVersion(root.InstanceId));
    }

    private RootRegistration RequireLiveRoute(MirrorPulseNamespaceMemberProtectionSnapshot member)
    {
        var root = router.Registrations.Single(item => item.RootId == member.Context.Member.RootId);
        if (root.State != RootRegistrationState.Active || !mayDispatch(root.InstanceId))
            throw new InvalidOperationException("Both original transfer roots and instances must remain enabled and active.");
        return root;
    }

    private async Task<MirrorPulseNamespaceTransferCopy> ReadAsync(Guid operationId, CancellationToken token) =>
        await catalog.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original file transfer is not registered.");

    private static FileStream OpenSource(CloudFile file) => new(file.FullPath, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void RequireNative(MirrorPulseNamespacePermissionObject native, MirrorPulseNamespaceMemberProtectionSnapshot retained)
    {
        if (native.LocalObject != retained.Context.Member.LocalObject || native.RootId != retained.Context.Member.RootId ||
            native.RelativePath != retained.Context.Location.RelativePath || native.IsDirectory != retained.Context.Member.IsDirectory ||
            native.LinkCount != 1 || native.OwnerSid != retained.Context.Member.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(retained.ExpectedDacl, native.Dacl))
            throw new InvalidDataException("A transfer member or direct parent lost its exact native binding and owned protection.");
    }

    private static void RequireBinding(CloudItemSnapshot snapshot, MirrorPulseNamespaceMemberProtectionSnapshot source, bool requireResident = true)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.File || snapshot.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != source.Context.Member.LocalObject ||
            requireResident && snapshot.IsPlaceholder && snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable)
            throw new InvalidDataException("The transfer requires the original fully resident source object.");
    }
}
