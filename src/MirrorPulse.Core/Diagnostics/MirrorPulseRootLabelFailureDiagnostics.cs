using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseRootLabelFailureBoundary
{
    Unknown, CurrentAuditFenced, LiveGuard, NativeIdentity, OriginalOwnership, CompletedPlacement, CurrentTree,
    PendingRemoteBatch, PendingTransition, UnavailableRoot
}

public enum MirrorPulseRootLabelPreparationStage { Unknown, RootWorkDrain, InstanceLibrary, OriginalOwnership, CurrentTreeAudit, PublicProof }

/// <summary>Fixed diagnostic classifications only; these values grant no namespace authority.</summary>
public static class MirrorPulseRootLabelFailureDiagnostics
{
    private static readonly object PreparationStageKey = new();

    public static void MarkPreparationStage(Exception failure, MirrorPulseRootLabelPreparationStage stage)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        failure.Data[PreparationStageKey] = stage;
    }

    public static MirrorPulseRootLabelPreparationStage PreparationStage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Data[PreparationStageKey] is MirrorPulseRootLabelPreparationStage stage && Enum.IsDefined(stage)
            ? stage : MirrorPulseRootLabelPreparationStage.Unknown;
    }

    public static async ValueTask<T> AtPreparationStageAsync<T>(MirrorPulseRootLabelPreparationStage stage, Func<ValueTask<T>> action)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(action);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            if (PreparationStage(original) == MirrorPulseRootLabelPreparationStage.Unknown) MarkPreparationStage(original, stage);
            throw;
        }
    }

    public static async ValueTask<T> RunPreparationAsync<T>(Guid rootId, Func<ValueTask<T>> action,
        Func<LogEntry, ValueTask> observe)
    {
        if (rootId == Guid.Empty) throw new ArgumentException("A stable root is required.", nameof(rootId));
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(observe);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try
            {
                await observe(new(LogLevel.Warning, "host", "RootLabelPreparationFailed", DateTimeOffset.UtcNow,
                    [new("rootId", rootId.ToString("D")), new("rootLabelFailureBoundary", Classify(original).ToString()),
                     new("rootLabelPreparationStage", PreparationStage(original).ToString()),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(original)),
                     new("hresult", original.HResult.ToString("X8", CultureInfo.InvariantCulture))])).ConfigureAwait(false);
            }
            catch (Exception loggingFailure) when (loggingFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }

    public static MirrorPulseRootLabelFailureBoundary Classify(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        // Exact product guard descriptions are an observation boundary. Arbitrary
        // library, Worker or user messages never become structured log values.
        return failure switch
        {
            InvalidOperationException { Message: "The original remote batch must converge before preparing a root rename." } => MirrorPulseRootLabelFailureBoundary.PendingRemoteBatch,
            InvalidOperationException
            {
                Message: "The managed root has a pending namespace transition." or
                "An existing recovery scope owns one of the selected roots."
            } => MirrorPulseRootLabelFailureBoundary.PendingTransition,
            InvalidOperationException
            {
                Message: "The managed root cannot be renamed in its current state." or
                "The root is not registered in this Host's namespace admission owner."
            } => MirrorPulseRootLabelFailureBoundary.UnavailableRoot,
            InvalidOperationException
            {
                Message: "New root preparation requires its current unfenced Host audit." or
                "Root notification acceptance requires the same complete unfenced audited inventory." or
                "Root readmission requires a complete unfenced audit under the live Host role."
            } => MirrorPulseRootLabelFailureBoundary.CurrentAuditFenced,
            InvalidOperationException
            {
                Message: "The current Host has no live startup guard." or
                "Root metadata admission lost its current Host guard or route." or
                "Every current member requires this Host's live role and original current-user owner."
            } => MirrorPulseRootLabelFailureBoundary.LiveGuard,
            InvalidDataException
            {
                Message: "Root preparation lost its live sync-root anchor." or
                "The current audit lost its live protected sync-root and ancestor guard."
            } => MirrorPulseRootLabelFailureBoundary.LiveGuard,
            InvalidDataException
            {
                Message: "Root metadata admission requires its current public native root identity." or
                "The current native root identity disagrees with the original item ID."
            } => MirrorPulseRootLabelFailureBoundary.NativeIdentity,
            InvalidOperationException { Message: "The original root notification already has immutable local-policy ownership." } => MirrorPulseRootLabelFailureBoundary.OriginalOwnership,
            InvalidDataException { Message: "The accepted root observation lost its original metadata owner." } => MirrorPulseRootLabelFailureBoundary.OriginalOwnership,
            InvalidOperationException
            {
                Message: "An original root item and the complete current-role protected inventory are required." or
                "Root journal policy requires its completed original protected Label and whole-subtree placement."
            } => MirrorPulseRootLabelFailureBoundary.CompletedPlacement,
            InvalidDataException
            {
                Message: "The current root placement lost its original Label move." or
                "The current root placement lost its complete atomic subtree receipt."
            } => MirrorPulseRootLabelFailureBoundary.CompletedPlacement,
            InvalidDataException
            {
                Message: "The current graph does not match the complete configured root layout." or
                "The actual sync-root contains an unexpected first-level entry." or
                "The actual first-level roots differ from the current graph." or
                "The complete actual local inventory differs from its current protected membership." or
                "The actual current tree lost a registered member."
            } => MirrorPulseRootLabelFailureBoundary.CurrentTree,
            _ => MirrorPulseRootLabelFailureBoundary.Unknown,
        };
    }
}
