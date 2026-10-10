using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseWindowsNamespacePermissionLease
{
    /// <summary>Runs a short permission callback inside CfSharp's same-object protected operation.</summary>
    /// <remarks>
    /// Persist original binding, identity and DACL before requesting conversion or permission writes.
    /// The caller owns product application admission and tree fences. CfSharp owns conversion,
    /// inspection, Access descriptor operations and callback draining; MP retains ancestor names,
    /// root routing and an independent read-only owner-SID check. No source or Worker work belongs
    /// in this callback. The receipt alone proves no tree readiness or synchronization acceptance.
    /// </remarks>
    public static async Task<CloudProtectedLocalOperationResult> RunProtectedAsync(CloudItem item,
        MirrorPulseRootRouter router, MirrorPulseLocalFileBinding originalBinding,
        CloudPlaceholderIdentity? localIdentity,
        Func<IMirrorPulseNamespacePermissionLease, CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(originalBinding);
        ArgumentNullException.ThrowIfNull(callback);
        if (originalBinding.VolumeSerialNumber == 0 || originalBinding.SyncRootFileId == Guid.Empty || originalBinding.LocalFileId == Guid.Empty)
            throw new ArgumentException("The retained original native binding is incomplete.", nameof(originalBinding));
        if (item.Kind == CloudItemKind.Directory && localIdentity is not null)
            throw new ArgumentException("Directory metadata protection does not prepare a file identity.", nameof(localIdentity));
        var binding = new CloudLocalFileBinding(originalBinding.VolumeSerialNumber, originalBinding.SyncRootFileId, originalBinding.LocalFileId);
        var request = localIdentity is not null ? CloudProtectedLocalOperationRequest.ForLocalConversion(binding, localIdentity)
            : new CloudProtectedLocalOperationRequest(binding, item.Kind == CloudItemKind.Directory
                ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile);
        // Only read-only metadata access to the target remains outside the library scope.
        // A second data open would conflict with its exclusive native object.
        await using var metadata = await OpenCoreAsync(item, router, metadataOnlyTarget: true, cancellationToken).ConfigureAwait(false);
        return await item.RunProtectedLocalOperationAsync(request, async (scope, stop) =>
        {
            await using var lease = new ProtectedPermissionLease(metadata, scope, binding);
            await callback(lease, stop).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private string ReadOwnerSid(CloudLocalFileBinding original)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRetainedHandle(_target);
        if (!GetFileInformationByHandleEx(_target, FileIdInformation, out FileIdInfo identity, (uint)System.Runtime.InteropServices.Marshal.SizeOf<FileIdInfo>()))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        if (identity.VolumeSerialNumber != original.VolumeSerialNumber || identity.FileId != original.LocalFileId)
            throw new InvalidDataException("The read-only owner reference does not belong to the protected original object.");
        var descriptor = new HandleSecurity(_target, _isDirectory);
        return ((SecurityIdentifier?)descriptor.GetOwner(typeof(SecurityIdentifier)))?.Value
            ?? throw new InvalidDataException("The retained original object has no owner SID.");
    }

    private sealed class ProtectedPermissionLease(MirrorPulseWindowsNamespacePermissionLease metadata,
        CloudProtectedLocalOperationContext scope, CloudLocalFileBinding original) : IMirrorPulseNamespacePermissionLease
    {
        private bool _disposed;

        public async ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloudItemSnapshot snapshot = await scope.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Exists || snapshot.LocalBinding != original || (snapshot.Kind == CloudItemKind.Directory) != metadata._isDirectory)
                throw new InvalidDataException("Protected permissions require the retained original object.");
            FileSystemSecurity descriptor = await scope.ReadAccessDescriptorAsync(cancellationToken).ConfigureAwait(false);
            string owner = metadata.ReadOwnerSid(original);
            return new(new(original.VolumeSerialNumber, original.SyncRootFileId, original.LocalFileId), metadata._rootId,
                metadata._item.RelativePath.Replace('\\', '/'), metadata._isDirectory, owner,
                descriptor.GetSecurityDescriptorSddlForm(AccessControlSections.Access), DateTimeOffset.UtcNow);
        }

        public async ValueTask ApplyDaclAsync(string dacl, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateAccessDescriptor(dacl);
            FileSystemSecurity descriptor = metadata._isDirectory ? new DirectorySecurity() : new FileSecurity();
            descriptor.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
            _ = await scope.ApplyAccessDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            // The callback wrapper owns no library handle and cannot release it prematurely.
            return ValueTask.CompletedTask;
        }
    }
}
