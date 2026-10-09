using System.Collections.Concurrent;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseJournalPumpHealth(bool Healthy, int PendingFaults, string? LastErrorCode);

/// <summary>Isolates source, dispatch and reporting failures without acknowledging failed commands.</summary>
public sealed class MirrorPulseJournalPumpRunner
{
    private readonly ConcurrentDictionary<Guid, string> _faults = new();
    private readonly int _maximumCommandsPerCycle;
    private readonly int _maximumCommandsPerRoot;
    private (InstanceId Instance, string Root)? _nextRoute;

    public MirrorPulseJournalPumpRunner(int maximumCommandsPerCycle = 64, int maximumCommandsPerRoot = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommandsPerCycle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommandsPerRoot);
        _maximumCommandsPerCycle = maximumCommandsPerCycle;
        _maximumCommandsPerRoot = maximumCommandsPerRoot;
    }
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
        MirrorPulseWorkerChangeCommand[] ordered = batch.ReadyCommands.OrderBy(command => command.Sequence).ToArray();
        var routes = ordered.GroupBy(command => (command.InstanceId, command.RootKey))
            .Select(group => (Key: group.Key, Commands: new Queue<MirrorPulseWorkerChangeCommand>(group), Inspected: 0)).ToArray();
        if (routes.Length == 0) return false;
        int route = _nextRoute is { } cursor ? Array.FindIndex(routes, entry => entry.Key == cursor) : 0;
        if (route < 0) route = 0;
        var acceptedOperations = new HashSet<Guid>();
        int inspected = 0;
        while (inspected < _maximumCommandsPerCycle && routes.Any(entry => entry.Commands.Count > 0 && entry.Inspected < _maximumCommandsPerRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int selected = route;
            route = (route + 1) % routes.Length;
            _nextRoute = routes[route].Key;
            if (routes[selected].Commands.Count == 0 || routes[selected].Inspected >= _maximumCommandsPerRoot) continue;
            MirrorPulseWorkerChangeCommand command = routes[selected].Commands.Dequeue();
            routes[selected].Inspected++;
            inspected++;
            // An earlier operation outside this cycle's budget still fences its
            // affected objects/subtree. Round-robin ordering is safe only for
            // commands independent of every earlier, unaccepted original ID.
            if (ordered.TakeWhile(earlier => earlier != command)
                .Any(earlier => !acceptedOperations.Contains(earlier.OperationId) && MirrorPulseJournalOrderPolicy.DependsOn(command, earlier)))
                continue;
            try
            {
                bool accepted = await dispatch(command, cancellationToken).ConfigureAwait(false);
                progressed |= accepted;
                if (accepted)
                {
                    acceptedOperations.Add(command.OperationId);
                    _faults.TryRemove(command.OperationId, out _);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
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
