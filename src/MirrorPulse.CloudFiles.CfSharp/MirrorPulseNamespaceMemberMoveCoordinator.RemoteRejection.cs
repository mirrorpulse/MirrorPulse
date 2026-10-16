using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Settles a durable SDK conflict only after proving the original object remained at its source.</summary>
    /// <remarks>This never moves an object, resolves the SDK conflict, publishes a cursor or acknowledges content.</remarks>
    public ValueTask<MirrorPulseNamespaceRemoteMoveRejection> RejectRemoteAsync(Guid operationId, InstanceId instanceId,
        CloudRemoteChangeBatch batch, int ordinal, Guid conflictId, CancellationToken cancellationToken = default)
    {
        var request = new RemoteMoveRequest(instanceId, batch, ordinal); _ = RequireRemoteChange(request);
        if (conflictId == Guid.Empty) throw new ArgumentException("The original durable SDK conflict is required.", nameof(conflictId));
        return _actor.RunAsync(async token =>
        {
            var intent = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote move admission is missing.");
            RequireRemoteRequestReplay(intent, request);
            if (await _catalog.ReadNamespaceRemoteMoveRejectionAsync(operationId, token).ConfigureAwait(false) is { } rejected)
                return rejected.ConflictId == conflictId ? rejected : throw new InvalidOperationException("The remote rejection belongs to a different SDK conflict.");
            RequireActiveRemoteRoute(intent);
            var start = await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Remote rejection cannot invent a before-native start.");
            await RequireRemoteProgressAsync(request, completed: true, token).ConfigureAwait(false);
            var conflict = await RequireOriginalRemoteConflictAsync(request, conflictId, start, token).ConfigureAwait(false);
            var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
            var sourceProof = await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false);
            var targetProof = await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false);
            var member = await _catalog.ReadNamespaceMemberAsync(intent.MemberId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote member is missing.");
            CloudItem source = member.IsDirectory ? _fileSystem.GetDirectory(intent.Source.RelativePath) : _fileSystem.GetFile(intent.Source.RelativePath);
            CloudItem target = member.IsDirectory ? _fileSystem.GetDirectory(intent.Target.RelativePath) : _fileSystem.GetFile(intent.Target.RelativePath);
            CloudDirectory sourceParent = source.Parent ?? throw new InvalidDataException("The original remote source parent is missing.");
            CloudDirectory targetParent = target.Parent ?? throw new InvalidDataException("The original remote target parent is missing.");
            MirrorPulseNamespaceRemoteMoveRejection result = null!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                RequireCaller(intent);
                await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceParent, _router, token).ConfigureAwait(false);
                await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetParent, _router, token).ConfigureAwait(false);
                var beforeSource = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
                var beforeTarget = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
                var original = await InspectChildAsync(source, childProof, intent.Source.RelativePath, intent, token).ConfigureAwait(false);
                string? nativeRevision = CloudPlaceholderIdentity.Decode(original.Snapshot.PlaceholderIdentity.Span).RemoteRevision;
                var projection = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
                RequireProjection(projection, intent, intent.Source.RelativePath);
                if (nativeRevision != intent.RemoteEntry!.OriginalRemoteRevision || projection.RemoteRevision != intent.RemoteEntry.PreviousRemoteRevision)
                    throw new InvalidDataException("The original remote source has changed identity or official revision.");
                if (string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase))
                    await RequireActualRemoteSpellingAsync(sourceParent, intent.Source.RelativePath, token).ConfigureAwait(false);
                else if ((await target.InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new InvalidDataException("An occupied target prevents proving that the original remote move was rejected.");
                if (member.IsDirectory)
                {
                    var tree = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The original remote directory lost its sealed subtree.");
                    var proof = CloudDirectoryMoveProof.Decode(Convert.FromBase64String(intent.DirectoryMoveEvidence!));
                    RequireDirectoryMoveProof(proof, intent);
                    var recovered = await ((CloudDirectory)source).ReconcileMoveAsync(proof, token).ConfigureAwait(false);
                    if (recovered.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || recovered.NativeMoveObserved || recovered.DurableProjectionCommitted)
                        throw new InvalidDataException("The original public directory proof does not establish an unmoved source.");
                    await using var objects = await OpenDirectoryMoveObjectsAsync(tree.Entries, intent, atTarget: false, token).ConfigureAwait(false);
                    foreach (var entry in tree.Entries)
                        RequireDirectoryMemberProjection(await ReadProjectionAsync(entry.MemberId, token).ConfigureAwait(false), entry, intent, atTarget: false, allowOriginal: false);
                    await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: false, token).ConfigureAwait(false);
                }
                await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false);
                var finalChild = await InspectChildAsync(source, childProof, intent.Source.RelativePath, intent, token).ConfigureAwait(false);
                if (CloudPlaceholderIdentity.Decode(finalChild.Snapshot.PlaceholderIdentity.Span).RemoteRevision != nativeRevision)
                    throw new InvalidDataException("The original source identity changed during remote rejection verification.");
                var finalSource = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
                var finalTarget = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeSource, finalSource);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeTarget, finalTarget);
                var finalProjection = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
                if (finalProjection != projection) throw new InvalidDataException("The source official projection changed during rejection verification.");
                await RequireRemoteProgressAsync(request, completed: true, token).ConfigureAwait(false);
                var currentConflict = await RequireOriginalRemoteConflictAsync(request, conflictId, start, token).ConfigureAwait(false);
                if (currentConflict.DetectedAt != conflict.DetectedAt)
                    throw new InvalidDataException("The original durable SDK conflict changed during verification.");
                result = await _catalog.RecordNamespaceRemoteMoveRejectionAsync(new(1, operationId, conflictId, conflict.DetectedAt,
                    DateTimeOffset.UtcNow, nativeRevision, projection.RemoteRevision, Verification(finalChild.Facts),
                    Verification(finalSource), Verification(finalTarget)), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, cancellationToken);
    }

    private Task<CloudRemoteConflict> RequireOriginalRemoteConflictAsync(RemoteMoveRequest request, Guid conflictId,
        MirrorPulseNamespaceMemberMoveStart start, CancellationToken token) => _execution.RunNormalUserOperationAsync(async () =>
        {
            var record = await _fileSystem.GetRemoteConflictAsync(conflictId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original unresolved SDK conflict is missing.");
            var changes = request.Batch.Changes.ToArray(); changes[request.Ordinal] = record.Conflict.Change;
            var observed = new CloudRemoteChangeBatch(request.Batch.BatchId, request.Batch.InitialCursor, changes, request.Batch.FinalCursor);
            if (!observed.Fingerprint.Span.SequenceEqual(request.Batch.Fingerprint.Span) || record.Conflict.DetectedAt < start.StartedAt)
                throw new InvalidDataException("The durable SDK conflict does not match the original remote entry and start.");
            return record.Conflict;
        });
}
