using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private void RegisterRemoteUpdateRecovery(MirrorPulseControlDispatcher dispatcher)
    {
        dispatcher.Register<NamespaceRemoteUpdateStatusArguments, MirrorPulseControlRemoteUpdateStatus>(
            MirrorPulseControlCommands.NamespaceRemoteUpdateStatus, (arguments, token) => ReadRemoteUpdateStatusAsync(arguments.OperationId, token));
        dispatcher.Register<NamespaceRemoteUpdatesArguments, MirrorPulseControlRemoteUpdates>(
            MirrorPulseControlCommands.NamespaceRemoteUpdates, async (arguments, token) =>
            {
                var pending = await _catalog.ReadPendingNamespaceRemoteUpdatesAsync(new RootId(arguments.RootId), token).ConfigureAwait(false);
                var entries = new List<MirrorPulseControlRemoteUpdateStatus>(pending.Count);
                foreach (var entry in pending) entries.Add(await ReadRemoteUpdateStatusAsync(entry.Plan.OperationId, token).ConfigureAwait(false));
                return new(entries.AsReadOnly());
            });
        dispatcher.Register<NamespaceRemoteUpdateRecoverArguments, MirrorPulseControlRemoteUpdateStatus>(
            MirrorPulseControlCommands.NamespaceRemoteUpdateRecover, async (arguments, token) =>
            {
                _ = await _session.RecoverRemoteUpdateAsync(arguments.OperationId, token).ConfigureAwait(false);
                return await ReadRemoteUpdateStatusAsync(arguments.OperationId, token).ConfigureAwait(false);
            });
    }

    private async ValueTask<MirrorPulseControlRemoteUpdateStatus> ReadRemoteUpdateStatusAsync(Guid operationId, CancellationToken token)
    {
        var value = await _catalog.ReadNamespaceRemoteUpdateStatusAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original remote update operation is not registered.");
        return new(value.OperationId, value.RootId.Value, value.InstanceId.Value, value.MemberId, value.RelativePath,
            value.BatchId, value.Ordinal, value.ChangeId, value.Stage, value.PreparedAt, value.StartedAt,
            value.Outcome, value.EvidenceSource, value.OutcomeObservedAt, value.PackageVersion, value.PackageSource);
    }
}
