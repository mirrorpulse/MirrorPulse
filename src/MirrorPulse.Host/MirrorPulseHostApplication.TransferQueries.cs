using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private void RegisterTransferQueries(MirrorPulseControlDispatcher dispatcher)
    {
        dispatcher.Register<NamespaceTransferStatusArguments, MirrorPulseControlTransferStatus>(
            MirrorPulseControlCommands.NamespaceTransferStatus, async (arguments, token) =>
                ToControlTransferStatus(await _catalog.ReadNamespaceTransferStatusAsync(arguments.OperationId, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original transfer is not registered.")));
        dispatcher.Register<NamespaceTransfersArguments, MirrorPulseControlTransfers>(
            MirrorPulseControlCommands.NamespaceTransfers, async (arguments, token) =>
            {
                var values = await _catalog.ReadPendingNamespaceTransferStatusesAsync(new RootId(arguments.RootId), token).ConfigureAwait(false);
                return new(values.Select(ToControlTransferStatus).ToArray());
            });
    }

    private static MirrorPulseControlTransferStatus ToControlTransferStatus(MirrorPulseNamespaceTransferStatus value) =>
        new(value.OperationId, value.SourceRootId.Value, value.TargetRootId.Value, value.SourceInstanceId.Value, value.TargetInstanceId.Value,
            value.SourceMemberId, value.TargetMemberId, value.SourceRelativePath, value.TargetRelativePath, value.Stage, value.PreparedAt,
            value.CopyStartedAt, value.CopyAcceptedAt, value.SourceDeleteStartedAt, value.SourceDeleteAcceptedAt, value.SourceNativeRetiredAt,
            value.SourceNameSettledAt, value.RootsFenced, value.JournalAcknowledged, value.Completed);
}
