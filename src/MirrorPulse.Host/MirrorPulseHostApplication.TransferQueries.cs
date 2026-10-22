using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private void RegisterTransferQueries(MirrorPulseControlDispatcher dispatcher)
    {
        dispatcher.Register<NamespaceTransferMoveArguments, MirrorPulseControlTransferStatus>(
            MirrorPulseControlCommands.NamespaceTransferMove, async (arguments, token) =>
            {
                if (arguments.OperationId == Guid.Empty || arguments.SourceMemberId == Guid.Empty || arguments.TargetParentMemberId == Guid.Empty)
                    throw new ArgumentException("Transfer operation, source member and target parent IDs are required.");
                await _session.PrepareNamespaceTransferAsync(arguments.OperationId, arguments.SourceMemberId,
                    arguments.TargetParentMemberId, arguments.TargetName, token).ConfigureAwait(false);
                return await ResumeNamespaceTransferAsync(arguments.OperationId, token).ConfigureAwait(false);
            });
        dispatcher.Register<NamespaceTransferResumeArguments, MirrorPulseControlTransferStatus>(
            MirrorPulseControlCommands.NamespaceTransferResume, (arguments, token) => ResumeNamespaceTransferAsync(arguments.OperationId, token));
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

    private async ValueTask<MirrorPulseControlTransferStatus> ResumeNamespaceTransferAsync(Guid operationId, CancellationToken token)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original transfer operation ID is required.", nameof(operationId));
        var copy = await _session.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
        if (copy.Acknowledgement?.AcknowledgedAt is null)
        {
            await _session.CopyNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
            await _session.VerifyNamespaceTransferTargetAsync(operationId, token).ConfigureAwait(false);
            await _session.PrepareNamespaceTransferTargetBirthAsync(operationId, token).ConfigureAwait(false);
            await _session.PublishNamespaceTransferTargetAsync(operationId, token).ConfigureAwait(false);
            await _session.RetireNamespaceTransferSourceAsync(operationId, token).ConfigureAwait(false);
            await _session.CompleteNamespaceTransferAsync(operationId, token).ConfigureAwait(false);
        }
        return ToControlTransferStatus(await _catalog.ReadNamespaceTransferStatusAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original transfer lost its durable status."));
    }

    private static MirrorPulseControlTransferStatus ToControlTransferStatus(MirrorPulseNamespaceTransferStatus value) =>
        new(value.OperationId, value.SourceRootId.Value, value.TargetRootId.Value, value.SourceInstanceId.Value, value.TargetInstanceId.Value,
            value.SourceMemberId, value.TargetMemberId, value.SourceRelativePath, value.TargetRelativePath, value.Stage, value.PreparedAt,
            value.CopyStartedAt, value.CopyAcceptedAt, value.SourceDeleteStartedAt, value.SourceDeleteAcceptedAt, value.SourceNativeRetiredAt,
            value.SourceNameSettledAt, value.RootsFenced, value.JournalAcknowledged, value.Completed);
}
