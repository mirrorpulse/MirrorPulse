using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Security;

public interface IWindowsCredentialManagerApi
{
    void Write(string targetName, ReadOnlyMemory<byte> secret);

    bool TryRead(string targetName, out byte[] secret);

    void Delete(string targetName);
}

/// <summary>
/// Stores current-user credential bytes in the Windows Credential Manager generic store.
/// </summary>
public sealed class WindowsCredentialManagerStore : ISecureCredentialStore
{
    private readonly IWindowsCredentialManagerApi _api;

    public WindowsCredentialManagerStore(IWindowsCredentialManagerApi? api = null)
    {
        _api = api ?? new NativeWindowsCredentialManagerApi();
    }

    public ValueTask SaveAsync(
        CredentialReference reference,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        _api.Write(GetTargetName(reference), secret);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SecureCredentialValue?> TryGetAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_api.TryRead(GetTargetName(reference), out var secret)
            ? new SecureCredentialValue(reference, secret)
            : null);
    }

    public ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        _api.Delete(GetTargetName(reference));
        return ValueTask.CompletedTask;
    }

    public static string GetTargetName(CredentialReference reference)
    {
        ValidateReference(reference);
        return $"{ProductInfo.Edition.IdentityPrefix}/{reference.ReferenceId}";
    }

    private static void ValidateReference(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope != CredentialScope.CurrentUser)
        {
            throw new ArgumentException("Windows Credential Manager access is limited to the current user.", nameof(reference));
        }
    }

    private sealed class NativeWindowsCredentialManagerApi : IWindowsCredentialManagerApi
    {
        private const uint GenericCredentialType = 1;
        private const uint LocalMachinePersistence = 2;
        private const int NotFoundError = 1168;

        public void Write(string targetName, ReadOnlyMemory<byte> secret)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
            }

            var bytes = secret.ToArray();
            var blob = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            try
            {
                if (blob != IntPtr.Zero)
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                }

                var credential = new NativeCredential
                {
                    Type = GenericCredentialType,
                    TargetName = targetName,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist = LocalMachinePersistence,
                    UserName = Environment.UserName,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed.");
                }
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(blob);
                }

                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public bool TryRead(string targetName, out byte[] secret)
        {
            secret = Array.Empty<byte>();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
            }

            if (!CredRead(targetName, GenericCredentialType, 0, out var credentialPointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NotFoundError)
                {
                    return false;
                }

                throw new Win32Exception(error, "CredRead failed.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (credential.CredentialBlobSize > int.MaxValue)
                {
                    throw new InvalidDataException("The credential blob is too large.");
                }

                secret = new byte[(int)credential.CredentialBlobSize];
                if (secret.Length > 0)
                {
                    Marshal.Copy(credential.CredentialBlob, secret, 0, secret.Length);
                }

                return true;
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        public void Delete(string targetName)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
            }

            if (!CredDelete(targetName, GenericCredentialType, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NotFoundError)
                {
                    throw new Win32Exception(error, "CredDelete failed.");
                }
            }
        }

        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("Advapi32.dll")]
        private static extern bool CredFree(IntPtr credential);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? UserName;
        }
    }
}
