using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseConflictCommandStage
{
    Unknown, Admission, RemoteSource, LocalContent, Acceptance, CopyRetention, OriginalJournal,
    FileStart, RoleRecovery, ContentWrite, NativeConfirmation, AccessRestoration, Acknowledgement
}

public enum MirrorPulseConflictCommandBoundary
{
    Unknown, MissingMember, MissingProtection, CurrentRole, DirectParent, NativeObject, ResidentIdentity, StartRelations
}

/// <summary>Retains closed command failure stages without recording user or exception text.</summary>
public static class MirrorPulseConflictCommandDiagnostics
{
    private static readonly object StageKey = new();

    public static MirrorPulseConflictCommandBoundary Boundary(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is not InvalidDataException) return MirrorPulseConflictCommandBoundary.Unknown;
        return failure.Message switch
        {
            "KeepRemote requires the original registered file." => MirrorPulseConflictCommandBoundary.MissingMember,
            "KeepRemote requires the original file protection." => MirrorPulseConflictCommandBoundary.MissingProtection,
            "KeepRemote requires the original protected file and direct parent in its active role." => MirrorPulseConflictCommandBoundary.CurrentRole,
            "KeepRemote lost the original direct parent." => MirrorPulseConflictCommandBoundary.DirectParent,
            "KeepRemote requires the original protected file and direct parent before any write." => MirrorPulseConflictCommandBoundary.NativeObject,
            "KeepRemote requires the exact original resident placeholder without source recall." => MirrorPulseConflictCommandBoundary.ResidentIdentity,
            "A file start requires the complete original UpdateFile decision, copies, journal, object, identity and direct parent." => MirrorPulseConflictCommandBoundary.StartRelations,
            _ => MirrorPulseConflictCommandBoundary.Unknown
        };
    }

    public static MirrorPulseConflictCommandStage Stage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            return failure.Data[StageKey] is MirrorPulseConflictCommandStage stage && Enum.IsDefined(stage)
                ? stage : MirrorPulseConflictCommandStage.Unknown;
        }
        catch (Exception diagnosticFailure) when (diagnosticFailure is not StackOverflowException and not OutOfMemoryException)
        { return MirrorPulseConflictCommandStage.Unknown; }
    }

    public static async Task<T> AtStageAsync<T>(MirrorPulseConflictCommandStage stage, Func<Task<T>> action)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(action);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try { if (Stage(original) == MirrorPulseConflictCommandStage.Unknown) original.Data[StageKey] = stage; }
            catch (Exception diagnosticFailure) when (diagnosticFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }

    public static async Task AtStageAsync(MirrorPulseConflictCommandStage stage, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = await AtStageAsync(stage, async () => { await action().ConfigureAwait(false); return true; }).ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(Guid conflictId, Func<Task<T>> action, Func<LogEntry, ValueTask> observe)
    {
        if (conflictId == Guid.Empty) throw new ArgumentException("An original conflict identity is required.", nameof(conflictId));
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(observe);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try
            {
                await observe(new(LogLevel.Warning, "host", "ConflictCommandFailed", DateTimeOffset.UtcNow,
                    [new("operationId", conflictId.ToString("D")), new("conflictCommandStage", Stage(original).ToString()),
                     new("conflictCommandBoundary", Boundary(original).ToString()),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(original)),
                     new("journalReadFailureKind", MirrorPulseJournalReadDiagnostics.Kind(original)),
                     new("hresult", original.HResult.ToString("X8", CultureInfo.InvariantCulture))])).ConfigureAwait(false);
            }
            catch (Exception loggingFailure) when (loggingFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }
}
