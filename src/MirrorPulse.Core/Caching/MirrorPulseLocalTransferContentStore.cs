using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Caching;

/// <summary>Retains complete transfer bytes separately from import inputs, replacement backups and disposable caches.</summary>
/// <remarks>The Host supplies its retained readable source handle. Staging proves the retained private
/// bytes, not an atomic source snapshot or protection against writable mappings. The private content
/// identity and digest do not establish native membership, remote acceptance or source deletion authority.
/// Unfinished transfers retain their bytes across Host restarts, including uncertain upload outcomes.</remarks>
public sealed class MirrorPulseLocalTransferContentStore
{
    private readonly MirrorPulseLocalImportStagingStore _storage;

    public MirrorPulseLocalTransferContentStore(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _storage = new(Path.Combine(paths.DataRootPath, "state", "transfer-inputs"));
    }

    /// <summary>Flushes and independently reopens the complete retained source without closing or repositioning its handle.</summary>
    public async Task<MirrorPulseNamespaceImportContent> StageRetainedAsync(Guid privateContentId, FileStream source,
        CancellationToken cancellationToken = default)
    {
        var content = await _storage.StageRetainedAsync(privateContentId, source, cancellationToken).ConfigureAwait(false);
        await using var verified = await _storage.OpenVerifiedAsync(privateContentId, content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    /// <summary>Reopens the original plan's private bytes and retains a verified read-only handle against writers and deletion.</summary>
    public async Task<MirrorPulseLocalTransferContentLease> OpenLeaseAsync(MirrorPulseNamespaceTransferPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version != 1 || plan.OperationId == Guid.Empty || plan.PrivateContentId == Guid.Empty || plan.OperationId == plan.PrivateContentId)
            throw new ArgumentException("Retained transfer bytes require their distinct original operation and content identities.", nameof(plan));
        var content = new MirrorPulseNamespaceImportContent(plan.Length, plan.Sha256);
        return new(plan.OperationId, plan.PrivateContentId, content,
            await _storage.OpenVerifiedAsync(plan.PrivateContentId, content, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Original transfer content, independently verified and held read-only; not a native execution authorization.</summary>
public sealed class MirrorPulseLocalTransferContentLease : IAsyncDisposable
{
    internal MirrorPulseLocalTransferContentLease(Guid operationId, Guid privateContentId,
        MirrorPulseNamespaceImportContent content, FileStream data)
    { OperationId = operationId; PrivateContentId = privateContentId; Content = content; Data = data; }

    public Guid OperationId { get; }
    public Guid PrivateContentId { get; }
    public MirrorPulseNamespaceImportContent Content { get; }
    public FileStream Data { get; }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
