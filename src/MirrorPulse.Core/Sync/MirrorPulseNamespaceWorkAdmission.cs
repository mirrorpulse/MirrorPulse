using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Coordinates ordinary work and fresh recovery for each stable root in one Host lifetime.</summary>
/// <remarks>Roots start closed. Only the owner of a drained recovery scope may request reopening
/// after an actual fresh audit. Persistent historical completion is not live admission.</remarks>
public sealed class MirrorPulseNamespaceWorkAdmission : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<RootId, RootState> _roots = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _closing;
    private Task? _disposeTask;

    public MirrorPulseNamespaceWorkAdmission(IEnumerable<RootId> roots)
    {
        foreach (RootId root in Normalize(roots, allowEmpty: true)) _roots.Add(root, new());
    }

    /// <summary>Registers a newly configured root as closed without changing existing admission.</summary>
    public void Register(RootId root)
    {
        if (root.Value == Guid.Empty) throw new ArgumentException("A stable root ID is required.", nameof(root));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (!_roots.ContainsKey(root) && _roots.Count >= 4096) throw new InvalidOperationException("The Host root inventory exceeds its bound.");
            _roots.TryAdd(root, new());
        }
    }

    public bool IsAdmitted(RootId root)
    {
        lock (_gate) return !_closing && _roots.TryGetValue(root, out var state) && state.Ready && state.RecoveryOwner is null;
    }

    /// <summary>Retains ordinary work through all awaited operations and lazy-body disposal.</summary>
    public WorkLease Enter(IEnumerable<RootId> roots)
    {
        if (!TryEnter(roots, out var lease))
            throw new InvalidOperationException("The root requires fresh Host namespace recovery before accepting ordinary work.");
        return lease!;
    }

    /// <summary>Atomically defers closed roots without reserving work in another selected root.</summary>
    public bool TryEnter(IEnumerable<RootId> roots, out WorkLease? lease)
    {
        RootId[] requested = Normalize(roots, allowEmpty: false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            lease = null;
            if (requested.Any(root => !_roots.ContainsKey(root))) return false;
            var states = requested.Select(RequireRoot).ToArray();
            if (states.Any(state => !state.Ready || state.RecoveryOwner is not null))
                return false;
            foreach (var state in states) state.Active++;
            _operations++; lease = new(this, requested); return true;
        }
    }

    public async ValueTask<T> RunAsync<T>(RootId root, Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work); cancellationToken.ThrowIfCancellationRequested();
        using var lease = Enter([root]); return await work(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retains execution of an already admitted durable operation, including on a closed root.</summary>
    /// <remarks>The Host must validate the original operation before entering. A closed-root
    /// execution drains earlier work and remains closed afterwards; completion is not a fresh audit.
    /// New preparation and ordinary synchronization must continue using Enter.</remarks>
    public async ValueTask<IAsyncDisposable> EnterPendingOperationAsync(RootId root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryEnter([root], out var work)) return new PendingOperationLease(work!, null);
        return new PendingOperationLease(null, await BeginRecoveryAsync([root], cancellationToken).ConfigureAwait(false));
    }

    private sealed class PendingOperationLease(WorkLease? work, RecoveryLease? recovery) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref work, null)?.Dispose();
            var owned = Interlocked.Exchange(ref recovery, null);
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes all selected roots atomically, then drains already accepted work.</summary>
    /// <remarks>Failure or cancellation leaves the roots closed; it never restores stale readiness.
    /// An empty inventory retains the owner lifecycle while its sync-root anchor is
    /// recovered. Completing that scope grants no root admission.</remarks>
    public async ValueTask<RecoveryLease> BeginRecoveryAsync(IEnumerable<RootId> roots, CancellationToken cancellationToken = default)
    {
        RootId[] requested = Normalize(roots, allowEmpty: true); cancellationToken.ThrowIfCancellationRequested();
        Guid owner = Guid.NewGuid(); Task[] pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var states = requested.Select(RequireRoot).ToArray();
            if (states.Any(state => state.RecoveryOwner is not null))
                throw new InvalidOperationException("An existing recovery scope owns one of the selected roots.");
            foreach (var state in states)
            {
                state.Ready = false; state.RecoveryOwner = owner;
                state.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (state.Active == 0) state.Drained.TrySetResult();
            }
            _operations++; pending = states.Select(state => state.Drained!.Task).ToArray();
        }
        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) ObjectDisposedException.ThrowIf(_closing, this);
            return new(this, requested, owner);
        }
        catch { ReleaseRecovery(requested, owner, false); throw; }
    }

    private RootState RequireRoot(RootId root) => _roots.TryGetValue(root, out var state) ? state :
        throw new InvalidOperationException("The root is not registered in this Host's namespace admission owner.");

    private void ReleaseWork(RootId[] roots)
    {
        lock (_gate)
        {
            foreach (RootId root in roots)
            {
                var state = _roots[root];
                if (--state.Active == 0) state.Drained?.TrySetResult();
            }
            ExitOperation();
        }
    }

    private void ReleaseRecovery(RootId[] roots, Guid owner, bool audited)
    {
        lock (_gate)
        {
            foreach (RootId root in roots)
            {
                var state = _roots[root];
                if (state.RecoveryOwner != owner) throw new InvalidOperationException("The recovery scope lost its root ownership.");
                state.RecoveryOwner = null; state.Drained = null; state.Ready = audited && !_closing;
            }
            ExitOperation();
        }
    }

    private void ExitOperation()
    {
        if (--_operations == 0 && _closing) _drained.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closing = true;
            foreach (var state in _roots.Values) state.Ready = false;
            if (_operations == 0) _drained.TrySetResult();
            _disposeTask = _drained.Task; return new(_disposeTask);
        }
    }

    private static RootId[] Normalize(IEnumerable<RootId> roots, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(roots); RootId[] requested = roots.Take(4097).ToArray();
        if (requested.Length > 4096 || !allowEmpty && requested.Length == 0 ||
            requested.Any(root => root.Value == Guid.Empty) || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException("Namespace admission requires a bounded set of distinct stable root IDs.", nameof(roots));
        return requested;
    }

    private sealed class RootState
    {
        public bool Ready { get; set; }
        public int Active { get; set; }
        public Guid? RecoveryOwner { get; set; }
        public TaskCompletionSource? Drained { get; set; }
    }

    public sealed class WorkLease : IDisposable
    {
        private MirrorPulseNamespaceWorkAdmission? _owner;
        private readonly RootId[] _roots;
        internal WorkLease(MirrorPulseNamespaceWorkAdmission owner, RootId[] roots) => (_owner, _roots) = (owner, roots);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWork(_roots);
    }

    public sealed class RecoveryLease : IAsyncDisposable
    {
        private readonly object _gate = new();
        private MirrorPulseNamespaceWorkAdmission? _owner;
        private readonly RootId[] _roots;
        private readonly Guid _recoveryId;
        private bool _audited;
        internal RecoveryLease(MirrorPulseNamespaceWorkAdmission owner, RootId[] roots, Guid recoveryId) =>
            (_owner, _roots, _recoveryId) = (owner, roots, recoveryId);

        /// <summary>Requests reopening on scope exit after this scope's actual fresh native audit.</summary>
        public void CompleteAfterFreshAudit()
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_owner is null, this); _audited = true; }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                var owner = _owner; _owner = null;
                owner?.ReleaseRecovery(_roots, _recoveryId, _audited); return ValueTask.CompletedTask;
            }
        }
    }
}
