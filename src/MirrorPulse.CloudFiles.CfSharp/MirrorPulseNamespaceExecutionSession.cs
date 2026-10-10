using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>An ephemeral current-user identity for Host-owned namespace operations.</summary>
/// <remarks>
/// This session neither changes ACLs nor enables protection. The Host owns its lifetime and
/// must keep source access and Worker RPC outside this execution context. Synthetic outbound
/// credentials are never used for network access or written to persistent storage.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed partial class MirrorPulseNamespaceExecutionSession : IAsyncDisposable
{
    private readonly SafeAccessTokenHandle _token;
    private readonly WindowsIdentity _normalUser;
    private readonly object _gate = new();
    private int _operations;
    private bool _disposing;
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;

    public SecurityIdentifier OwnerSid { get; }
    public SecurityIdentifier RoleSid { get; }

    public MirrorPulseNamespaceExecutionSession()
    {
        _normalUser = WindowsIdentity.GetCurrent();
        if (!LogonUser("MirrorPulseNamespace", ".", Guid.NewGuid().ToString("N"),
            NewCredentials, WinNt50, out _token))
        {
            int error = Marshal.GetLastWin32Error();
            _token.Dispose();
            _normalUser.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            using var clone = new WindowsIdentity(_token.DangerousGetHandle());
            if (_normalUser.User is null || clone.User is null || !_normalUser.User.Equals(clone.User))
                throw new InvalidOperationException("The namespace execution identity changed the local user.");
            string[] added = ReadLogonGroups(_token).Except(ReadLogonGroups(_normalUser.AccessToken)).ToArray();
            if (added.Length != 1 || !added[0].StartsWith("S-1-5-5-", StringComparison.Ordinal))
                throw new InvalidOperationException("The namespace execution identity has no unique added logon SID.");
            OwnerSid = _normalUser.User;
            RoleSid = new(added[0]);
        }
        catch { _token.Dispose(); _normalUser.Dispose(); throw; }
    }

    /// <summary>Flows the role across asynchronous local namespace work and restores the caller.</summary>
    public Task RunNamespaceOperationAsync(Func<Task> operation) => RunOwnedOperationAsync(_token, operation);

    /// <summary>Runs source access using the captured Host user, even when called from local namespace work.</summary>
    /// <remarks>
    /// Construct the session in the Host's normal user context. This boundary removes the session's
    /// namespace role; it neither grants network access nor changes Worker process identity.
    /// The owner drains normal-user work along with namespace work before releasing either token.
    /// </remarks>
    public Task RunNormalUserOperationAsync(Func<Task> operation) => RunOwnedOperationAsync(_normalUser.AccessToken, operation);

    /// <summary>Returns a source result without flowing the namespace role into source access.</summary>
    public async Task<T> RunNormalUserOperationAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        T result = default!;
        await RunNormalUserOperationAsync(async () => { result = await operation().ConfigureAwait(false); }).ConfigureAwait(false);
        return result;
    }

    private async Task RunOwnedOperationAsync(SafeAccessTokenHandle token, Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _operations++;
        }
        try { await WindowsIdentity.RunImpersonatedAsync(token, operation).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _operations--;
                if (_disposing && _operations == 0) _drained?.TrySetResult();
            }
        }
    }

    /// <summary>Rejects new work and waits for active work before releasing the identity.</summary>
    /// <remarks>The owner must not dispose the session from inside its own operation.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposing = true;
            Task drain = _operations == 0 ? Task.CompletedTask :
                (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = ReleaseAsync(drain);
            return new(_disposeTask);
        }
    }

    private async Task ReleaseAsync(Task drain)
    {
        await drain.ConfigureAwait(false);
        _token.Dispose();
        _normalUser.Dispose();
    }

    private static string[] ReadLogonGroups(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, TokenGroups, nint.Zero, 0, out int length);
        if (length is <= 0 or > 1_048_576) throw new InvalidDataException("The native token groups are not bounded.");
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            int count = Marshal.ReadInt32(buffer);
            int offset = nint.Size == 8 ? 8 : 4;
            int stride = nint.Size == 8 ? 16 : 8;
            if (count < 0 || offset + (long)count * stride > length)
                throw new InvalidDataException("The native token group array is invalid.");
            var groups = new List<string>();
            for (int index = 0; index < count; index++)
            {
                int position = offset + index * stride;
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, position + nint.Size));
                if ((attributes & LogonId) == LogonId)
                    groups.Add(new SecurityIdentifier(Marshal.ReadIntPtr(buffer, position)).Value);
            }
            return groups.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int NewCredentials = 9;
    private const int WinNt50 = 3;
    private const int TokenGroups = 2;
    private const uint LogonId = 0xC0000000;

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint data, int length, out int required);
}
