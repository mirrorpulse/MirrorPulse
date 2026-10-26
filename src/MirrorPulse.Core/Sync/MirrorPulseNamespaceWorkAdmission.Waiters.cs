using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

public sealed partial class MirrorPulseNamespaceWorkAdmission
{
    private const int MaximumRecoveryWaiters = 64;
    private readonly List<RecoveryWaiter> _recoveryWaiters = [];
    private TaskCompletionSource _recoveryChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Queues a bounded public recovery request behind earlier overlapping owners and requests.</summary>
    /// <remarks>Call before acquiring root work, instance/library serialization or the namespace actor.
    /// Waiting and cancellation cannot grant readiness. Existing internal BeginRecoveryAsync remains nonwaiting.</remarks>
    public async ValueTask<RecoveryLease> WaitForRecoveryAsync(IEnumerable<RootId> roots, CancellationToken cancellationToken = default)
    {
        RootId[] requested = Normalize(roots, allowEmpty: true); cancellationToken.ThrowIfCancellationRequested();
        var waiter = new RecoveryWaiter(Guid.NewGuid(), requested); bool claimed = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            foreach (RootId root in requested) _ = RequireRoot(root);
            if (_recoveryWaiters.Count >= MaximumRecoveryWaiters)
                throw new InvalidOperationException("The Host recovery request queue exceeds its bound.");
            _recoveryWaiters.Add(waiter); _operations++;
            foreach (RootId root in requested) _roots[root].Ready = false;
        }
        try
        {
            Task[] pending;
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closing, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    bool available = requested.All(root => RequireRoot(root).RecoveryOwner is null);
                    foreach (var earlier in _recoveryWaiters)
                    {
                        if (ReferenceEquals(earlier, waiter)) break;
                        if (earlier.Roots.Overlaps(waiter.Roots)) { available = false; break; }
                    }
                    if (available)
                    {
                        _recoveryWaiters.Remove(waiter);
                        pending = ClaimRecovery(requested, waiter.Owner, countOperation: false); claimed = true;
                        PulseRecoveryWaiters(); break;
                    }
                    changed = _recoveryChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) ObjectDisposedException.ThrowIf(_closing, this);
            return new(this, requested, waiter.Owner);
        }
        catch
        {
            if (claimed) ReleaseRecovery(requested, waiter.Owner, false);
            else lock (_gate)
            {
                _recoveryWaiters.Remove(waiter); ExitOperation(); PulseRecoveryWaiters();
            }
            throw;
        }
    }

    // All queue and root-state accesses occur under the same owner gate.
    private bool HasQueuedRecovery(RootId root) => _recoveryWaiters.Any(waiter => waiter.Roots.Contains(root));

    private Task[] ClaimRecovery(RootId[] roots, Guid owner, bool countOperation)
    {
        var pending = new Task[roots.Length];
        for (int index = 0; index < roots.Length; index++)
        {
            var state = RequireRoot(roots[index]);
            state.Ready = false; state.RecoveryOwner = owner;
            state.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (state.Active == 0) state.Drained.TrySetResult();
            pending[index] = state.Drained.Task;
        }
        if (countOperation) _operations++;
        return pending;
    }

    private void PulseRecoveryWaiters()
    {
        var previous = _recoveryChanged;
        _recoveryChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private sealed class RecoveryWaiter(Guid owner, RootId[] roots)
    {
        internal Guid Owner { get; } = owner;
        internal HashSet<RootId> Roots { get; } = roots.ToHashSet();
    }
}
