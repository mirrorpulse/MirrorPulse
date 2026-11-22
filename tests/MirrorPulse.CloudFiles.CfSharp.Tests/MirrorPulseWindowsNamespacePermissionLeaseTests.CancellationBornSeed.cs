using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static void SeedRetainedInheritedCancellationFixtureDacl(CloudItem item, string retainedDacl)
    {
        // Fault injection only, while the admitted scope retains this disposable fixture's
        // original object and ancestor names. NativeObjectSecurity.Persist deliberately
        // recalculates inheritance from the current parent, so it cannot reproduce stale
        // inherited ACEs. The non-propagating Windows setter seeds that interrupted state;
        // production recovery still has to perform its real public CfSharp Access write.
        _ = MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(retainedDacl, retainedDacl);
        var descriptor = new RawSecurityDescriptor(retainedDacl);
        // Ask the low-level setter to retain completed auto-inheritance metadata as well
        // as the exact old ACEs. The subsequent protected readback must prove the original
        // descriptor, binding and owner; this request is never permission evidence.
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        if (!SetCancellationFixtureFileSecurity(item.FullPath, 4, binary))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("advapi32.dll", EntryPoint = "SetFileSecurityW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCancellationFixtureFileSecurity(string path, uint securityInformation, [In] byte[] descriptor);
}
