using System.Collections.Concurrent;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Serializes operations within an instance while allowing independent instances to progress.</summary>
public sealed partial class MirrorPulseInstanceScheduler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<InstanceId, SemaphoreSlim> _gates = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;

    public async ValueTask<T> RunAsync<T>(InstanceId instanceId,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _active++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            SemaphoreSlim gate = _gates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (_lifecycle)
                    if (_closing) throw new OperationCanceledException(_shutdown.Token);
                return await operation(linked.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        finally
        {
            lock (_lifecycle)
                if (--_active == 0 && _closing) _drained.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_closing) return;
            _closing = true;
            if (_active == 0) _drained.TrySetResult();
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        foreach (SemaphoreSlim gate in _gates.Values) gate.Dispose();
        _shutdown.Dispose();
    }
}
