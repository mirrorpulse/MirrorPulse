using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseRootLabelExecutionStage { Unknown, RootWorkDrain, InstanceLibrary, SubtreePreparation, OriginalExecution, CurrentTreeAudit }

/// <summary>Observes fixed root execution boundaries without changing the original operation or exception.</summary>
public static class MirrorPulseRootLabelExecutionFailureDiagnostics
{
    private static readonly object StageKey = new();

    public static void MarkStage(Exception failure, MirrorPulseRootLabelExecutionStage stage)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        failure.Data[StageKey] = stage;
    }

    public static MirrorPulseRootLabelExecutionStage Stage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Data[StageKey] is MirrorPulseRootLabelExecutionStage stage && Enum.IsDefined(stage)
            ? stage : MirrorPulseRootLabelExecutionStage.Unknown;
    }

    public static async ValueTask<T> AtStageAsync<T>(MirrorPulseRootLabelExecutionStage stage, Func<ValueTask<T>> action)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(action);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            if (Stage(original) == MirrorPulseRootLabelExecutionStage.Unknown) MarkStage(original, stage);
            throw;
        }
    }

    public static async ValueTask<T> RunAsync<T>(Guid operationId, Func<ValueTask<T>> action, Func<LogEntry, ValueTask> observe)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(observe);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try
            {
                await observe(new(LogLevel.Warning, "host", "RootLabelExecutionFailed", DateTimeOffset.UtcNow,
                    [new("operationId", operationId.ToString("D")),
                     new("rootLabelFailureBoundary", MirrorPulseRootLabelFailureDiagnostics.Classify(original).ToString()),
                     new("rootLabelExecutionStage", Stage(original).ToString()),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(original)),
                     new("hresult", original.HResult.ToString("X8", CultureInfo.InvariantCulture))])).ConfigureAwait(false);
            }
            catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }
}
