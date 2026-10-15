using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    internal async Task<T> RunCurrentTreeAuditAsync<T>(Guid anchorMemberId,
        Func<MirrorPulseNamespaceCurrentTreeSnapshot, CancellationToken, Task<T>> work, CancellationToken cancellationToken,
        Func<T, CancellationToken, Task>? onAudited = null, RootId? scopeRoot = null)
    {
        Task<MirrorPulseNamespaceCurrentTreeSnapshot> ReadAsync() => scopeRoot is null
            ? _catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchorMemberId, cancellationToken)
            : _catalog.ReadNamespaceCurrentRootSnapshotAsync(anchorMemberId, scopeRoot.Value, cancellationToken);
        var inventory = await ReadAsync().ConfigureAwait(false);
        var anchor = inventory.Members.Single(member => member.Context.Member.MemberId == anchorMemberId);
        var binding = anchor.Context.Member.LocalObject;
        var scope = new PermissionAdmissionScope(binding.VolumeSerialNumber, binding.SyncRootFileId, null);
        EnterApplication(scope); bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            var current = await ReadAsync().ConfigureAwait(false);
            if (current.Fingerprint != inventory.Fingerprint) throw new InvalidOperationException("The current graph changed before audit admission.");
            var result = await work(current, cancellationToken).ConfigureAwait(false);
            if ((await ReadAsync().ConfigureAwait(false)).Fingerprint != current.Fingerprint)
                throw new InvalidOperationException("The current graph or its dispatch fences changed during the native audit.");
            if (onAudited is not null) await onAudited(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (entered) _gate.Release();
            ExitApplication(scope);
        }
    }
}
