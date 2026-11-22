namespace MirrorPulse.Core.Sync;

/// <summary>Serializes local namespace work for one Host-owned sync root.</summary>
/// <remarks>
/// Use one shared owner for controlled operations, recovery and placeholder population.
/// Callbacks must await all their work and retain the required object and ancestor guards.
/// This owner does not acquire native leases, impersonate a role, authorize mutations or
/// replace durable operation evidence. Keep source access and Worker RPC outside callbacks.
/// </remarks>
public sealed class MirrorPulseNamespaceActor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private readonly AsyncLocal<Admission?> _current = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _closing;
    private Task? _disposeTask;

    /// <summary>Runs one awaited operation; a canceled waiter never invokes its callback.</summary>
    /// <remarks>
    /// Reentering this owner from its callback is rejected rather than queued behind itself.
    /// Call helpers directly under the existing admission. Cancellation is cooperative after
    /// entry: the callback receives the caller's token and is always awaited to completion.
    /// A completed callback's result is retained even if cancellation arrives after its work.
    /// </remarks>
    public async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        RejectReentry();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _operations++;
        }
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await _gate.WaitAsync(waiting.Token).ConfigureAwait(false);
            try
            {
                lock (_lifecycle)
                    if (_closing) throw new OperationCanceledException(_shutdown.Token);
                cancellationToken.ThrowIfCancellationRequested();
                var admission = new Admission();
                Admission? previous = _current.Value;
                _current.Value = admission;
                try { return await operation(cancellationToken).ConfigureAwait(false); }
                finally
                {
                    admission.Complete();
                    _current.Value = previous;
                }
            }
            finally { _gate.Release(); }
        }
        finally
        {
            lock (_lifecycle)
                if (--_operations == 0 && _closing) _drained.TrySetResult();
        }
    }

    /// <summary>Checks that a helper is executing inside this owner's active callback.</summary>
    /// <remarks>
    /// This is an ownership assertion, not a security boundary or native permission proof.
    /// An execution context retained by a completed callback no longer carries admission.
    /// </remarks>
    public void AssertAccess()
    {
        if (_current.Value?.IsActive != true)
            throw new InvalidOperationException("Local namespace work requires this owner's active admission.");
    }

    /// <summary>Rejects new work, cancels queued work and drains the active callback.</summary>
    /// <remarks>
    /// Shutdown does not cancel the active callback's token or detach its native work.
    /// Every disposal caller observes the same drain. Do not dispose from inside a callback.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        RejectReentry();
        lock (_lifecycle)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closing = true;
            if (_operations == 0) _drained.TrySetResult();
            _disposeTask = ReleaseAsync();
            return new(_disposeTask);
        }
    }

    private void RejectReentry()
    {
        if (_current.Value?.IsActive == true)
            throw new InvalidOperationException("A namespace callback cannot reenter or dispose its owner.");
    }

    private async Task ReleaseAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        _gate.Dispose();
        _shutdown.Dispose();
    }

    private sealed class Admission
    {
        private int _active = 1;
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public void Complete() => Volatile.Write(ref _active, 0);
    }
}
