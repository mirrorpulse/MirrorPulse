using System.Collections.Concurrent;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseJournalPumpHealth(bool Healthy, int PendingFaults, string? LastErrorCode);

/// <summary>Isolates source, dispatch and reporting failures without acknowledging failed commands.</summary>
public sealed class MirrorPulseJournalPumpRunner
{
    private readonly ConcurrentDictionary<Guid, string> _faults = new();
    public void ClearRecoveredFault(Guid operationId) => _faults.TryRemove(operationId, out _);
    public MirrorPulseJournalPumpHealth Health => new(_faults.IsEmpty, _faults.Count,
        _faults.Values.Order(StringComparer.Ordinal).FirstOrDefault());

    public async ValueTask<bool> RunCycleAsync(
        Func<CancellationToken, ValueTask<MirrorPulseJournalUploadBatch>> read,
        Func<MirrorPulseWorkerChangeCommand, CancellationToken, ValueTask<bool>> dispatch,
        Func<MirrorPulseWorkerChangeCommand?, string, Exception, CancellationToken, ValueTask> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(report);
        MirrorPulseJournalUploadBatch batch;
        try
        {
            batch = await read(cancellationToken).ConfigureAwait(false);
            _faults.TryRemove(Guid.Empty, out _);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportAsync(null, exception is MirrorPulseMutationAmbiguousException ? "MutationOutcomeAmbiguous" : "JournalReadFailed",
                exception, report, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (batch.RequiresFullRescan) return false;
        bool progressed = false;
        var held = new List<MirrorPulseWorkerChangeCommand>();
        foreach (MirrorPulseWorkerChangeCommand command in batch.ReadyCommands.OrderBy(command => command.Sequence))
        {
            if (held.Any(earlier => MirrorPulseJournalOrderPolicy.DependsOn(command, earlier)))
            {
                held.Add(command);
                continue;
            }
            try
            {
                bool accepted = await dispatch(command, cancellationToken).ConfigureAwait(false);
                progressed |= accepted;
                if (accepted) _faults.TryRemove(command.OperationId, out _);
                else held.Add(command);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                held.Add(command);
                await ReportAsync(command, exception is MirrorPulseJournalAcknowledgementException
                    ? "JournalAcknowledgementFailed" : exception is MirrorPulseMutationAmbiguousException ? "MutationOutcomeAmbiguous" :
                    "JournalCommandFailed", exception, report, cancellationToken).ConfigureAwait(false);
            }
        }
        return progressed;
    }

    private async ValueTask ReportAsync(MirrorPulseWorkerChangeCommand? command, string code, Exception exception,
        Func<MirrorPulseWorkerChangeCommand?, string, Exception, CancellationToken, ValueTask> report,
        CancellationToken cancellationToken)
    {
        _faults[command?.OperationId ?? Guid.Empty] = code;
        try { await report(command, code, exception, cancellationToken).ConfigureAwait(false); }
        catch (Exception reportingFailure) when (reportingFailure is not OperationCanceledException)
        {
            // The in-memory fault remains observable even while the product catalog is unavailable.
        }
    }
}

public sealed class MirrorPulseJournalAcknowledgementException : IOException
{
    public MirrorPulseJournalAcknowledgementException() { }
    public MirrorPulseJournalAcknowledgementException(string? message) : base(message) { }
    public MirrorPulseJournalAcknowledgementException(string? message, Exception? innerException) : base(message, innerException) { }
}
