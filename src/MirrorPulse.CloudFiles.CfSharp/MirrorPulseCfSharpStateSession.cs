using CfSharp;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Captures the official store owned by CloudFileSystem so MP may use its public operation
/// repository for retry metadata. This wrapper never owns or disposes the store.
/// </summary>
public sealed class MirrorPulseCfSharpStateSession : ICloudStateStoreFactory
{
    private readonly ICloudStateStoreFactory _inner;
    private readonly string _databasePath;
    private ICloudStateStore? _store;

    public MirrorPulseCfSharpStateSession(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _databasePath = paths.CfSharpStateDatabasePath;
        _inner = MirrorPulseCfSharpStateStoreFactory.Create(paths);
    }

    public ICloudStateStore OpenStore => Volatile.Read(ref _store)
        ?? throw new InvalidOperationException("The CfSharp state store has not opened.");

    /// <summary>Retains the opened official SQLite file, without inspecting its schema or claiming journal authority.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.16299")]
    internal ValueTask<MirrorPulseCfSharpStoreFileLease> OpenStoreFileLeaseAsync(CancellationToken cancellationToken = default) =>
        MirrorPulseCfSharpStoreFileLease.OpenAsync(OpenStore, _databasePath, cancellationToken);

    public async ValueTask<ICloudStateStore> OpenAsync(
        CloudStateStoreContext context,
        CancellationToken cancellationToken = default)
    {
        ICloudStateStore opened = await _inner.OpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _store, opened, null) is not null)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The CfSharp state session has already opened a store.");
        }

        return opened;
    }
}
