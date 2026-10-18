using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private void RegisterRemoteRetirementQueries(MirrorPulseControlDispatcher dispatcher)
    {
        dispatcher.Register<NamespaceRemoteRetirementStatusArguments, MirrorPulseControlRemoteRetirementStatus>(
            MirrorPulseControlCommands.NamespaceRemoteRetirementStatus, ReadRemoteRetirementStatusAsync);
        dispatcher.Register<NamespaceRemoteRetirementsArguments, MirrorPulseControlRemoteRetirementPage>(
            MirrorPulseControlCommands.NamespaceRemoteRetirements, ReadRemoteRetirementsAsync);
    }

    private async ValueTask<MirrorPulseControlRemoteRetirementStatus> ReadRemoteRetirementStatusAsync(
        NamespaceRemoteRetirementStatusArguments arguments, CancellationToken token)
    {
        var status = await _catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(arguments.OperationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original SDK directory operation is not registered.");
        return MapRemoteRetirementStatus(status);
    }

    private async ValueTask<MirrorPulseControlRemoteRetirementPage> ReadRemoteRetirementsAsync(
        NamespaceRemoteRetirementsArguments arguments, CancellationToken token)
    {
        var page = await _catalog.ReadNamespaceRemoteRetirementTreeStatusesAsync(new RootId(arguments.RootId),
            arguments.AfterOperationId, arguments.Limit, token).ConfigureAwait(false);
        return new(page.Entries.Select(MapRemoteRetirementStatus).ToArray(), page.NextOperationId);
    }

    internal static MirrorPulseControlRemoteRetirementStatus MapRemoteRetirementStatus(MirrorPulseNamespaceRemoteRetirementTreeStatus status) =>
        new(status.OperationId, status.MemberId, status.RootId.Value, status.InstanceId.Value, status.BatchId, status.Ordinal,
            status.RelativePath, status.Phase, status.Stage, status.PreparedAt, status.StartedAt, status.RootOutcome,
            status.RootNativeDeleted, status.RootOfficialTombstone, status.RootRemoteRevision, status.ConflictId, status.ConflictReason,
            status.MemberCount, status.SettledMembers, status.RemovedMembers, status.RetainedMembers, status.NextOrdinal,
            status.OutcomeObservedAt, status.CompletedAt, status.PackageVersion, status.PackageSource);
}
