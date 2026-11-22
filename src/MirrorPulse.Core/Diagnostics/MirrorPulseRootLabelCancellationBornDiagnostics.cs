using System.Diagnostics;
using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

/// <summary>Captures fixed born-recovery timings without performing observer I/O in a protected callback.</summary>
public static class MirrorPulseRootLabelCancellationBornDiagnostics
{
    public static void Capture(string? milestone)
    {
        if (Enum.TryParse(milestone, out MirrorPulseCancellationBornStep step) && Enum.IsDefined(step) && milestone == step.ToString())
            MirrorPulseRootLabelCancellationProgressDiagnostics.CaptureBorn(step);
    }

    /// <summary>The caller flushes after leaving its namespace execution scope, including on failure.</summary>
    public static ValueTask FlushAsync() => MirrorPulseRootLabelCancellationProgressDiagnostics.FlushBornAsync();
}

internal enum MirrorPulseCancellationBornStep
{
    NamespaceBornRecoveryHistoryBegin, NamespaceBornRecoveryHistoryCompleted,
    NamespaceBornRecoveryContextBegin, NamespaceBornRecoveryContextHistoryCompleted,
    NamespaceBornRecoveryContextLineageBegin, NamespaceBornRecoveryContextLineageCompleted,
    NamespaceBornRecoveryContextParentBegin, NamespaceBornRecoveryContextParentCompleted,
    NamespaceBornRecoveryContextInheritanceBegin, NamespaceBornRecoveryContextInheritanceCompleted,
    NamespaceBornRecoveryContextCompleted, NamespaceBornRecoveryGuardsBegin, NamespaceBornRecoveryGuardsCompleted,
    NamespaceBornRecoveryActionBegin, NamespaceBornRecoveryActionCompleted, NamespaceBornRecoveryScopeBegin,
    NamespaceBornRecoveryAdmissionCompleted, NamespaceBornRecoveryCallbackBegin, NamespaceBornRecoveryFirstInspectionCompleted,
    NamespaceBornRecoveryCallbackWorkCompleted, NamespaceBornRecoveryScopeCompleted, NamespaceBornRecoveryReadbackBegin,
    NamespaceBornRecoveryReadbackCompleted, NamespaceBornRecoveryCommitBegin, NamespaceBornRecoveryCommitCompleted
}

internal static partial class MirrorPulseRootLabelCancellationProgressDiagnostics
{
    internal static void CaptureBorn(MirrorPulseCancellationBornStep step) => Current.Value?.CaptureBorn(step);
    internal static ValueTask FlushBornAsync() => Current.Value is { } scope ? scope.FlushBornAsync() : ValueTask.CompletedTask;

    internal sealed partial class Scope
    {
        private const int BornLimit = 128;
        private readonly object _bornGate = new();
        private readonly SemaphoreSlim _bornFlush = new(1, 1);
        private readonly List<BornObservation> _bornPending = [];
        private int _bornSequence;
        private bool _bornTruncated, _bornLimitReported, _bornDisabled, _bornClosed;

        private sealed record BornObservation(MirrorPulseCancellationBornStep Step, int Sequence, long Elapsed, DateTimeOffset At);

        internal void CaptureBorn(MirrorPulseCancellationBornStep step)
        {
            lock (_bornGate)
            {
                if (_bornClosed || _bornDisabled || Volatile.Read(ref _disabled) != 0) return;
                if (_bornSequence == BornLimit) { _bornTruncated = true; return; }
                _bornPending.Add(new(step, ++_bornSequence, (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds, DateTimeOffset.UtcNow));
            }
        }

        internal async ValueTask FlushBornAsync()
        {
            await _bornFlush.WaitAsync().ConfigureAwait(false);
            try
            {
                BornObservation[] pending; bool truncated;
                lock (_bornGate)
                {
                    if (_bornClosed || _bornDisabled || Volatile.Read(ref _disabled) != 0) return;
                    pending = _bornPending.ToArray(); _bornPending.Clear();
                    truncated = _bornTruncated && !_bornLimitReported;
                    if (truncated) _bornLimitReported = true;
                }
                try
                {
                    foreach (var item in pending)
                        await observe(new(LogLevel.Information, "host", "RootLabelCancellationBornProgress", item.At,
                            [new("operationId", operationId.ToString("D")), new("cancellationBornStep", item.Step.ToString()),
                             new("cancellationBornSequence", item.Sequence.ToString(CultureInfo.InvariantCulture)),
                             new("cancellationBornElapsedMs", item.Elapsed.ToString(CultureInfo.InvariantCulture))])).ConfigureAwait(false);
                    if (truncated)
                        await observe(new(LogLevel.Information, "host", "RootLabelCancellationBornProgressLimit", DateTimeOffset.UtcNow,
                            [new("operationId", operationId.ToString("D")), new("cancellationBornSequence", (BornLimit + 1).ToString(CultureInfo.InvariantCulture)),
                             new("cancellationBornElapsedMs", ((long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                             new("cancellationBornTruncated", "true")])).ConfigureAwait(false);
                }
                catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException)
                {
                    lock (_bornGate) { _bornDisabled = true; _bornPending.Clear(); }
                }
            }
            finally { _bornFlush.Release(); }
        }

        private void CloseBornDetails()
        {
            lock (_bornGate) { _bornClosed = true; _bornPending.Clear(); }
        }
    }
}
