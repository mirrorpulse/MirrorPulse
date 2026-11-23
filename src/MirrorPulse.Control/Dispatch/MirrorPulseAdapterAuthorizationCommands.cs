using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Control.Dispatch;

public static class MirrorPulseAdapterAuthorizationCommands
{
    public static void Register(MirrorPulseControlDispatcher dispatcher, AdapterAuthorizationCoordinator authorization)
    {
        dispatcher.Register<InstanceAuthorizationStartArguments, AdapterAuthorizationStatus>(
            MirrorPulseControlCommands.InstanceAuthorizationStart, (arguments, token) => authorization.StartAsync(new(
                InstallId.Parse(arguments.InstallId),
                arguments.InstanceId is null ? null : InstanceId.Parse(arguments.InstanceId),
                arguments.DraftId is null ? null : InstanceId.Parse(arguments.DraftId),
                arguments.ExpectedConfigurationRevision, arguments.CallbackId, arguments.Configuration), token));
        dispatcher.Register<InstanceAuthorizationArguments, AdapterAuthorizationStatus>(
            MirrorPulseControlCommands.InstanceAuthorizationPoll, (arguments, token) => authorization.PollAsync(arguments.AuthorizationId, token));
        dispatcher.Register<InstanceAuthorizationArguments, AdapterAuthorizationStatus>(
            MirrorPulseControlCommands.InstanceAuthorizationCancel, (arguments, token) => authorization.CancelAsync(arguments.AuthorizationId, token));
    }
}
