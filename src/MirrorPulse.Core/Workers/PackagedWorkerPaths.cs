using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Core.Workers;

internal static class PackagedWorkerPaths
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly Lazy<bool> Packaged = new(HasPackageIdentity);

    internal static (string Executable, string WorkingDirectory) Resolve(WorkerLaunchRequest request) =>
        Packaged.Value ? ResolvePackageFiles(request) : (request.ExecutablePath, request.WorkingDirectory);

    internal static (string Executable, string WorkingDirectory) ResolvePackageFiles(WorkerLaunchRequest request)
    {
        string relative = Path.GetRelativePath(request.WorkingDirectory, request.ExecutablePath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal))
            throw new InvalidDataException("The packaged Worker executable is outside its installation directory.");

        // MSIX can place files written to Local AppData in the edition's private cache.
        // Give the child its actual installation path so its native host can open its own image.
        using SafeFileHandle directory = CreateFile(request.WorkingDirectory, 0,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using SafeFileHandle executable = File.OpenHandle(request.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualRoot = ReadPath(directory);
        string actualExecutable = ReadPath(executable);
        if (!string.Equals(Path.Combine(actualRoot, relative), actualExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The packaged Worker executable changed its installation path.");

        return (actualExecutable, actualRoot);
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFamilyName(ref length, IntPtr.Zero) switch
        {
            ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,
            int error => throw new Win32Exception(error),
        };
    }

    private static string ReadPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= buffer.Length) throw new PathTooLongException();
        string path = new(buffer, 0, (int)length);
        const string dosPrefix = @"\\?\";
        if (!path.StartsWith(dosPrefix, StringComparison.Ordinal) ||
            path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
            throw new IOException("The packaged Worker must be installed on a local Windows volume.");
        return path[dosPrefix.Length..];
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, IntPtr buffer);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}
