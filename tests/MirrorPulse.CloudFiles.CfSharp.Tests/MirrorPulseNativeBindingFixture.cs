using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

// Independent assertion oracle only. Never supplies recovery authority to the Host.
internal static partial class MirrorPulseNativeBindingFixture
{
    internal static MirrorPulseLocalFileBinding Read(string syncRoot, string targetPath)
    {
        using var root = Open(syncRoot);
        using var target = Open(targetPath);
        var rootId = Read(root);
        var targetId = Read(target);
        if (rootId.VolumeSerialNumber != targetId.VolumeSerialNumber)
            throw new InvalidDataException("The fixture target left its original volume.");
        return new(targetId.VolumeSerialNumber, rootId.FileId, targetId.FileId);
    }

    private static SafeFileHandle Open(string path)
    {
        // Attributes only; preserve the reparse point and share ordinary readers/writers.
        var handle = CreateFile(path, 0x80, 7, nint.Zero, 3, 0x02200000, nint.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static FileIdInfo Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out FileIdInfo identity, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public Guid FileId; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out FileIdInfo identity, uint length);
}
