using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private const string RemoteFilePackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion;
    private const string RemoteFilePackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit;

    private static MirrorPulseRemoteFileMovePolicy RemoteFilePolicy(CloudRemoteApplyOptions options) => new(
        options.PreserveUnsynchronizedLocalContent, options.SuppressLocalEcho,
        options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount);

    internal async ValueTask<CloudRemoteApplyResult> ResumeOriginalRemoteAsync(Guid operationId, InstanceId instanceId,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, CancellationToken cancellationToken)
    {
        // Absence of historical protocol admission never becomes permission to
        // replay a moved legacy object. A freshly proven source may enter the
        // new protocol while preserving its original start.
        var protocol = await _catalog.ReadNamespaceRemoteFileMoveProtocolAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (protocol is null)
            return await ResumeUnmovedRemoteAsync(operationId, instanceId, batch, ordinal, options, cancellationToken).ConfigureAwait(false);
        var request = new RemoteMoveRequest(instanceId, batch, ordinal); _ = RequireRemoteChange(request);
        return await _actor.RunAsync(async token =>
        {
            var intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
            RequireRemoteRequestReplay(intent, request); RequireActiveRemoteRoute(intent);
            var retained = await _catalog.ReadNamespaceRemoteFileMoveProtocolAsync(operationId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original SDK protocol admission disappeared.");
            if (retained != protocol || !MirrorPulseRemoteLibraryProvenance.IsSupported(protocol.PackageVersion, protocol.PackageSourceCommit) ||
                protocol.Policy != RemoteFilePolicy(options))
                throw new InvalidDataException("Recovery requires the originally admitted SDK protocol and public apply policy.");
            await RequireRemoteProgressAsync(request, completed: false, token).ConfigureAwait(false);
            var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
            var sourceProof = await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false);
            var targetProof = await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false);
            var source = _fileSystem.GetFile(intent.Source.RelativePath); var target = _fileSystem.GetFile(intent.Target.RelativePath);
            var sourceParent = source.Parent ?? throw new InvalidDataException("The original source parent is missing.");
            var targetParent = target.Parent ?? throw new InvalidDataException("The original target parent is missing.");
            CloudRemoteApplyResult result = null!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                RequireCaller(intent);
                await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceParent, _router, token).ConfigureAwait(false);
                await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetParent, _router, token).ConfigureAwait(false);
                var sourceBefore = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
                var targetBefore = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
                var atSource = await source.InspectAsync(token).ConfigureAwait(false);
                var atTarget = await target.InspectAsync(token).ConfigureAwait(false);
                bool sameName = string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase);
                if (!atSource.Exists && !atTarget.Exists || !sameName && atSource.Exists && atTarget.Exists)
                    throw new InvalidDataException("The original remote file is absent or has an unrelated path occupant.");
                bool moved = !atSource.Exists;
                if (sameName)
                    moved = await IsOriginalRemoteTargetSpellingAsync(sourceParent, intent, token).ConfigureAwait(false);
                var original = await InspectChildAsync(moved ? target : source, childProof,
                    moved ? intent.Target.RelativePath : intent.Source.RelativePath, intent, token).ConfigureAwait(false);
                if (CloudPlaceholderIdentity.Decode(original.Snapshot.PlaceholderIdentity.Span).RemoteRevision != intent.RemoteEntry!.OriginalRemoteRevision)
                    throw new InvalidDataException("The retained original native identity changed before SDK recovery.");
                var projection = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
                RequireProjection(projection, intent, null);
                string? path = projection.RelativePath?.Replace('\\', '/');
                bool originalProjection = string.Equals(path, intent.Source.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    projection.RemoteRevision == protocol.OriginalStart.OriginalOfficialRemoteRevision;
                bool targetProjection = string.Equals(path, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    projection.RemoteRevision == intent.RemoteEntry.RemoteRevision;
                if ((!originalProjection && !targetProjection) || !moved && !originalProjection)
                    throw new InvalidDataException("The official item is outside the original source or incoming target boundary.");
                await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(token).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(token).ConfigureAwait(false));
                // Product admission authenticates our original member/parents.
                // CfSharp alone authenticates its opaque preparation and decides
                // whether native work, projection or only outcome is missing.
                result = await _fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsOriginalRemoteTargetSpellingAsync(CloudDirectory parent,
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        int inspected = 0;
        await foreach (var child in parent.EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, token).ConfigureAwait(false))
        {
            if (++inspected > 65_536) throw new InvalidOperationException("The original remote move parent exceeds its bounded inspection limit.");
            string path = child.RelativePath.Replace('\\', '/');
            if (string.Equals(path, intent.Target.RelativePath, StringComparison.Ordinal)) return true;
            if (string.Equals(path, intent.Source.RelativePath, StringComparison.Ordinal)) return false;
        }
        throw new InvalidDataException("The original file is not at either admitted exact spelling.");
    }
}
