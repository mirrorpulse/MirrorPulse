using System.Security.Principal;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

// Fail one original target-creation projection through public SDK contracts and the real SQLite store.
// The normal-user watcher cannot consume the namespace role's armed failure.
internal sealed class MirrorPulseTransferProjectionCommitFault(SecurityIdentifier role)
{
    private Guid _itemId;
    private string? _targetPath;
    private bool _commitBeforeFailure;
    private int _failures;
    internal int Failures => Volatile.Read(ref _failures);
    internal bool CommittedBeforeFailure { get; private set; }
    internal void Arm(Guid itemId) => _itemId = itemId;
    internal void Arm(Guid itemId, string targetPath, bool commitBeforeFailure)
    {
        _itemId = itemId;
        _targetPath = Path.GetFullPath(targetPath);
        _commitBeforeFailure = commitBeforeFailure;
    }
    internal ICloudStateStoreFactory Wrap(ICloudStateStoreFactory inner) => new Factory(inner, this);

    private async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
    {
        using var caller = WindowsIdentity.GetCurrent();
        if (_itemId != Guid.Empty && Failures == 0 && new WindowsPrincipal(caller).IsInRole(role) &&
            (_targetPath is null || File.Exists(_targetPath)) &&
            await transaction.Items.GetByItemIdAsync(_itemId, token).ConfigureAwait(false) is { IsTombstone: false } &&
            Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
        {
            if (_commitBeforeFailure)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                CommittedBeforeFailure = true;
            }
            throw new IOException("Synthetic failure of the first target SDK projection after native creation.");
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private sealed class Factory(ICloudStateStoreFactory inner, MirrorPulseTransferProjectionCommitFault fault) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken).ConfigureAwait(false), fault);
    }
    private sealed class Store(ICloudStateStore inner, MirrorPulseTransferProjectionCommitFault fault) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), fault);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Transaction(ICloudStateTransaction inner, MirrorPulseTransferProjectionCommitFault fault) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations => inner.Operations;
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => fault.CommitAsync(inner, cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
