using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains existing local names and ancestor chains without reading file contents.</summary>
/// <remarks>
/// Local inventory uses CfSharp's public local enumeration. Every object is independently
/// inspected through its retained metadata lease; foreign reparse points and aliases are
/// rejected there. Directory membership and content are not frozen. The Host must drain
/// reconciliation before disposing this owner and must not treat it as namespace authorization.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseWindowsNamespacePermissionTreeLease : IAsyncDisposable
{
    private readonly CloudDirectory _root;
    private readonly Dictionary<string, (CloudItem Item, MirrorPulseWindowsNamespacePermissionLease Lease)> _objects =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    internal MirrorPulseRootRouter Router { get; }

    private MirrorPulseWindowsNamespacePermissionTreeLease(CloudDirectory root, MirrorPulseRootRouter router)
    {
        _root = root;
        Router = router;
    }

    /// <summary>Opens one currently registered first-level root and retains all materialized local objects.</summary>
    public static async Task<MirrorPulseWindowsNamespacePermissionTreeLease> OpenAsync(CloudDirectory root,
        MirrorPulseRootRouter router, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(router);
        if (router.IsSyncRoot(root.FullPath) || router.ResolveCurrentPath(root.FullPath).RelativePath.Length != 0)
            throw new ArgumentException("A namespace tree lease requires one current first-level managed root.", nameof(root));
        var owner = new MirrorPulseWindowsNamespacePermissionTreeLease(root, router);
        try
        {
            await foreach (var _ in owner.InspectObjectsAsync(cancellationToken).ConfigureAwait(false)) { }
            return owner;
        }
        catch
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal CloudItem GetItem(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _objects.TryGetValue(relativePath, out var retained) ? retained.Item :
            throw new InvalidDataException("The captured namespace object has no retained local reference.");
    }

    /// <summary>Reads a fresh local membership set and access descriptors, retaining newly observed names.</summary>
    public async IAsyncEnumerable<MirrorPulseNamespacePermissionObject> InspectObjectsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        yield return await InspectAsync(_root, cancellationToken).ConfigureAwait(false);
        var directories = new Queue<CloudDirectory>();
        directories.Enqueue(_root);
        while (directories.TryDequeue(out CloudDirectory? directory))
        {
            // Top-level public enumeration also visits locally materialized descendants of
            // Cloud Files directories, without following foreign reparse points ourselves.
            await foreach (CloudItem child in directory.EnumerateLocalChildrenAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                var observed = await InspectAsync(child, cancellationToken).ConfigureAwait(false);
                yield return observed;
                if (observed.IsDirectory) directories.Enqueue((CloudDirectory)child);
            }
        }
    }

    private async Task<MirrorPulseNamespacePermissionObject> InspectAsync(CloudItem item, CancellationToken stop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string relative = item.RelativePath.Replace('\\', '/');
        if (!_objects.TryGetValue(relative, out var retained))
        {
            var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, Router, stop).ConfigureAwait(false);
            retained = (item, lease);
            _objects.Add(relative, retained);
        }
        return await retained.Lease.InspectAsync(stop).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var retained in _objects.Values.Reverse()) await retained.Lease.DisposeAsync().ConfigureAwait(false);
        _objects.Clear();
    }
}
