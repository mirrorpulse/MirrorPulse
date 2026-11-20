using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Correlates the exact CfSharp root Move with the previously settled controlled subtree.</summary>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseJournalDirectoryMoveAdmissionReader(MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, MirrorPulseRootRouter router, CloudFileSystem fileSystem)
{
    internal async Task<MirrorPulseJournalDirectoryMoveAdmission?> ReadAsync(MirrorPulseWorkerChangeCommand command,
        CancellationToken token)
    {
        if (command.Kind != MirrorPulseWorkerChangeKind.Move || !command.IsDirectory ||
            command.PreviousRelativePath is null || command.ItemId is not { } rootItemId ||
            (command.PreviousRootKey ?? command.RootKey) != command.RootKey ||
            (command.PreviousInstanceId ?? command.InstanceId) != command.InstanceId)
            return null;

        var route = router.GetRegistration(command.InstanceId, command.RootKey);
        if (route.State != RootRegistrationState.Active) return null;
        string source = SyncPath(command.PreviousRelativePath);
        string target = SyncPath(command.RelativePath);
        var rootSnapshot = await fileSystem.GetDirectory(target).InspectAsync(token).ConfigureAwait(false);
        if (ReadBinding(rootSnapshot) is not { } binding) return null;
        var registered = await catalog.ReadNamespaceMemberByBindingAsync(binding, token).ConfigureAwait(false);
        if (registered is null || !registered.IsDirectory || registered.RootId != route.RootId) return null;
        var location = await catalog.ReadNamespaceMemberLocationAsync(registered.MemberId, token).ConfigureAwait(false);
        if (location.RelativePath != target || location.CurrentPositionOperationId is not { } positionId) return null;
        var localMove = await catalog.ReadNamespaceMemberMoveAsync(positionId, token).ConfigureAwait(false);
        if (localMove is null || localMove.MemberId != registered.MemberId || localMove.RootId != route.RootId ||
            localMove.ItemId != rootItemId || localMove.Source.RelativePath != source || localMove.Target.RelativePath != target ||
            localMove.DirectoryMoveEvidence is null || localMove.Version != 1 || localMove.RemoteEntry is not null)
            return null;

        var seal = await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(positionId, token).ConfigureAwait(false);
        var completion = await catalog.ReadNamespaceDirectoryMoveCompletionAsync(positionId, token).ConfigureAwait(false);
        if (seal is null || completion is null || seal.Entries.Count == 0 || completion.Entries.Count != seal.Entries.Count)
            throw new InvalidDataException("The controlled directory Move lost its original subtree or completion.");
        string[] targets = new string[seal.Entries.Count];
        for (int index = 0; index < targets.Length; index++)
        {
            var entry = seal.Entries[index];
            if (index == 0 ? entry.MemberId != registered.MemberId || entry.RelativePath != source :
                !entry.RelativePath.StartsWith(source + "/", StringComparison.Ordinal))
                throw new InvalidDataException("The sealed directory member is outside its original move.");
            targets[index] = target + entry.RelativePath[source.Length..];
            var current = await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, token).ConfigureAwait(false);
            if (current.RelativePath != targets[index] || current.CurrentPositionOperationId != positionId)
                throw new MirrorPulseMutationAmbiguousException("A later namespace position superseded the original directory Move.");
        }

        var members = new List<MirrorPulseJournalDirectoryMoveMember>(targets.Length);
        for (int index = 0; index < targets.Length; index++)
        {
            var before = seal.Entries[index];
            var settled = completion.Entries[index].Completion;
            var currentIdentity = await catalog.ReadNamespaceMemberIdentityAsync(before.MemberId, token).ConfigureAwait(false);
            if (before.Identity != currentIdentity || currentIdentity.ItemId != settled.ItemId || currentIdentity.RemoteId != settled.RemoteId)
                throw new MirrorPulseMutationAmbiguousException("A later public identity superseded the original directory Move member.");
            CloudItem item = before.IsDirectory ? fileSystem.GetDirectory(targets[index]) : fileSystem.GetFile(targets[index]);
            var observed = await item.InspectAsync(token).ConfigureAwait(false);
            if (ReadBinding(observed) != before.LocalObject || observed.Kind != (before.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File))
                throw new MirrorPulseMutationAmbiguousException("The original directory Move member no longer occupies its committed path.");
            var identity = CloudPlaceholderIdentity.Decode(observed.PlaceholderIdentity.Span);
            if (identity.ItemId != settled.ItemId || identity.RemoteId != settled.RemoteId ||
                identity.RemoteRevision != completion.Entries[index].Native.NativeRemoteRevision ||
                !MirrorPulsePlaceholderIdentity.BelongsToRoot(route, identity))
                throw new MirrorPulseMutationAmbiguousException("The current placeholder differs from the original directory Move member.");
            members.Add(new(before.MemberId, before.LocalObject, before.RelativePath, targets[index], before.IsDirectory,
                settled.ItemId, settled.RemoteId, settled.OfficialRemoteRevision, identity.RemoteRevision, currentIdentity.AcceptanceOperationId));
        }

        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var original = await transaction.Operations.GetAsync(command.OperationId, token).ConfigureAwait(false);
        if (original is null || original.Kind != CloudStateOperationKind.Move || original.Sequence != command.Sequence || original.ItemId != rootItemId)
            throw new InvalidDataException("The official directory Move original changed before admission.");
        foreach (var member in members)
        {
            var row = await transaction.Items.GetByItemIdAsync(member.OriginalItemId, token).ConfigureAwait(false);
            if (row is null || row.IsTombstone || row.Kind != (member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File) ||
                row.RelativePath.Replace('\\', '/') != member.TargetPath || row.RemoteId != member.OriginalRemoteId || row.RemoteRevision != member.OriginalRevision)
                throw new InvalidDataException("The official directory Move member differs from the original accepted baseline.");
        }
        string digest = Convert.ToHexString(SHA256.HashData(original.Payload.Span));
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return new(command.OperationId, command.Sequence, digest, positionId, route.RootId,
            command.InstanceId, command.RootKey, members.AsReadOnly());

        string SyncPath(string path) => Path.GetRelativePath(fileSystem.Root.FullPath,
            router.ResolveUploadPath(command.InstanceId, command.RootKey, path)).Replace('\\', '/');
    }

    private static MirrorPulseLocalFileBinding? ReadBinding(CloudItemSnapshot snapshot) =>
        snapshot.Exists && snapshot.IsPlaceholder && !snapshot.IsTombstone && snapshot.LocalBinding is { } binding
            ? new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) : null;
}
