using System.Diagnostics;
using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

internal static class MirrorPulseRootLabelExecutionProgressDiagnostics
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal static Scope Enter(Guid operationId, Func<LogEntry, ValueTask> observe)
    {
        var scope = new Scope(operationId, observe, Current.Value);
        Current.Value = scope;
        return scope;
    }

    internal static ValueTask ReportAsync(MirrorPulseRootLabelExecutionStage stage, string phase) =>
        Current.Value is { } scope ? scope.ReportAsync(stage, phase) : ValueTask.CompletedTask;

    internal sealed class Scope(Guid operationId, Func<LogEntry, ValueTask> observe, Scope? previous) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _sequence;
        private int _disabled;

        internal async ValueTask ReportAsync(MirrorPulseRootLabelExecutionStage stage, string phase)
        {
            if (Volatile.Read(ref _disabled) != 0 || Volatile.Read(ref _sequence) >= 32) return;
            int sequence = Interlocked.Increment(ref _sequence);
            if (sequence > 32) return;
            try
            {
                await observe(new(LogLevel.Information, "host", "RootLabelExecutionProgress", DateTimeOffset.UtcNow,
                    [new("operationId", operationId.ToString("D")), new("rootLabelExecutionStage", stage.ToString()),
                     new("executionProgressPhase", phase), new("executionSequence", sequence.ToString(CultureInfo.InvariantCulture)),
                     new("executionElapsedMs", ((long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds).ToString(CultureInfo.InvariantCulture))]))
                    .ConfigureAwait(false);
            }
            catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException)
            {
                Interlocked.Exchange(ref _disabled, 1);
            }
        }

        public void Dispose() => Current.Value = previous;
    }
}
