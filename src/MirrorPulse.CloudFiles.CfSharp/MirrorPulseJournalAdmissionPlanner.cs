using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Bounds final bookkeeping, dependency indexing and projection without exposing a partial batch.</summary>
/// <remarks>The complete original routing plan already exists. Callers recheck feed loss and root fences
/// each turn, and retain the normal durable request/ACK boundaries. This continuation is ephemeral;
/// cancellation or failure requires a new read of the authoritative official journal.</remarks>
internal sealed class MirrorPulseJournalAdmissionPlanner
{
    private readonly MirrorPulseLocalBatchPlan _plan;
    private readonly int _budget;
    private readonly List<MirrorPulseWorkerChangeCommand> _ready = [];
    private readonly List<MirrorPulseWorkerChangeCommand> _complete = [];
    private readonly HashSet<Guid> _acceptedMetadata = [];
    private readonly MirrorPulseJournalOrderIndex _held = new();
    private int _stage;
    private int _next;
    private int _deferred;
    private int _reading;
    private long _lastSequence;
    private bool _failed;
    private MirrorPulseJournalUploadBatch? _result;

    internal MirrorPulseJournalAdmissionPlanner(MirrorPulseLocalBatchPlan plan, long upperSequence, int operationsPerTurn = 512)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.RequiresFullRescan) throw new ArgumentException("Admission requires a complete original routing plan.", nameof(plan));
        ArgumentOutOfRangeException.ThrowIfNegative(upperSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(operationsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationsPerTurn, 4096);
        _plan = plan; UpperSequence = upperSequence; _budget = operationsPerTurn;
        _deferred = plan.BlockedOperations?.Count ?? 0;
    }

    internal long UpperSequence { get; }
    internal MirrorPulseJournalUploadBatch Result => _result ?? throw new InvalidOperationException("Final journal admission is incomplete.");

    internal async ValueTask<bool> AdvanceAsync(
        Func<MirrorPulseBlockedLocalOperation, CancellationToken, ValueTask<bool>> recordBlocked,
        Func<MirrorPulseWorkerChangeCommand, CancellationToken, ValueTask<bool>> admit,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(recordBlocked); ArgumentNullException.ThrowIfNull(admit);
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Final journal admission has one scheduling owner.");
        try
        {
            if (_failed) throw new InvalidOperationException("A failed admission must restart from the official journal.");
            token.ThrowIfCancellationRequested();
            if (_result is not null) return true;
            int remaining = _budget;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                switch (_stage)
                {
                    case 0:
                        if (_next >= (_plan.BlockedOperations?.Count ?? 0)) { NextStage(); continue; }
                        if (remaining == 0) return false;
                        var blocked = _plan.BlockedOperations![_next];
                        if (await recordBlocked(blocked, token).ConfigureAwait(false))
                        {
                            if (blocked.Reason != MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange)
                                throw new InvalidDataException("Only unsupported metadata may settle during final bookkeeping.");
                            _acceptedMetadata.Add(blocked.OperationId);
                        }
                        break;
                    case 1:
                        if (_next >= _plan.RoutedBlockedCommands.Count) { NextStage(); continue; }
                        if (remaining == 0) return false;
                        var held = _plan.RoutedBlockedCommands[_next];
                        if (!_acceptedMetadata.Contains(held.OperationId)) _held.Add(held);
                        break;
                    case 2:
                        if (_next >= _plan.Commands.Count) { NextStage(); continue; }
                        if (remaining == 0) return false;
                        var command = _plan.Commands[_next];
                        if (command.Sequence <= _lastSequence || command.Sequence > UpperSequence)
                            throw new InvalidDataException("Final admission lost the original finite command order.");
                        if (await admit(command, token).ConfigureAwait(false) && !_held.HasEarlierDependency(command)) _ready.Add(command);
                        else { _held.Add(command); _deferred++; }
                        _lastSequence = command.Sequence;
                        break;
                    default:
                        // The existing immutable list is safe to share when no metadata
                        // was removed. Otherwise filter it in bounded projection turns.
                        if (_plan.CompleteWindow is null || _acceptedMetadata.Count == 0)
                            return Finish(_plan.CompleteWindow);
                        if (_next >= _plan.CompleteWindow.Count) return Finish(_complete.AsReadOnly());
                        if (remaining == 0) return false;
                        var observation = _plan.CompleteWindow[_next];
                        if (!_acceptedMetadata.Contains(observation.OperationId)) _complete.Add(observation);
                        break;
                }
                _next++; remaining--;
            }
        }
        catch { _failed = true; throw; }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private void NextStage() { _stage++; _next = 0; }

    private bool Finish(IReadOnlyList<MirrorPulseWorkerChangeCommand>? complete)
    {
        var ready = _ready.AsReadOnly();
        _result = new(ready, _deferred, false)
        { ValidatedSequenceCommands = ready, CompleteWindow = complete, WindowUpperSequence = UpperSequence };
        return true;
    }
}
