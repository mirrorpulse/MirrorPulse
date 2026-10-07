using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Projects an accepted directory namespace through the public CfSharp owner.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public static class MirrorPulseDirectoryConfirmation
{
    public static async ValueTask ConfirmAsync(CloudFileSystem fileSystem, MirrorPulseRootRouter router,
        MirrorPulseMutationIntent intent, MirrorPulseWorkerDirectoryEntry remote, string? acceptedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(remote);
        if (!intent.IsDirectory || remote.IsDeleted || !string.Equals(remote.ItemKind, "Directory", StringComparison.OrdinalIgnoreCase) ||
            acceptedRevision is null || remote.RemoteRevision != acceptedRevision)
            throw new MirrorPulseMutationAmbiguousException("The accepted directory metadata changed before projection.");
        string localPath = router.ResolveUploadPath(intent.InstanceId, intent.RootKey, intent.RelativePath);
        string relative = Path.GetRelativePath(fileSystem.Root.FullPath, localPath).Replace(Path.DirectorySeparatorChar, '/');
        CloudDirectory directory = fileSystem.GetDirectory(relative);
        CloudItemSnapshot snapshot = await directory.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists || !Directory.Exists(localPath))
            throw new MirrorPulseMutationAmbiguousException("The accepted local directory disappeared.");
        CloudPlaceholderIdentity identity = router.CreateFileIdentity(intent.InstanceId, intent.RootKey, remote.RemoteId, acceptedRevision);
        if (snapshot.IsPlaceholder)
        {
            if (CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).ItemId != identity.ItemId)
                throw new MirrorPulseMutationAmbiguousException("The local directory belongs to another object.");
            await directory.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentity(identity)
                .WithInSyncState(true).Build(), cancellationToken).ConfigureAwait(false);
        }
        else
            await directory.ConvertToPlaceholderAsync(identity, CloudPlaceholderConversionOptions.CreateBuilder()
                .WithInSyncState().WithPopulationState(CloudDirectoryPopulationState.Partial).Build(), cancellationToken).ConfigureAwait(false);
    }
}
