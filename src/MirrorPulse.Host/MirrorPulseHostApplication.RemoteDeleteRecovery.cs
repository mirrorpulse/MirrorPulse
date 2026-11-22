using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private void RegisterRemoteDeleteRecovery(MirrorPulseControlDispatcher dispatcher)
    {
        dispatcher.Register<NamespaceRemoteDeleteStatusArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteStatus, (arguments, token) => ReadRemoteDeleteStatusAsync(arguments.CommandId, token));
        dispatcher.Register<NamespaceRemoteDeletesArguments, MirrorPulseControlRemoteDeletePage>(
            MirrorPulseControlCommands.NamespaceRemoteDeletes, async (arguments, token) =>
            {
                var page = await _catalog.ReadPendingRemoteDeleteResolutionsAsync(new RootId(arguments.RootId),
                    arguments.AfterCommandId, arguments.Limit, token).ConfigureAwait(false);
                var entries = new List<MirrorPulseControlRemoteDeleteStatus>(page.Count);
                foreach (var entry in page) entries.Add(await ReadRemoteDeleteStatusAsync(entry.Plan.Decision.CommandId, token).ConfigureAwait(false));
                return new(entries.AsReadOnly(), page.Count == arguments.Limit ? page[^1].Plan.Decision.CommandId : null);
            });
        dispatcher.Register<NamespaceRemoteDeleteRecoverArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteRecover, async (arguments, token) =>
            {
                _ = await _session.RecoverRemoteDeleteResolutionAsync(arguments.CommandId, token).ConfigureAwait(false);
                return await ReadRemoteDeleteStatusAsync(arguments.CommandId, token).ConfigureAwait(false);
            });
    }

    private async ValueTask<MirrorPulseControlRemoteDeleteStatus> ReadRemoteDeleteStatusAsync(Guid commandId, CancellationToken token)
    {
        var value = await _catalog.ReadRemoteDeleteResolutionStatusAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original explicit Delete command is not registered.");
        return new(value.CommandId, value.ConflictId, value.RootId.Value, value.InstanceId.Value, value.RelativePath,
            value.Stage, value.MemberCount, value.SettledMembers, value.NextOrdinal, value.PreparedAt, value.StartedAt,
            value.Outcome, value.OutcomeObservedAt, value.MembersCompletedAt, value.CommandCompleted, value.PackageVersion, value.PackageSource);
    }
}
