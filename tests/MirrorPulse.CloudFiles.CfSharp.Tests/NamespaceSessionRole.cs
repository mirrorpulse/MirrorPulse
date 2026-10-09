using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

/// <summary>Disposable current-user execution identity for isolated namespace probes only.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NamespaceSessionRole : IDisposable
{
    private readonly SafeAccessTokenHandle _token;
    public SecurityIdentifier RoleSid { get; }

    public NamespaceSessionRole()
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        // NewCredentials keeps the local user. Synthetic outbound credentials
        // are never used by a network operation or written to persistent storage.
        if (!LogonUser("MirrorPulseNamespaceProbe", ".", Guid.NewGuid().ToString("N"), 9, 3, out _token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var clone = new WindowsIdentity(_token.DangerousGetHandle());
            if (caller.User is null || clone.User is null || !caller.User.Equals(clone.User))
                throw new InvalidOperationException("The namespace role changed the local user.");
            string[] added = ReadLogonGroups(_token).Except(ReadLogonGroups(caller.AccessToken)).ToArray();
            if (added.Length != 1) throw new InvalidOperationException("The namespace role has no unique added logon SID.");
            RoleSid = new(added[0]);
        }
        catch { _token.Dispose(); throw; }
    }

    public Task RunAsync(Func<Task> operation) => WindowsIdentity.RunImpersonatedAsync(_token, operation);

    public void Dispose() => _token.Dispose();

    private static string[] ReadLogonGroups(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, 2, nint.Zero, 0, out int length);
        if (length is <= 0 or > 1_048_576) throw new InvalidDataException("The native token groups are not bounded.");
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int count = Marshal.ReadInt32(buffer);
            int offset = nint.Size == 8 ? 8 : 4;
            int stride = nint.Size == 8 ? 16 : 8;
            if (count < 0 || offset + (long)count * stride > length) throw new InvalidDataException("The native token group array is invalid.");
            var groups = new List<string>();
            for (int index = 0; index < count; index++)
            {
                int position = offset + index * stride;
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, position + nint.Size));
                if ((attributes & 0xC0000000) == 0xC0000000)
                    groups.Add(new SecurityIdentifier(Marshal.ReadIntPtr(buffer, position)).Value);
            }
            return groups.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint data, int length, out int required);
}
