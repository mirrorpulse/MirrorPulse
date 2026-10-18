using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private readonly object _disposal = new();
    private Task? _disposeTask;

    public ValueTask DisposeAsync()
    {
        lock (_disposal) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _state = MirrorPulseLifecycleState.Stopping;
        try
        {
            await MirrorPulseHostResourceCleanup.RunAsync([
                () => new(_shutdown.CancelAsync()),
                DrainControlAsync,
                _remotePoller.DisposeAsync,
                _remoteScheduler.DisposeAsync,
                // Stop queued callbacks and await admitted work while the library and identities remain live.
                _namespaceActor.DisposeAsync,
                _session.DisposeAsync,
                _namespaceWork.DisposeAsync,
                _workers.DisposeAsync,
                // Normal-user RPC bodies and source disposal finish before releasing captured identities.
                _namespaceExecution.DisposeAsync,
                _catalog.DisposeAsync,
                () => { _systemNotifications.Dispose(); return ValueTask.CompletedTask; },
                () => { _shutdown.Dispose(); return ValueTask.CompletedTask; },
                () => { _hostLease.Dispose(); return ValueTask.CompletedTask; }
            ]).ConfigureAwait(false);
            _state = MirrorPulseLifecycleState.Stopped;
        }
        catch { _state = MirrorPulseLifecycleState.Failed; throw; }
    }

    private async ValueTask DrainControlAsync()
    {
        if (_serveTask is null || _lifetimeDrained) return;
        try { await _serveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
}
