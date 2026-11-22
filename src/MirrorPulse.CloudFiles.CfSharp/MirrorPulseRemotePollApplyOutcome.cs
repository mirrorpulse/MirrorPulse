using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Whether CfSharp durably completed a polled batch and its safe cursor.</summary>
public sealed record MirrorPulseRemotePollApplyOutcome(bool Completed, ReadOnlyMemory<byte> SafeCursor)
{
    public static MirrorPulseRemotePollApplyOutcome FromResult(MirrorPulseRemoteBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Status == CloudRemoteBatchStatus.Applied && !result.RequiresRetry, result.SafeCursor.ToArray());
    }

    public static MirrorPulseRemotePollApplyOutcome FromResult(CloudRemoteApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Status == CloudRemoteBatchStatus.Applied && !result.RequiresRetry,
            result.SafeCursor.ToArray());
    }
}
