using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

/// <summary>Rejects one actual original-journal removal commit; CfSharp owns every store operation and rollback.</summary>
internal sealed class MirrorPulseJournalAcknowledgementCommitFault
{
    private Guid _operation;
    private int _failures;
    internal int Failures => Volatile.Read(ref _failures);
    internal void Arm(Guid operation) => _operation = operation;
    internal ICloudStateStoreFactory Wrap(ICloudStateStoreFactory inner) => new Factory(inner, this);

    private async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
    {
        if (_operation != Guid.Empty && Volatile.Read(ref _failures) == 0 &&
            await transaction.Operations.GetAsync(_operation, token).ConfigureAwait(false) is null &&
            Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
            throw new IOException("Synthetic loss of the original public journal acknowledgement commit.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private sealed class Factory(ICloudStateStoreFactory inner, MirrorPulseJournalAcknowledgementCommitFault fault) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken).ConfigureAwait(false), fault);
    }
    private sealed class Store(ICloudStateStore inner, MirrorPulseJournalAcknowledgementCommitFault fault) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), fault);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Transaction(ICloudStateTransaction inner, MirrorPulseJournalAcknowledgementCommitFault fault) : ICloudStateTransaction
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
