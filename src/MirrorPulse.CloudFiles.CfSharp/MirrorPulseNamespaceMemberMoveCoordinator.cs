using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceMemberMoveCheckpoint { AfterNativeMove, AfterNativeObservation, AfterOfficialProjection }

/// <summary>Executes an admitted member move using fresh native checks and public CfSharp recovery.</summary>
/// <remarks>
/// The actor serializes these operations. The caller must also quiesce other root work before
/// admission. Parent leases retain both ancestor chains across movement; the child metadata
/// lease is released before CfSharp moves it. Immutable first observations never authorize a
/// retry. Directory moves settle every sealed descendant in one product transaction after public
/// CfSharp reconciliation and fresh inspection of the complete actual subtree.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private readonly CloudFileSystem _fileSystem;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseNamespaceExecutionSession _execution;
    private readonly MirrorPulseNamespaceActor _actor;
    private readonly Func<MirrorPulseNamespaceMemberMoveCheckpoint, CancellationToken, ValueTask>? _checkpoint;

    public MirrorPulseNamespaceMemberMoveCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
        : this(fileSystem, state, catalog, router, execution, actor, null) { }

    internal MirrorPulseNamespaceMemberMoveCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
        MirrorPulseNamespaceActor actor, Func<MirrorPulseNamespaceMemberMoveCheckpoint, CancellationToken, ValueTask>? checkpoint)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _checkpoint = checkpoint;
    }

    public async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        return await _actor.RunAsync(token => ExecuteAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteAdmittedAsync(Guid operationId, CancellationToken token,
        bool observeRemoteOnly = false)
    {
        _actor.AssertAccess();
        // A completed replay returns history only, without inspecting or mutating today's path.
        var completed = await _catalog.ReadNamespaceMemberMoveCompletionAsync(operationId, token).ConfigureAwait(false);
        if (completed is not null) return completed;
        var intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
        if (intent.RemoteEntry is not null && !observeRemoteOnly)
            throw new InvalidOperationException("A remote move requires its original CfSharp batch owner.");
        if (observeRemoteOnly && intent.RemoteEntry is null)
            throw new InvalidOperationException("Remote settlement requires its original remote admission.");
        var member = await _catalog.ReadNamespaceMemberAsync(intent.MemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The admitted move member is missing.");
        if (member.IsDirectory)
            return await ExecuteDirectoryMoveAdmittedAsync(intent, token, observeRemoteOnly).ConfigureAwait(false);
        var childProof = await ReadProofAsync(member.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
        var sourceProof = await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false);
        var targetProof = await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false);
        var start = await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false);
        if (observeRemoteOnly && start is null)
            throw new InvalidDataException("Remote settlement cannot invent a before-native start.");
        CloudFile source = _fileSystem.GetFile(intent.Source.RelativePath);
        CloudFile moved = _fileSystem.GetFile(intent.Target.RelativePath);
        CloudDirectory sourceParent = source.Parent ?? throw new InvalidDataException("A member requires a source parent.");
        CloudDirectory targetParent = moved.Parent ?? throw new InvalidDataException("A member requires a target parent.");
        MirrorPulseNamespaceMemberMoveCompletion result = null!;
        await _execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(intent);
            await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceParent, _router, token).ConfigureAwait(false);
            await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetParent, _router, token).ConfigureAwait(false);
            var sourceBefore = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
            var targetBefore = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
            var sourceSnapshot = await source.InspectAsync(token).ConfigureAwait(false);
            var targetSnapshot = await moved.InspectAsync(token).ConfigureAwait(false);
            bool sameName = string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase);
            if (observeRemoteOnly)
            {
                if (start is null || !targetSnapshot.Exists || sourceSnapshot.Exists && !sameName)
                    throw new InvalidDataException("Remote settlement requires the original start and the moved object at its admitted target.");
                _ = await InspectChildAsync(moved, childProof, intent.Target.RelativePath, intent, token).ConfigureAwait(false);
                if (sameName) await RequireActualTargetSpellingAsync(targetParent, intent, token).ConfigureAwait(false);
            }
            else if (sourceSnapshot.Exists)
            {
                _ = await InspectChildAsync(source, childProof, intent.Source.RelativePath, intent, token).ConfigureAwait(false);
                if (targetSnapshot.Exists && !sameName)
                    throw new IOException("The admitted move destination is occupied.");
            }
            else
            {
                if (start is null || !targetSnapshot.Exists)
                    throw new InvalidDataException("Move recovery requires its original start and the original object at the target.");
                _ = await InspectChildAsync(moved, childProof, intent.Target.RelativePath, intent, token).ConfigureAwait(false);
            }
            // Once the original object is verified at the target, recovery drains even if the
            // client cancels. A fresh source still observes cancellation before native movement.
            CancellationToken operationToken = !observeRemoteOnly && sourceSnapshot.Exists ? token : CancellationToken.None;
            // Verify the official item before a native write. A path alone is never recovery evidence.
            var projectedBefore = await ReadProjectionAsync(member.MemberId, operationToken).ConfigureAwait(false);
            RequireProjection(projectedBefore, intent, observeRemoteOnly ? intent.Target.RelativePath : sourceSnapshot.Exists ? intent.Source.RelativePath : null);
            if (observeRemoteOnly && projectedBefore.RemoteRevision != intent.RemoteEntry!.RemoteRevision)
                throw new InvalidDataException("The original remote move has not projected its incoming revision.");
            await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, operationToken).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(operationToken).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(operationToken).ConfigureAwait(false));
            if (start is null)
                await _catalog.RecordNamespaceMemberMoveStartAsync(new(1, operationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);

            if (!observeRemoteOnly && (sourceSnapshot.Exists || !string.Equals(projectedBefore.RelativePath?.Replace('\\', '/'), intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                // CfSharp owns projection recovery if a previous native move left the source absent.
                // Its public operation ignores cancellation for durable work after native success.
                await source.MoveToAsync(targetParent, TargetName(intent), cancellationToken: operationToken).ConfigureAwait(false);
            }
            // Movement may already have happened on a previous attempt. Drain all verification and
            // persistence after this point even when the client disconnects or requests cancellation.
            CancellationToken durable = CancellationToken.None;
            await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove, durable).ConfigureAwait(false);
            var fresh = await InspectChildAsync(moved, childProof, intent.Target.RelativePath, intent, durable).ConfigureAwait(false);
            var freshParent = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, durable).ConfigureAwait(false);
            var observation = await _catalog.ReadNamespaceMemberMoveObservationAsync(operationId, durable).ConfigureAwait(false);
            var nativeIdentity = CloudPlaceholderIdentity.Decode(fresh.Snapshot.PlaceholderIdentity.Span);
            if (observation is null)
            {
                observation = new(1, operationId, intent.ItemId, intent.RemoteId, nativeIdentity.RemoteRevision,
                    intent.Target.RelativePath, false, true, fresh.Facts.LinkCount!.Value,
                    Verification(fresh.Facts), Verification(freshParent));
                await _catalog.RecordNamespaceMemberMoveObservationAsync(observation, durable).ConfigureAwait(false);
            }
            else if (observation.NativeRemoteRevision != nativeIdentity.RemoteRevision ||
                !SameProtection(observation.Child, fresh.Facts) || !SameProtection(observation.Parent, freshParent))
                throw new InvalidDataException("Fresh move recovery disagrees with the retained native observation.");
            await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation, durable).ConfigureAwait(false);

            var projected = await ReadProjectionAsync(member.MemberId, durable).ConfigureAwait(false);
            RequireProjection(projected, intent, intent.Target.RelativePath);
            var projection = await _catalog.ReadNamespaceMemberMoveProjectionAsync(operationId, durable).ConfigureAwait(false);
            if (projection is null)
            {
                projection = new(1, operationId, intent.ItemId, intent.RemoteId, projected.RemoteRevision,
                    intent.Target.RelativePath, DateTimeOffset.UtcNow);
                await _catalog.RecordNamespaceMemberMoveProjectionAsync(projection, durable).ConfigureAwait(false);
            }
            else if (projection.ProjectedRemoteRevision != projected.RemoteRevision)
                throw new InvalidDataException("Fresh official projection disagrees with the retained move observation.");
            await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection, durable).ConfigureAwait(false);

            var final = await InspectChildAsync(moved, childProof, intent.Target.RelativePath, intent, durable).ConfigureAwait(false);
            var finalParent = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, durable).ConfigureAwait(false);
            if (CloudPlaceholderIdentity.Decode(final.Snapshot.PlaceholderIdentity.Span).RemoteRevision != nativeIdentity.RemoteRevision)
                throw new InvalidDataException("The native identity changed during move completion.");
            await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, durable).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(durable).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(durable).ConfigureAwait(false));
            result = await _catalog.CompleteNamespaceMemberMoveAsync(new(1, operationId, intent.ItemId, intent.RemoteId,
                intent.Target.RelativePath, false, DateTimeOffset.UtcNow, Verification(final.Facts), Verification(finalParent),
                projected.RemoteRevision), durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }

    private async Task<MirrorPulseNamespaceProtectionProof> ReadProofAsync(Guid memberId, Guid protectionId,
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var snapshot = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("A move member lost its retained provenance.");
        return RequireAdmittedProof(snapshot, protectionId, intent);
    }

    private static MirrorPulseNamespaceProtectionProof RequireAdmittedProof(MirrorPulseNamespaceMemberProtectionSnapshot snapshot,
        Guid protectionId, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        var proof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(snapshot);
        if (proof.ProtectionId != protectionId || proof.RoleSid != intent.RoleSid || proof.OwnerSid != intent.OwnerSid || proof.RootId != intent.RootId)
            throw new InvalidOperationException("Move execution requires the current admitted protection epochs and role.");
        return proof;
    }

    private async Task RequireUnchangedAdmissionAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceProtectionProof child, MirrorPulseNamespaceProtectionProof source, MirrorPulseNamespaceProtectionProof target,
        CancellationToken token)
    {
        _actor.AssertAccess();
        if (await _catalog.ValidateNamespaceMemberMoveExecutionAsync(intent.OperationId, token).ConfigureAwait(false) != intent ||
            !await HasUnchangedProofsAsync(intent, child, source, target, token).ConfigureAwait(false))
            throw new InvalidOperationException("Move admission or protection changed during execution.");
    }

    private async Task<bool> HasUnchangedProofsAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceProtectionProof child, MirrorPulseNamespaceProtectionProof source, MirrorPulseNamespaceProtectionProof target,
        CancellationToken token) =>
            await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false) == child &&
            await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false) == source &&
            await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false) == target;

    private static void RequireCaller(MirrorPulseNamespaceMemberMoveIntent intent)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != intent.OwnerSid || !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(intent.RoleSid)))
            throw new UnauthorizedAccessException("Controlled movement requires the original owner and admitted namespace role.");
    }

    private async Task<(MirrorPulseNamespacePermissionObject Facts, CloudItemSnapshot Snapshot)> InspectChildAsync(
        CloudItem item, MirrorPulseNamespaceProtectionProof proof, string path, MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, _router, token).ConfigureAwait(false);
        var facts = await lease.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseNamespaceMemberMoveInspection.RequireChild(facts, snapshot, proof, path, intent);
        return (facts, snapshot);
    }

    private async Task<MirrorPulseNamespacePermissionObject> InspectParentAsync(CloudDirectory parent,
        MirrorPulseWindowsNamespacePermissionLease lease, MirrorPulseNamespaceProtectionProof proof,
        MirrorPulseNamespaceMoveEndpoint endpoint, MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var facts = await lease.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await parent.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseNamespaceMemberMoveInspection.RequireParent(facts, snapshot, proof, endpoint, intent);
        var member = await _catalog.ReadNamespaceMemberAsync(endpoint.ParentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The actual move parent lost its member registration.");
        if (proof.CurrentIdentity is null && member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var identity = await _catalog.ReadNamespacePermissionLocalIdentityAsync(member.OriginId, token).ConfigureAwait(false);
            if (identity is not null && (!snapshot.IsPlaceholder || snapshot.ItemId != identity.ItemId || snapshot.RemoteId != identity.RemoteId))
                throw new InvalidDataException("The actual original parent lost its retained public identity.");
        }
        return facts;
    }

    private Task<MirrorPulseNamespaceMemberLocationCandidate> ReadProjectionAsync(Guid memberId, CancellationToken token) =>
        _execution.RunNormalUserOperationAsync(() => MirrorPulseNamespaceMemberLocator.ResolveAsync(_catalog, _state.OpenStore, memberId, _actor, token));

    private static void RequireProjection(MirrorPulseNamespaceMemberLocationCandidate projection,
        MirrorPulseNamespaceMemberMoveIntent intent, string? expectedPath)
    {
        string? projectedPath = projection.RelativePath?.Replace('\\', '/');
        if (projection.State != MirrorPulseNamespaceMemberLocationState.Projected || projection.ItemId != intent.ItemId || projection.RemoteId != intent.RemoteId ||
            (expectedPath is not null ? !string.Equals(projectedPath, expectedPath, StringComparison.OrdinalIgnoreCase) :
                !string.Equals(projectedPath, intent.Source.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(projectedPath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The official item does not identify the admitted move object and recovery path.");
    }

    private ValueTask CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint checkpoint, CancellationToken token) =>
        _checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;

    private static string TargetName(MirrorPulseNamespaceMemberMoveIntent intent) => intent.Target.RelativePath[(intent.Target.RelativePath.LastIndexOf('/') + 1)..];
    private static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject facts) =>
        new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt);
    private static bool SameProtection(MirrorPulseNamespacePermissionVerification retained, MirrorPulseNamespacePermissionObject fresh) =>
        retained.LocalObject == fresh.LocalObject && retained.OwnerSid == fresh.OwnerSid && retained.Dacl == fresh.Dacl;
}
