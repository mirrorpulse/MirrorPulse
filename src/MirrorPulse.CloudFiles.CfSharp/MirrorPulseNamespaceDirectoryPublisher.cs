using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Publishes source pages locally before the library completes an external enumeration.</summary>
/// <remarks>The source page is fetched under the normal user before entering this actor. Roots
/// require live runtime admission. Existing objects are audited, never adopted or overwritten by
/// their names. Pending births retain their fences for explicit recovery.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseNamespaceDirectoryPublisher(Func<CloudFileSystem> currentFileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    private int _active;

    internal Task CloseAndDrainAsync()
    {
        lock (_gate)
        {
            _closing = true;
            if (_active == 0) _drained.TrySetResult();
            return _drained.Task;
        }
    }

    internal async ValueTask<CloudProviderDirectoryPage> PublishAsync(string path, ReadOnlyMemory<byte> directoryIdentity,
        CloudProviderDirectoryPage page, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _active++;
        }
        try { return await PublishUnderActorAsync(path, directoryIdentity, page, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (--_active == 0 && _closing) _drained.TrySetResult();
            }
        }
    }

    private ValueTask<CloudProviderDirectoryPage> PublishUnderActorAsync(string path, ReadOnlyMemory<byte> directoryIdentity,
        CloudProviderDirectoryPage page, CancellationToken cancellationToken) => actor.RunAsync(async token =>
        {
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var fileSystem = currentFileSystem();
                var parent = fileSystem.GetDirectory(path);
                var snapshot = await parent.InspectAsync(token).ConfigureAwait(false);
                if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
                    snapshot.PlaceholderIdentity.IsEmpty ||
                    !snapshot.PlaceholderIdentity.Span.SequenceEqual(directoryIdentity.Span))
                    throw new InvalidDataException("Source population lost its actual directory identity or binding.");
                var member = await catalog.ReadNamespaceMemberByBindingAsync(new(binding.VolumeSerialNumber,
                    binding.SyncRootFileId, binding.LocalFileId), token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Source population requires registered parent provenance.");
                var current = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Source population requires current parent protection.");
                if (member.RootId is null || !member.IsDirectory || current.Context.Location.RelativePath != parent.RelativePath.Replace('\\', '/'))
                    throw new InvalidDataException("The source parent disagrees with its committed managed location.");
                var ancestor = current.Context.Location.ParentMemberId is { } parentId
                    ? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parentId, token).ConfigureAwait(false) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
                await MirrorPulseNamespaceMemberRegistrar.InspectCurrentAsync(catalog, current, ancestor, parent,
                    parent.Parent, router, actor, token).ConfigureAwait(false);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in page.Children)
                {
                    _ = spec.Kind switch
                    {
                        CloudItemKind.File => (CloudItem)parent.GetFile(spec.Name),
                        CloudItemKind.Directory => parent.GetDirectory(spec.Name),
                        _ => throw new InvalidDataException("Source pages require supported child kinds.")
                    };
                    if (!names.Add(spec.Name)) throw new InvalidDataException("Source pages contain duplicate child names.");
                }
                var creator = new MirrorPulseNamespaceRemoteCreationCoordinator(fileSystem, catalog, router, execution, actor);
                foreach (var spec in page.Children)
                {
                    var existing = await catalog.ReadNamespaceMemberChildAsync(member.MemberId, spec.Name, token).ConfigureAwait(false);
                    if (existing is null)
                    {
                        _ = await creator.CreateUnderActorAsync(Guid.NewGuid(), member.MemberId, spec, token).ConfigureAwait(false);
                        continue;
                    }
                    CloudItem child = spec.Kind == CloudItemKind.Directory ? parent.GetDirectory(spec.Name) : parent.GetFile(spec.Name);
                    var childCurrent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(existing.MemberId, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The source child requires current protected membership.");
                    await MirrorPulseNamespaceMemberRegistrar.InspectCurrentAsync(catalog, childCurrent, current, child, parent,
                        router, actor, token).ConfigureAwait(false);
                    var childSnapshot = await child.InspectAsync(token).ConfigureAwait(false);
                    if (childSnapshot.Kind != spec.Kind || childSnapshot.PlaceholderIdentity.IsEmpty ||
                        !childSnapshot.PlaceholderIdentity.Span.SequenceEqual(spec.Identity.Encode()))
                        throw new InvalidOperationException("An occupied source child requires remote change reconciliation before population.");
                }
            }).ConfigureAwait(false);
            // Public creation already owns the official projection. Preserve the cursor, but
            // let the library complete the callback without a second native publication.
            return new CloudProviderDirectoryPage([], page.ContinuationToken);
        }, cancellationToken);
}
