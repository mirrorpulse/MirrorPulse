using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    // Runs as the ordinary user while the actor, per-instance admissions and original source guards are held.
    private async Task RequireOriginalDirectoryRemoteSourceAsync(MirrorPulseDirectoryTransferAdmission plan,
        MirrorPulseTransferWorkerServices workers, CancellationToken token)
    {
        var route = plan.SourceRoute;
        var root = router.GetRegistration(route.InstanceId, route.RootKey);
        var metadata = new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories);
        var remoteDirectories = new Dictionary<Guid, (string Path, string Revision, List<MirrorPulseWorkerDirectoryEntry> Children)>();
        var remoteIds = new HashSet<string>(StringComparer.Ordinal);
        int prefixLength = route.DirectoryName.Length + 1;
        for (int index = 0; index < plan.Members.Count; index++)
        {
            var member = plan.Members[index];
            var original = member.Source.Protection;
            var context = original.Context;
            string path = context.Location.RelativePath[prefixLength..];
            Guid parent = context.Location.ParentMemberId!.Value;
            bool hasRemoteParent = index == 0 || remoteDirectories.ContainsKey(parent);
            if (member.SourceRevision is not { } revision)
            {
                if (await workers.Stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) is not null)
                    throw new IOException("The original local-only transfer member is no longer absent remotely.");
                // Retained remote parent inventories prove absence for their local-only children.
                // For the subtree root the external parent is not part of that inventory.
                if (index == 0)
                    await metadata.RequireAbsentEntryAsync(route, path, token).ConfigureAwait(false);
                continue;
            }
            if (!hasRemoteParent ||
                await workers.Stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != revision)
                throw new IOException("The original remote directory transfer source revision changed.");
            var entry = context.Member.IsDirectory
                ? await metadata.ReadDirectoryMetadataAsync(route, path, revision, token).ConfigureAwait(false)
                : await metadata.ReadMetadataAsync(route, path, null, revision, token).ConfigureAwait(false);
            var identity = new CloudPlaceholderIdentity(original.Identity.ItemId!.Value, original.Identity.RemoteId!, revision);
            if (!remoteIds.Add(entry.RemoteId) || !MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, entry.RemoteId, identity))
                throw new IOException("The source path no longer identifies its original remote object.");
            if (index != 0) remoteDirectories[parent].Children.Add(entry);
            if (context.Member.IsDirectory)
                remoteDirectories.Add(context.Member.MemberId, (path, revision, []));
        }

        foreach (var (path, revision, children) in remoteDirectories.Values)
        {
            await metadata.RequireDirectoryInventoryAsync(route, path, children, token).ConfigureAwait(false);
            if (await workers.Stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != revision)
                throw new IOException("An original source directory changed during inventory verification.");
        }
    }
}
