using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains a local object and ancestor chain while reading or applying its owned DACL.</summary>
/// <remarks>
/// CfSharp's public inspection supplies the native object binding and placeholder classification.
/// MP only owns handle-based access-descriptor policy. No file contents are read and no Cloud
/// Files state is independently projected. The caller must drain operations before disposal.
/// Applying a directory DACL can propagate inheritance; tree preparation must capture every
/// original descriptor before any application. This lease alone does not enable tree protection.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed partial class MirrorPulseWindowsNamespacePermissionLease : IMirrorPulseNamespacePermissionLease
{
    private readonly CloudItem _item;
    private readonly RootId? _rootId;
    private readonly List<SafeFileHandle> _handles;
    private readonly SafeFileHandle _target;
    private readonly bool _isDirectory;
    private readonly bool _metadataOnly;
    private bool _disposed;

    private MirrorPulseWindowsNamespacePermissionLease(CloudItem item, RootId? rootId, List<SafeFileHandle> handles, bool metadataOnly = false)
    {
        _item = item;
        _rootId = rootId;
        _handles = handles;
        _target = handles[^1];
        _isDirectory = item.Kind == CloudItemKind.Directory;
        _metadataOnly = metadataOnly;
    }

    /// <summary>Opens only a currently routed root or its descendant in an already started CfSharp session.</summary>
    public static Task<MirrorPulseWindowsNamespacePermissionLease> OpenAsync(CloudItem item,
        MirrorPulseRootRouter router, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(item, router, metadataOnlyTarget: false, cancellationToken);

    internal static Task<MirrorPulseWindowsNamespacePermissionLease> OpenMetadataAsync(CloudItem item,
        MirrorPulseRootRouter router, CancellationToken cancellationToken) =>
        OpenCoreAsync(item, router, metadataOnlyTarget: true, cancellationToken);

    private static async Task<MirrorPulseWindowsNamespacePermissionLease> OpenCoreAsync(CloudItem item,
        MirrorPulseRootRouter router, bool metadataOnlyTarget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        RootId? rootId = null;
        if (!router.IsSyncRoot(item.FullPath))
        {
            MirrorPulseRoutedItem route = router.ResolveCurrentPath(item.FullPath);
            rootId = router.GetRegistration(route.InstanceId, route.RootKey).RootId;
        }
        else if (item.RelativePath.Length != 0 || item.Kind != CloudItemKind.Directory)
            throw new InvalidDataException("The permission lease does not refer to the actual sync-root directory.");

        var cloudAncestors = new Dictionary<string, CloudItem>(StringComparer.OrdinalIgnoreCase);
        CloudItem? ancestor = item;
        CloudItem rootItem = item;
        while (ancestor is not null)
        {
            cloudAncestors.Add(ancestor.FullPath, ancestor);
            rootItem = ancestor;
            ancestor = ancestor.Parent;
        }
        if (!router.IsSyncRoot(rootItem.FullPath))
            throw new InvalidDataException("The permission lease belongs to a different Cloud Files session root.");
        string volumeRoot = Path.GetPathRoot(item.FullPath) ?? throw new InvalidDataException("The local namespace has no volume root.");
        if (volumeRoot.Length != 3 || volumeRoot[1] != ':')
            throw new InvalidDataException("Namespace permissions require a local drive-backed Cloud Files root.");
        if (string.Equals(Path.TrimEndingDirectorySeparator(rootItem.FullPath),
            Path.TrimEndingDirectorySeparator(volumeRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An entire volume cannot be a protected product namespace.");
        var handles = new List<SafeFileHandle>();
        try
        {
            string current = volumeRoot;
            string[] parts = item.FullPath[volumeRoot.Length..].Split(Path.DirectorySeparatorChar);
            for (int index = -1; index < parts.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= 0) current = Path.Combine(current, parts[index]);
                bool target = index == parts.Length - 1;
                SafeFileHandle handle = Open(current, target, metadataOnlyTarget);
                handles.Add(handle);
                AttributeTag attributes = ReadAttributes(handle);
                if ((attributes.Attributes & DirectoryAttribute) == 0 && (!target || item.Kind == CloudItemKind.Directory) ||
                    target && item.Kind == CloudItemKind.File && (attributes.Attributes & DirectoryAttribute) != 0)
                    throw new InvalidDataException("A retained namespace component has an unexpected object type.");
                if ((attributes.Attributes & ReparseAttribute) != 0)
                {
                    if (!cloudAncestors.TryGetValue(current, out CloudItem? cloudItem) ||
                        !(await cloudItem.InspectAsync(cancellationToken).ConfigureAwait(false)).IsPlaceholder)
                        throw new InvalidDataException("A foreign reparse point cannot own namespace permission evidence.");
                }
            }
            var lease = new MirrorPulseWindowsNamespacePermissionLease(item, rootId, handles, metadataOnlyTarget);
            if (!metadataOnlyTarget) _ = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            throw;
        }
    }

    public async ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloudItemSnapshot snapshot = await _item.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists || snapshot.LocalBinding is not { } binding ||
            (snapshot.Kind == CloudItemKind.Directory) != _isDirectory ||
            (ReadAttributes(_target).Attributes & ReparseAttribute) != 0 && !snapshot.IsPlaceholder)
            throw new InvalidDataException("The retained namespace object cannot provide an owned permission binding.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetainedHandle(_target);
        var descriptor = new HandleSecurity(_target, _isDirectory);
        string owner = ((SecurityIdentifier?)descriptor.GetOwner(typeof(SecurityIdentifier)))?.Value
            ?? throw new InvalidDataException("The retained namespace object has no owner SID.");
        return new(new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), _rootId,
            _item.RelativePath.Replace('\\', '/'), _isDirectory, owner,
            descriptor.GetSecurityDescriptorSddlForm(AccessControlSections.Access), DateTimeOffset.UtcNow);
    }

    public ValueTask ApplyDaclAsync(string dacl, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_metadataOnly) throw new InvalidOperationException("Protected permission writes belong to the CfSharp scope.");
        ValidateAccessDescriptor(dacl);
        ValidateRetainedHandle(_target);
        var descriptor = new HandleSecurity(_target, _isDirectory);
        descriptor.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        descriptor.PersistAccess(_target);
        return ValueTask.CompletedTask;
    }

    private static void ValidateAccessDescriptor(string dacl)
    {
        if (string.IsNullOrEmpty(dacl) || dacl.Length > 65_536)
            throw new ArgumentException("The access descriptor is not bounded.", nameof(dacl));
        var parsed = new RawSecurityDescriptor(dacl);
        if (parsed.Owner is not null || parsed.Group is not null || parsed.SystemAcl is not null || parsed.DiscretionaryAcl is null ||
            parsed.BinaryLength > 65_536 || parsed.GetSddlForm(AccessControlSections.Access) != dacl)
            throw new ArgumentException("The permission lease accepts only a canonical access descriptor.", nameof(dacl));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        for (int index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        return ValueTask.CompletedTask;
    }

    internal static SafeFileHandle Open(string path, bool target, bool metadataOnlyTarget = false)
    {
        // Ancestors and legacy targets request read-data without reading content, retaining
        // their names against replacement. The protected target requests only read-only
        // metadata; CfSharp owns its data-sharing protection and DACL writes. Reparse-object
        // and no-recall flags keep cold metadata operations offline.
        uint access = ReadAttributesAccess | (target && metadataOnlyTarget ? ReadControl :
            ReadDataOrListDirectory | (target ? ReadControl | WriteDacl : 0));
        SafeFileHandle handle = CreateFile(path, access,
            ShareRead | ShareWrite, nint.Zero, OpenExisting, OpenReparsePoint | OpenNoRecall | BackupSemantics, nint.Zero);
        if (!handle.IsInvalid)
        {
            try { ValidateRetainedHandle(handle); return handle; }
            catch { handle.Dispose(); throw; }
        }
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    internal static void ValidateRetainedHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!GetFileInformationByHandleEx(handle, StandardInformation, out StandardInfo information, (uint)Marshal.SizeOf<StandardInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        // An ACL belongs to the object, not one directory entry. Reject ordinary hard-link
        // aliases before capture or writes, including links outside the managed namespace.
        // CfSharp remains responsible for IDs, placeholder classification and CFAPI policy.
        if (information.DeletePending != 0 || information.Directory == 0 && information.NumberOfLinks != 1)
            throw new InvalidDataException("A deleted or multiply linked object cannot own namespace permission evidence.");
    }

    private static AttributeTag ReadAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, AttributeTagInformation, out AttributeTag attributes, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return attributes;
    }

    // NativeObjectSecurity supplies mature handle-based DACL marshalling and persistence.
    // Only the access section is written; owner and audit sections remain unchanged.
    private sealed class HandleSecurity(SafeFileHandle handle, bool isDirectory)
        : NativeObjectSecurity(isDirectory, ResourceType.FileObject, handle, AccessControlSections.Owner | AccessControlSections.Access)
    {
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(HandleAccessRule);
        public override Type AuditRuleType => typeof(HandleAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new HandleAccessRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, type);
        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new HandleAuditRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, flags);
        public void PersistAccess(SafeFileHandle target) => Persist(target, AccessControlSections.Access);
    }

    private sealed class HandleAccessRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AccessControlType type)
        : AccessRule(identity, mask, inherited, inheritance, propagation, type);

    private sealed class HandleAuditRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AuditFlags flags)
        : AuditRule(identity, mask, inherited, inheritance, propagation, flags);

    private const uint ReadDataOrListDirectory = 1, ReadAttributesAccess = 0x80, ReadControl = 0x20000, WriteDacl = 0x40000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint OpenNoRecall = 0x00100000, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;
    private const int StandardInformation = 1, AttributeTagInformation = 9, FileIdInformation = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public Guid FileId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out AttributeTag data, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out StandardInfo data, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out FileIdInfo data, uint length);
}
