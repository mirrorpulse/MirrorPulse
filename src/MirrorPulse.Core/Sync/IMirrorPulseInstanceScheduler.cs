using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Serializes work within an instance through an explicitly owned admission boundary.</summary>
public interface IMirrorPulseInstanceScheduler
{
    ValueTask<T> RunAsync<T>(InstanceId instanceId, Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);
}
