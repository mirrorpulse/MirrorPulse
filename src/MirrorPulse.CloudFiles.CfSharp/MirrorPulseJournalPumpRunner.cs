using System.Collections.Concurrent;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseJournalPumpHealth(bool Healthy, int PendingFaults, string? LastErrorCode);

/// <summary>Isolates source, dispatch and reporting failures without acknowledging failed commands.</summary>
public sealed class MirrorPulseJournalPumpRunner
{
    private readonly ConcurrentDictionary<Guid, string> _faults = new();
    private readonly ConcurrentDictionary<Guid, string> _coalescingRecoveryFaults = new();
    private readonly ConcurrentDictionary<Guid, string> _conflictDeletionRecoveryFaults = new();
    private readonly ConcurrentDictionary<Guid, string> _keepBothRecoveryFaults = new();
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
    internal void RetainCoalescingRecoveryFault(Guid planId) => _coalescingRecoveryFaults[planId] = "JournalGroupRecoveryFailed";
    internal void ClearCoalescingRecoveryFault(Guid planId) => _coalescingRecoveryFaults.TryRemove(planId, out _);
    internal void RetainConflictDeletionRecoveryFault(Guid commandId) => _conflictDeletionRecoveryFaults[commandId] = "ConflictDeletionRecoveryFailed";
    internal void ClearConflictDeletionRecoveryFault(Guid commandId) => _conflictDeletionRecoveryFaults.TryRemove(commandId, out _);
    internal IReadOnlyList<Guid> ReadConflictDeletionRecoveryFaults(Guid? after) => _conflictDeletionRecoveryFaults.Keys
        .Where(id => after is null || string.CompareOrdinal(id.ToString("D"), after.Value.ToString("D")) > 0)
        .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Take(64).ToArray();
    internal void RetainKeepBothRecoveryFault(Guid commandId) => _keepBothRecoveryFaults[commandId] = "ConflictKeepBothRecoveryFailed";
    internal void ClearKeepBothRecoveryFault(Guid commandId) => _keepBothRecoveryFaults.TryRemove(commandId, out _);
    internal IReadOnlyList<Guid> ReadKeepBothRecoveryFaults(Guid? after) => _keepBothRecoveryFaults.Keys
        .Where(id => after is null || string.CompareOrdinal(id.ToString("D"), after.Value.ToString("D")) > 0)
        .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Take(64).ToArray();
    public MirrorPulseJournalPumpHealth Health => new(_faults.IsEmpty && _coalescingRecoveryFaults.IsEmpty && _conflictDeletionRecoveryFaults.IsEmpty && _keepBothRecoveryFaults.IsEmpty,
        _faults.Count + _coalescingRecoveryFaults.Count + _conflictDeletionRecoveryFaults.Count + _keepBothRecoveryFaults.Count,
        _faults.Values.Concat(_coalescingRecoveryFaults.Values).Concat(_conflictDeletionRecoveryFaults.Values).Concat(_keepBothRecoveryFaults.Values).Order(StringComparer.Ordinal).FirstOrDefault());

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
        IReadOnlyList<MirrorPulseWorkerChangeCommand> ordered = ReferenceEquals(batch.ValidatedSequenceCommands, batch.ReadyCommands)
            ? batch.ReadyCommands : batch.ReadyCommands.OrderBy(command => command.Sequence).ToArray();
        var routes = ordered.GroupBy(command => (command.InstanceId, command.RootKey))
            .Select(group => (Key: group.Key, Commands: new Queue<MirrorPulseWorkerChangeCommand>(group), Inspected: 0)).ToArray();
        if (routes.Length == 0) return false;
        int route = _nextRoute is { } cursor ? Array.FindIndex(routes, entry => entry.Key == cursor) : 0;
        if (route < 0) route = 0;
        // Visit each eligible route directly. A root that exhausts its commands
        // or per-cycle budget leaves this ring, rather than participating in
        // repeated global Any scans and skipped round-robin turns.
        var eligibleRoutes = new Queue<int>(routes.Length);
        for (int offset = 0; offset < routes.Length; offset++) eligibleRoutes.Enqueue((route + offset) % routes.Length);
        var acceptedOperations = new HashSet<Guid>();
        int inspected = 0;
        while (inspected < _maximumCommandsPerCycle && eligibleRoutes.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int selected = eligibleRoutes.Dequeue();
            _nextRoute = routes[(selected + 1) % routes.Length].Key;
            MirrorPulseWorkerChangeCommand command = routes[selected].Commands.Dequeue();
            routes[selected].Inspected++;
            if (routes[selected].Commands.Count > 0 && routes[selected].Inspected < _maximumCommandsPerRoot) eligibleRoutes.Enqueue(selected);
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
