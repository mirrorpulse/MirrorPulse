using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retries original retained input in bounded, rotating owner pages without enumerating Adapter data.</summary>
internal sealed class MirrorPulseRetainedRemoteBatchRecoveryPump(
    MirrorPulseProductCatalog catalog, Func<InstanceId, bool>? mayDispatch,
    Func<InstanceId, CancellationToken, ValueTask<bool>> resume, TimeSpan? interval = null) : IAsyncDisposable
{
    private readonly TimeSpan _interval = interval is null ? TimeSpan.FromSeconds(30) :
        interval > TimeSpan.Zero ? interval.Value : throw new ArgumentOutOfRangeException(nameof(interval));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _lifetime = new();
    private InstanceId? _after;
    private Task? _loop;
    private Task? _disposal;

    internal void Start()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_loop is not null) throw new InvalidOperationException("Retained remote recovery has already started.");
            _loop = RunAsync();
        }
    }

    /// <summary>Visits at most 64 retained owners, advancing past offline, fenced and failed owners.</summary>
    internal async Task RecoverOnceAsync(CancellationToken token)
    {
        lock (_lifetime) ObjectDisposedException.ThrowIf(_disposal is not null, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _turn.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var owners = await catalog.ReadRetainedRemoteBatchOwnersAsync(_after, stop.Token).ConfigureAwait(false);
            if (owners.Count == 0 && _after is not null)
            {
                _after = null;
                owners = await catalog.ReadRetainedRemoteBatchOwnersAsync(null, stop.Token).ConfigureAwait(false);
            }
            foreach (var instance in owners)
            {
                stop.Token.ThrowIfCancellationRequested();
                try
                {
                    if (mayDispatch is not null && !mayDispatch(instance)) continue;
                    if (await resume(instance, stop.Token).ConfigureAwait(false)) continue;
                    await catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(instance, stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    // Preserve the envelope and isolate this owner, including a failed status write.
                    // Ordinary diagnostics must not include batch bodies or exception/source text.
                    try { await catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(instance, stop.Token).ConfigureAwait(false); }
                    catch (Exception statusError) when (statusError is not OperationCanceledException || !stop.IsCancellationRequested) { }
                }
                finally { _after = instance; }
            }
        }
        finally { _turn.Release(); }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                // Delay after every bounded turn, also after an unchanged retry or scan failure.
                await Task.Delay(_interval, _shutdown.Token).ConfigureAwait(false);
                try { await RecoverOnceAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !_shutdown.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* A failed cancellation callback must not bypass the original owner drain. */ }
        if (_loop is not null) await _loop.ConfigureAwait(false);
        // The Runtime awaits this outside its library/scheduler gates and before disposing their owners.
        await _turn.WaitAsync().ConfigureAwait(false);
        _turn.Dispose();
        _shutdown.Dispose();
    }
}
