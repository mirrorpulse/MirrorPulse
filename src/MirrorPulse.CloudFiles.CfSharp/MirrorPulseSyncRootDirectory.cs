using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Gives newly created sync roots the Host user's ownership without adopting existing directories.</summary>
internal static partial class MirrorPulseSyncRootDirectory
{
    internal static unsafe void EnsureExists(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists) return;
        using var user = WindowsIdentity.GetCurrent();
        var owner = user.User ?? throw new InvalidOperationException("The Host has no current-user owner identity.");
        Directory.CreateDirectory(directory.Parent?.FullName ?? throw new IOException("The sync root has no parent directory."));
        // DirectorySecurity adds an empty DACL even when only Owner was supplied.
        // Pass a descriptor with no DACL section so Windows performs normal inheritance.
        var security = new RawSecurityDescriptor(ControlFlags.SelfRelative, owner, null, null, null);
        byte[] descriptor = new byte[security.BinaryLength]; security.GetBinaryForm(descriptor, 0);
        string nativePath = directory.FullName.StartsWith(@"\\?\", StringComparison.Ordinal) ? directory.FullName :
            directory.FullName.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + directory.FullName[2..] : @"\\?\" + directory.FullName;
        fixed (byte* pointer = descriptor)
        {
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = (nint)pointer };
            if (CreateDirectory(nativePath, in attributes)) return;
            int error = Marshal.GetLastPInvokeError();
            // A concurrent creator retains its original ownership and descriptor.
            if (error == 183 && Directory.Exists(directory.FullName)) return;
            throw new Win32Exception(error, "Windows could not create the current-user sync-root directory.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public nint Descriptor; public int InheritHandle; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, in SecurityAttributes attributes);
}
