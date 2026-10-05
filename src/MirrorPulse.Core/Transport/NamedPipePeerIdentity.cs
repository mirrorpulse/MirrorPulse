using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Core.Transport;

/// <summary>Reads kernel-owned peer identity before application payloads or credentials are trusted.</summary>
public static class NamedPipePeerIdentity
{
    public static void ValidateClient(NamedPipeServerStream pipe, int processId, int sessionId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actualProcess) ||
            !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint actualSession) ||
            actualProcess != processId || actualSession != sessionId)
        {
            throw new UnauthorizedAccessException("The Worker pipe peer does not match the launched process and Windows session.");
        }
    }

    public static int ValidateServer(NamedPipeClientStream pipe, int? expectedProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using Process current = Process.GetCurrentProcess();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualProcess) ||
            !GetNamedPipeServerSessionId(pipe.SafePipeHandle, out uint actualSession) ||
            actualProcess == 0 || actualProcess > int.MaxValue || actualSession != current.SessionId ||
            (expectedProcessId is not null && actualProcess != expectedProcessId.Value))
        {
            throw new UnauthorizedAccessException("The control pipe server does not match the expected Windows session or process.");
        }

        return (int)actualProcess;
    }

    public static void ValidateServerImage(NamedPipeClientStream pipe, string expectedExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        int processId = ValidateServer(pipe);
        try
        {
            using Process process = Process.GetProcessById(processId);
            string? image = process.MainModule?.FileName;
            if (image is null || !string.Equals(Path.GetFullPath(image), Path.GetFullPath(expectedExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("The control pipe is owned by an unexpected executable.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new UnauthorizedAccessException("The control pipe server identity could not be verified.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
}
