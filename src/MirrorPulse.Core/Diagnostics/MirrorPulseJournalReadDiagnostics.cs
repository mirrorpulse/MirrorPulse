using System.ComponentModel;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseJournalReadStage
{
    Unknown, RootLabelRecovery, ConflictDeletionRecovery, LocalRemovalRecovery, KeepBothRecovery,
    CoalescingRecovery, MutationProjectionRecovery, RescanDecision, JournalSource, FullRescan,
    RetiredSourceRecovery, FeedHead, FeedWindow, FeedLossCheck, PendingRootRead, JournalClassification,
    JournalAdmission, RootWorkDrain, RootNativeAudit, RootAcknowledgement, RootReadmissionAudit, ConflictRemoteObservationRecovery
}

/// <summary>Retains fixed read-failure observations without changing an operation, exception or retry.</summary>
public static class MirrorPulseJournalReadDiagnostics
{
    private static readonly object StageKey = new();

    public static MirrorPulseJournalReadStage Stage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            return failure.Data[StageKey] is MirrorPulseJournalReadStage stage && Enum.IsDefined(stage)
                ? stage : MirrorPulseJournalReadStage.Unknown;
        }
        catch (Exception diagnosticFailure) when (diagnosticFailure is not StackOverflowException and not OutOfMemoryException)
        { return MirrorPulseJournalReadStage.Unknown; }
    }

    public static async ValueTask<T> AtStageAsync<T>(MirrorPulseJournalReadStage stage, Func<ValueTask<T>> action)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(action);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try { if (Stage(original) == MirrorPulseJournalReadStage.Unknown) original.Data[StageKey] = stage; }
            catch (Exception diagnosticFailure) when (diagnosticFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }

    public static async ValueTask AtStageAsync(MirrorPulseJournalReadStage stage, Func<ValueTask> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = await AtStageAsync(stage, async () => { await action().ConfigureAwait(false); return true; }).ConfigureAwait(false);
    }

    public static string Kind(Exception failure) => failure switch
    {
        Win32Exception => "Native",
        ObjectDisposedException => "Disposed",
        InvalidDataException => "InvalidData",
        InvalidOperationException => "InvalidOperation",
        UnauthorizedAccessException => "Authorization",
        OperationCanceledException => "Cancelled",
        IOException => "IO",
        ArgumentException => "Argument",
        _ => "Other"
    };
}
