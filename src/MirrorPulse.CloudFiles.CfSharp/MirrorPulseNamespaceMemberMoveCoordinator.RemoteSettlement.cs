using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Records the fresh pre-native boundary for the original remote batch without executing it.</summary>
    /// <remarks>An existing start requires outcome recovery, not permission to dispatch again.</remarks>
    public ValueTask<MirrorPulseNamespaceMemberMoveStart> BeginRemoteAsync(Guid operationId, InstanceId instanceId,
        CloudRemoteChangeBatch batch, int ordinal, CancellationToken cancellationToken = default)
    {
        var request = new RemoteMoveRequest(instanceId, batch, ordinal); _ = RequireRemoteChange(request);
        return _actor.RunAsync(token => BeginRemoteAdmittedAsync(operationId, request, token), cancellationToken);
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveStart> BeginRemoteAdmittedAsync(Guid operationId,
        RemoteMoveRequest request, CancellationToken token, bool recoverUnmovedStart = false,
        Func<CancellationToken, ValueTask>? dispatch = null)
    {
        _actor.AssertAccess();
        var intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
        RequireRemoteRequestReplay(intent, request);
        RequireActiveRemoteRoute(intent);
        var originalStart = await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false);
        if (!recoverUnmovedStart && originalStart is not null)
            throw new InvalidOperationException("An existing remote start requires original outcome recovery before another dispatch.");
        if (recoverUnmovedStart && originalStart?.Version != 2)
            throw new InvalidOperationException("Original outcome recovery requires its retained actual pre-dispatch revision.");
        await RequireRemoteProgressAsync(request, completed: false, token).ConfigureAwait(false);
        var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
        var sourceProof = await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false);
        var targetProof = await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false);
        var member = await _catalog.ReadNamespaceMemberAsync(intent.MemberId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original remote move member is missing.");
        CloudItem source = member.IsDirectory ? _fileSystem.GetDirectory(intent.Source.RelativePath) : _fileSystem.GetFile(intent.Source.RelativePath);
        var sourceParent = source.Parent ?? throw new InvalidDataException("The remote move requires its original source parent.");
        CloudItem target = member.IsDirectory ? _fileSystem.GetDirectory(intent.Target.RelativePath) : _fileSystem.GetFile(intent.Target.RelativePath);
        var targetParent = target.Parent
            ?? throw new InvalidDataException("The remote move requires its original target parent.");
        MirrorPulseNamespaceMemberMoveStart result = null!;
        await _execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(intent);
            await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceParent, _router, token).ConfigureAwait(false);
            await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetParent, _router, token).ConfigureAwait(false);
            var beforeSource = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
            var beforeTarget = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
            var observed = await InspectChildAsync(source, childProof, intent.Source.RelativePath, intent, token).ConfigureAwait(false);
            if (CloudPlaceholderIdentity.Decode(observed.Snapshot.PlaceholderIdentity.Span).RemoteRevision != intent.RemoteEntry!.OriginalRemoteRevision)
                throw new InvalidDataException("The original remote move identity changed before its start.");
            var originalProjection = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
            RequireProjection(originalProjection, intent, intent.Source.RelativePath);
            if (recoverUnmovedStart && originalProjection.RemoteRevision != originalStart!.OriginalOfficialRemoteRevision)
                throw new InvalidDataException("The official source changed after the retained remote start.");
            if (recoverUnmovedStart && string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase))
                await RequireActualRemoteSpellingAsync(sourceParent, intent.Source.RelativePath, token).ConfigureAwait(false);
            if (!string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                (await target.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The original remote move target is occupied.");
            if (member.IsDirectory)
            {
                if (recoverUnmovedStart)
                {
                    var proof = CloudDirectoryMoveProof.Decode(Convert.FromBase64String(intent.DirectoryMoveEvidence!));
                    RequireDirectoryMoveProof(proof, intent);
                    var recovered = await ((CloudDirectory)source).ReconcileMoveAsync(proof, token).ConfigureAwait(false);
                    if (recovered.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || recovered.NativeMoveObserved || recovered.DurableProjectionCommitted)
                        throw new InvalidDataException("The original public directory proof does not establish an unmoved source.");
                }
                var tree = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original remote directory has no sealed subtree.");
                await using var objects = await OpenDirectoryMoveObjectsAsync(tree.Entries, intent, atTarget: false, token).ConfigureAwait(false);
                foreach (var entry in tree.Entries)
                    RequireDirectoryMemberProjection(await ReadProjectionAsync(entry.MemberId, token).ConfigureAwait(false), entry, intent,
                        atTarget: false, allowOriginal: false);
                await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: false, token).ConfigureAwait(false);
            }
            await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeSource, await sourceLease.InspectAsync(token).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeTarget, await targetLease.InspectAsync(token).ConfigureAwait(false));
            var finalProjection = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
            RequireProjection(finalProjection, intent, intent.Source.RelativePath);
            if (finalProjection.RemoteRevision != originalProjection.RemoteRevision)
                throw new InvalidDataException("The official source revision changed before the original remote start.");
            result = originalStart ?? await _catalog.RecordNamespaceMemberMoveStartAsync(new(2, operationId, DateTimeOffset.UtcNow)
            { OriginalOfficialRemoteRevision = finalProjection.RemoteRevision }, token).ConfigureAwait(false);
            if (dispatch is not null)
            {
                // Retain the actor, current namespace role and original parent guards across
                // dispatch. Fresh source inspection released its child handle for the SDK move.
                await RequireRemoteProgressAsync(request, completed: false, token).ConfigureAwait(false);
                if (await _catalog.ReadNamespaceMemberMoveObservationAsync(operationId, token).ConfigureAwait(false) is not null ||
                    await _catalog.ReadNamespaceMemberMoveProjectionAsync(operationId, token).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("A remote start with successful move evidence cannot dispatch from its source.");
                await dispatch(token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return result;
    }

    /// <summary>Resumes only an original start whose complete fresh evidence proves the native move did not occur.</summary>
    /// <remarks>This is a source recovery boundary, not blind replay. A moved, missing, replaced,
    /// case-renamed, reprotected or otherwise uncertain object remains fenced.</remarks>
    internal ValueTask<CloudRemoteApplyResult> ResumeUnmovedRemoteAsync(Guid operationId, InstanceId instanceId,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, CancellationToken cancellationToken)
    {
        var request = new RemoteMoveRequest(instanceId, batch, ordinal); _ = RequireRemoteChange(request);
        return _actor.RunAsync(async token =>
        {
            CloudRemoteApplyResult result = null!;
            _ = await BeginRemoteAdmittedAsync(operationId, request, token, recoverUnmovedStart: true,
                dispatch: async admitted => result = await _fileSystem.ApplyRemoteChangesAsync(batch,
                    options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false)).ConfigureAwait(false);
            return result;
        }, cancellationToken);
    }

    /// <summary>Settles the original batch's actual target using retained bindings and public CfSharp progress.</summary>
    /// <remarks>This never issues a native move or acknowledges local content. Directory path reconciliation uses the original public proof.</remarks>
    public ValueTask<MirrorPulseNamespaceMemberMoveCompletion> SettleRemoteAsync(Guid operationId, InstanceId instanceId,
        CloudRemoteChangeBatch batch, int ordinal, CancellationToken cancellationToken = default)
    {
        var request = new RemoteMoveRequest(instanceId, batch, ordinal); _ = RequireRemoteChange(request);
        return _actor.RunAsync(async token =>
        {
            var intent = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote move admission is missing.");
            RequireRemoteRequestReplay(intent, request);
            if (await _catalog.ReadNamespaceMemberMoveCompletionAsync(operationId, token).ConfigureAwait(false) is { } completed)
                return completed;
            RequireActiveRemoteRoute(intent);
            if (await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false) is null)
                throw new InvalidDataException("Remote settlement cannot invent a before-native start.");
            await RequireRemoteProgressAsync(request, completed: true, token).ConfigureAwait(false);
            return await ExecuteAdmittedAsync(operationId, token, observeRemoteOnly: true).ConfigureAwait(false);
        }, cancellationToken);
    }

    private void RequireActiveRemoteRoute(MirrorPulseNamespaceMemberMoveIntent intent)
    {
        foreach (string path in new[] { intent.Source.RelativePath, intent.Target.RelativePath })
        {
            var route = _router.ResolveCurrentPath(path);
            var root = _router.GetRegistration(route.InstanceId, route.RootKey);
            if (route.InstanceId != intent.RemoteEntry!.InstanceId || root.RootId != intent.RootId || root.State != RootRegistrationState.Active)
                throw new InvalidOperationException("An offline or rerouted root retains its original remote move for recovery.");
        }
    }

    private Task RequireRemoteProgressAsync(RemoteMoveRequest request, bool completed, CancellationToken token) =>
        _execution.RunNormalUserOperationAsync(async () =>
        {
            await using var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
            var progress = await transaction.RemoteBatches.GetAsync(request.Batch.BatchId, token).ConfigureAwait(false);
            int expectedCount = request.Ordinal + (completed ? 1 : 0);
            if (progress is null ? expectedCount != 0 :
                progress.TotalEntryCount != request.Batch.Changes.Count ||
                !progress.Fingerprint.Span.SequenceEqual(request.Batch.Fingerprint.Span) || progress.AppliedEntryCount != expectedCount ||
                (expectedCount > 0 && progress.LastAppliedChangeId != request.Batch.Changes[expectedCount - 1].ChangeId) ||
                (progress.Status == CloudRemoteBatchStatus.Applied) != (expectedCount == request.Batch.Changes.Count))
                throw new InvalidDataException("The original remote entry is not at its required public batch boundary.");
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        });

    private static async Task RequireActualTargetSpellingAsync(CloudDirectory parent, MirrorPulseNamespaceMemberMoveIntent intent,
        CancellationToken token) => await RequireActualRemoteSpellingAsync(parent, intent.Target.RelativePath, token).ConfigureAwait(false);

    private static async Task RequireActualRemoteSpellingAsync(CloudDirectory parent, string expectedPath, CancellationToken token)
    {
        int observed = 0;
        await foreach (var item in parent.EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, token).ConfigureAwait(false))
        {
            if (++observed > 65_536) throw new InvalidOperationException("The remote case-only move parent exceeds its bounded inspection limit.");
            if (string.Equals(item.RelativePath.Replace('\\', '/'), expectedPath, StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("The remote case-only move has not established its actual target spelling.");
    }
}
