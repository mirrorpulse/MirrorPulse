using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

public sealed partial class MirrorPulseInstanceScheduler
{
    /// <summary>Retains a bounded, ordered set of instance gates across another Host lifetime boundary.</summary>
    /// <remarks>The lease has its own serialized execution lanes. It never implicitly reenters
    /// the scheduler through an inherited async context, and cannot authorize another instance.</remarks>
    public async ValueTask<InstanceLease> AcquireAsync(IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var requested = instanceIds.Take(4097).ToArray();
        if (requested.Length > 4096 || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException("Instance admission requires a bounded distinct inventory.", nameof(instanceIds));
        var ordered = requested.OrderBy(instance => instance.Value).ToArray();
        lock (_lifecycle) { ObjectDisposedException.ThrowIf(_closing, this); _active++; }
        var held = new List<SemaphoreSlim>(); CancellationTokenSource? lifetime = null;
        try
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            foreach (var instance in ordered)
            {
                var gate = _gates.GetOrAdd(instance, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false); held.Add(gate);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            lock (_lifecycle) if (_closing) throw new OperationCanceledException(_shutdown.Token);
            return new(this, ordered, held.ToArray(), lifetime);
        }
        catch
        {
            foreach (var gate in held.AsEnumerable().Reverse()) gate.Release();
            lifetime?.Dispose(); ReleaseInstanceAdmission(); throw;
        }
    }

    private void ReleaseInstanceAdmission()
    {
        lock (_lifecycle) if (--_active == 0 && _closing) _drained.TrySetResult();
    }

    public sealed class InstanceLease : IMirrorPulseInstanceScheduler, IAsyncDisposable
    {
        private readonly MirrorPulseInstanceScheduler _owner;
        private readonly SemaphoreSlim[] _held;
        private readonly Dictionary<InstanceId, SemaphoreSlim> _lanes;
        private readonly CancellationTokenSource _lifetime;
        private readonly object _lifecycle = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private bool _closing;
        private Task? _disposeTask;

        internal InstanceLease(MirrorPulseInstanceScheduler owner, InstanceId[] instances, SemaphoreSlim[] held,
            CancellationTokenSource lifetime)
        {
            _owner = owner; _held = held; _lifetime = lifetime;
            _lanes = instances.ToDictionary(instance => instance, _ => new SemaphoreSlim(1, 1));
        }

        public async ValueTask<T> RunAsync<T>(InstanceId instanceId, Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (!_lanes.TryGetValue(instanceId, out var lane))
                throw new InvalidOperationException("This instance was not admitted by the retained scheduler lease.");
            lock (_lifecycle) { ObjectDisposedException.ThrowIf(_closing, this); _active++; }
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                await lane.WaitAsync(linked.Token).ConfigureAwait(false);
                try { linked.Token.ThrowIfCancellationRequested(); return await operation(linked.Token).ConfigureAwait(false); }
                finally { lane.Release(); }
            }
            finally { lock (_lifecycle) if (--_active == 0 && _closing) _drained.TrySetResult(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lifecycle)
            {
                if (_disposeTask is not null) return new(_disposeTask);
                _closing = true; if (_active == 0) _drained.TrySetResult();
                return new(_disposeTask = DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally
            {
                await _drained.Task.ConfigureAwait(false);
                foreach (var lane in _lanes.Values) lane.Dispose();
                _lifetime.Dispose();
                foreach (var gate in _held.Reverse()) gate.Release();
                _owner.ReleaseInstanceAdmission();
            }
        }
    }
}
