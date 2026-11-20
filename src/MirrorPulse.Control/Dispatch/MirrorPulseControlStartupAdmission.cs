using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Control.Dispatch;

/// <summary>Keeps SDK-dependent requests closed while exposing lifecycle observation and cancellation.</summary>
public static class MirrorPulseControlStartupAdmission
{
    public static ControlResponseEnvelope? Reject(MirrorPulseLifecycleState state, ControlRequestEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (state is MirrorPulseLifecycleState.Running or MirrorPulseLifecycleState.Degraded ||
            request.Command is MirrorPulseControlCommands.HostStatus or MirrorPulseControlCommands.HostStart or
                MirrorPulseControlCommands.HostStop or MirrorPulseControlCommands.HostRestart) return null;
        return ControlResponseEnvelope.Failure(request.RequestId, new ControlError(MirrorPulseControlErrorCodes.HostBusy,
            "The Host has not completed initialization or is stopping.", ErrorCategory.Native, retryable: true));
    }
}
