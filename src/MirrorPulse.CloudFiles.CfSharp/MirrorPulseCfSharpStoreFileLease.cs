using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CfSharp;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Physical identity of one original official database file, independent of its path or a feed consumer's lifetime.</summary>
internal readonly record struct MirrorPulseCfSharpStoreFileBinding(ulong VolumeSerialNumber, Guid FileId);

/// <summary>Retains the database file selected by the actual official-store factory and checks its physical identity.</summary>
/// <remarks>
/// The official provider already retains its verified parent chain, database and sidecars for its
/// complete lifetime. This additional handle binds the main file across a product operation and
/// refuses a replacement after reopening. It never reads SQLite bytes, opens another connection,
/// owns or disposes the SDK store, mutates its rows, or proves application or acknowledgement.
/// Physical identity alone does not authenticate journal contents; those need their original public
/// SDK validations. The caller also retains Host lifecycle admission throughout the operation.
/// Read-data access is requested so Windows enforces no-delete sharing. No database bytes are read.
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed partial class MirrorPulseCfSharpStoreFileLease : IDisposable
{
    private readonly ICloudStateStore _store;
    private readonly string _path;
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private MirrorPulseCfSharpStoreFileLease(ICloudStateStore store, string path, SafeFileHandle handle,
        MirrorPulseCfSharpStoreFileBinding binding)
    { _store = store; _path = path; _handle = handle; Binding = binding; }

    internal MirrorPulseCfSharpStoreFileBinding Binding { get; }

    internal static async ValueTask<MirrorPulseCfSharpStoreFileLease> OpenAsync(ICloudStateStore store, string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequireLiveStoreAsync(store, cancellationToken).ConfigureAwait(false);
        SafeFileHandle handle = OpenFile(path);
        try
        {
            var result = new MirrorPulseCfSharpStoreFileLease(store, path, handle, ReadBinding(handle));
            await result.RequireCurrentAsync(result.Binding, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch { handle.Dispose(); throw; }
    }

    internal async ValueTask RequireCurrentAsync(MirrorPulseCfSharpStoreFileBinding original,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
        if (original != Binding || ReadBinding(_handle) != original)
            throw new InvalidDataException("The official SQLite database differs from its retained physical file.");
        await RequireLiveStoreAsync(_store, cancellationToken).ConfigureAwait(false);
        // The live official provider owns and pins the parent chain. Never infer ownership
        // from a retained path, an opaque scan scope, row absence or this file ID alone.
        using var current = OpenFile(_path);
        if (ReadBinding(current) != original || ReadBinding(_handle) != original)
            throw new InvalidDataException("The opened official SQLite database file was replaced.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async ValueTask RequireLiveStoreAsync(ICloudStateStore store, CancellationToken cancellationToken)
    {
        await using var transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SafeFileHandle OpenFile(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0x81, 1 | 2, 0, 3, 0x00200000 | 0x00100000, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("The original official database file handle could not be retained.", new Win32Exception(error));
    }

    private static MirrorPulseCfSharpStoreFileBinding ReadBinding(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag attributes, (uint)Marshal.SizeOf<AttributeTag>()) ||
            !GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, (uint)Marshal.SizeOf<StandardInfo>()) ||
            !GetFileInformationByHandleEx(handle, 18, out FileIdInfo identity, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new IOException("The original official database file identity could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((attributes.Attributes & (0x10 | 0x400)) != 0 || attributes.Tag != 0 || standard.Directory != 0 ||
            standard.DeletePending != 0 || standard.NumberOfLinks != 1 || identity.FileId == Guid.Empty)
            throw new InvalidDataException("The original official database must be one retained regular single-link file.");
        return new(identity.VolumeSerialNumber, identity.FileId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public Guid FileId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out FileIdInfo data, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out StandardInfo data, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out AttributeTag data, uint length);
}
