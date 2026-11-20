using System.Runtime.ExceptionServices;

namespace MirrorPulse.Core.Host;

/// <summary>Awaits ordered Host resource drains and preserves every failure without skipping later owners.</summary>
public static class MirrorPulseHostResourceCleanup
{
    public static async Task RunAsync(IReadOnlyList<Func<ValueTask>> cleanup, Exception? originalFailure = null)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        var failures = new List<Exception>();
        if (originalFailure is not null) failures.Add(originalFailure);
        foreach (var dispose in cleanup)
        {
            try { await dispose().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Host resource cleanup encountered multiple failures.", failures);
    }
}
