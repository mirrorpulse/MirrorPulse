using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Uses Windows inheritance rules to predict a descriptor without writing any permissions.</summary>
/// <remarks>
/// This prediction is not evidence of an actual child, its owner or its protection. The owner
/// must separately inspect the native binding and actual descriptor while its parent is guarded.
/// A born object's descriptor is not a pre-protection original or a restoration target.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public static partial class MirrorPulseWindowsNamespaceInheritance
{
    /// <summary>Derives the access descriptor for implicit creation under the exact parent DACL.</summary>
    public static string CreateInheritedDacl(string parentDacl, bool isDirectory)
    {
        // Validate canonical, bounded, access-only input before passing it to Windows.
        _ = MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentDacl, parentDacl);
        var parent = new RawSecurityDescriptor(parentDacl);
        byte[] binary = new byte[parent.BinaryLength];
        parent.GetBinaryForm(binary, 0);
        using WindowsIdentity creator = WindowsIdentity.GetCurrent();
        var mapping = new GenericMapping
        {
            Read = (uint)(FileSystemRights.Read | FileSystemRights.Synchronize),
            Write = (uint)(FileSystemRights.Write | FileSystemRights.Synchronize),
            Execute = (uint)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize),
            All = (uint)FileSystemRights.FullControl,
        };
        if (!CreatePrivateObjectSecurityEx(binary, nint.Zero, out nint descriptor, nint.Zero,
            isDirectory, DaclAutoInherit, creator.AccessToken, in mapping))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65_536) throw new InvalidDataException("Windows returned an unbounded inherited descriptor.");
            byte[] inherited = new byte[length];
            Marshal.Copy(descriptor, inherited, 0, inherited.Length);
            return new RawSecurityDescriptor(inherited, 0).GetSddlForm(AccessControlSections.Access);
        }
        finally { _ = DestroyPrivateObjectSecurity(ref descriptor); }
    }

    private const uint DaclAutoInherit = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        internal uint Read;
        internal uint Write;
        internal uint Execute;
        internal uint All;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePrivateObjectSecurityEx(byte[] parentDescriptor, nint creatorDescriptor,
        out nint newDescriptor, nint objectType, [MarshalAs(UnmanagedType.Bool)] bool isContainer,
        uint autoInheritFlags, SafeAccessTokenHandle token, in GenericMapping genericMapping);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetSecurityDescriptorLength(nint descriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyPrivateObjectSecurity(ref nint descriptor);
}
