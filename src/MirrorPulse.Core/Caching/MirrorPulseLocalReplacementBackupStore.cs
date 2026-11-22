using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Caching;

/// <summary>Retains immutable old bytes before a controlled same-object replacement writes new content.</summary>
/// <remarks>
/// The Host supplies a retained local data handle that excludes concurrent writers and deletion.
/// Backups use their own private directory and never replace staged new input. Digests prove
/// retained bytes only; the product catalog separately binds them to the original operation
/// and local object. Neither preparing nor reading a backup authorizes a native write or upload.
/// </remarks>
public sealed class MirrorPulseLocalReplacementBackupStore
{
    private readonly MirrorPulseLocalImportStagingStore _storage;

    public MirrorPulseLocalReplacementBackupStore(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _storage = new(Path.Combine(paths.DataRootPath, "state", "replacement-backups"));
    }

    /// <summary>Flushes and independently verifies the complete original bytes without closing or repositioning the caller's handle.</summary>
    public async Task<MirrorPulseNamespaceImportContent> StageRetainedAsync(Guid operationId, FileStream original,
        CancellationToken cancellationToken = default)
    {
        var content = await _storage.StageRetainedAsync(operationId, original, cancellationToken).ConfigureAwait(false);
        await using var verified = await _storage.OpenVerifiedAsync(operationId, content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    /// <summary>Revalidates old bytes and keeps the private backup against writes and deletion during recovery.</summary>
    public async Task<MirrorPulseLocalReplacementBackupLease> OpenLeaseAsync(Guid operationId,
        MirrorPulseNamespaceImportContent content, CancellationToken cancellationToken = default) =>
        new(operationId, content, await _storage.OpenVerifiedAsync(operationId, content, cancellationToken).ConfigureAwait(false));

    /// <summary>Reclaims this backup only after the Host's durable recovery policy releases its last reference.</summary>
    /// <remarks>Native write success alone is insufficient; unfinished recovery and unresolved conflicts retain their backups.</remarks>
    public void RemoveReleased(Guid operationId) => _storage.RemoveCompleted(operationId);
}

/// <summary>A verified, operation-bound backup of old local bytes; not a replacement input or native authority.</summary>
public sealed class MirrorPulseLocalReplacementBackupLease : IAsyncDisposable
{
    internal MirrorPulseLocalReplacementBackupLease(Guid operationId, MirrorPulseNamespaceImportContent content, FileStream data)
    { OperationId = operationId; Content = content; Data = data; }

    public Guid OperationId { get; }
    public MirrorPulseNamespaceImportContent Content { get; }
    public FileStream Data { get; }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
