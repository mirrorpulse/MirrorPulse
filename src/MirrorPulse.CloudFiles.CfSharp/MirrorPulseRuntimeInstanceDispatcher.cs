using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Orders instance work and current-library ownership for controlled Host operations.</summary>
internal static class MirrorPulseRuntimeInstanceDispatcher
{
    internal static async ValueTask<TResult> RunRecoveryAsync<TPreparation, TAdmission, TResult>(SemaphoreSlim requests,
        MirrorPulseInstanceScheduler scheduler, IEnumerable<InstanceId> instances,
        Func<CancellationToken, ValueTask<(TPreparation Preparation, Task<TAdmission?> Drain)>> prepareWithinLibrary,
        Func<TPreparation, TAdmission?, MirrorPulseInstanceScheduler.InstanceLease, CancellationToken, ValueTask<TResult>> executeWithinLibrary,
        CancellationToken cancellationToken) where TAdmission : class, IAsyncDisposable
    {
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Preflight starts root closure under the current library, but returns its
            // pending drain. Earlier work must be able to reacquire that library and
            // finish before this recovery owns any physical instance gate.
            var pending = await prepareWithinLibrary(cancellationToken).ConfigureAwait(false);
            await using var roots = await pending.Drain.ConfigureAwait(false);
            await using var admitted = await scheduler.AcquireAsync(instances, cancellationToken).ConfigureAwait(false);
            return await executeWithinLibrary(pending.Preparation, roots, admitted, cancellationToken).ConfigureAwait(false);
        }
        finally { requests.Release(); }
    }

    internal static async ValueTask<T> RunAsync<T>(MirrorPulseInstanceScheduler scheduler, InstanceId instance,
        Func<Func<CancellationToken, ValueTask<T>>, CancellationToken, ValueTask<T>> withCurrentLibrary,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        await using var admission = await scheduler.AcquireAsync([instance], cancellationToken).ConfigureAwait(false);
        return await admission.RunAsync(instance, token => withCurrentLibrary(operation, token), cancellationToken).ConfigureAwait(false);
    }
}
