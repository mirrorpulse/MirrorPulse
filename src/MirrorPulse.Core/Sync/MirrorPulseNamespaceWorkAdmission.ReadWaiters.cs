using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

public sealed partial class MirrorPulseNamespaceWorkAdmission
{
    private const int MaximumPendingReads = 256;
    private int _pendingReads;

    /// <summary>Unadmitted callbacks awaiting existing recovery; this count grants no work authority.</summary>
    public int PendingReadCount { get { lock (_gate) return _pendingReads; } }

    /// <summary>Retains ordinary work after any already running root recovery finishes.</summary>
    /// <remarks>Waiting holds no ordinary lease and never grants readiness. Unknown roots,
    /// closed roots without a recovery and unsuccessful audits remain refused. Recovery
    /// requests retain priority; callback cancellation and owner disposal wake pending reads.</remarks>
    public async ValueTask<WorkLease> EnterAfterRecoveryAsync(IEnumerable<RootId> roots, CancellationToken cancellationToken)
    {
        RootId[] requested = Normalize(roots, allowEmpty: false);
        bool waiting = false;
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closing, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryEnter(requested, out var lease)) return lease!;
                    // A pending read may wait for an existing recovery, but cannot
                    // reopen an idle closed/disabled root or invent a future audit.
                    if (requested.Any(root => !_roots.TryGetValue(root, out var state) ||
                        !state.Ready && state.RecoveryOwner is null && !HasQueuedRecovery(root)))
                        throw new InvalidOperationException("The root requires fresh Host namespace recovery before accepting ordinary work.");
                    if (!waiting)
                    {
                        if (_pendingReads >= MaximumPendingReads)
                            throw new InvalidOperationException("The Host pending read queue exceeds its bound.");
                        _pendingReads++; waiting = true;
                    }
                    changed = _recoveryChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Unadmitted reads do not participate in the drain they are awaiting.
            // The native session cancels callbacks before draining its dispatcher.
            if (waiting) lock (_gate) _pendingReads--;
        }
    }
}
