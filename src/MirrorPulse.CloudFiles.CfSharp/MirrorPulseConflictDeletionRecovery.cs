using System.Runtime.Versioning;
using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Recovers bounded deletion decisions independently of ordinary journal retry.</summary>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseConflictDeletionRecovery(MirrorPulseProductCatalog catalog,
    Func<Guid, CancellationToken, ValueTask<MirrorPulseConflictResolution>> resume,
    MirrorPulseRootRouter router, Func<InstanceId, bool> mayDispatch, MirrorPulseJournalPumpRunner runner, LocalRollingLogWriter log)
{
    private Guid? _after;
    private Guid? _faultAfter;

    internal async ValueTask RepairAsync(CancellationToken token)
    {
        // A direct user command can finish while this reader is waiting. Heal its
        // retained fault without requiring another pending decision or original row.
        var faults = runner.ReadConflictDeletionRecoveryFaults(_faultAfter);
        foreach (Guid id in faults)
        {
            _faultAfter = id;
            try
            {
                var completed = await catalog.ReadConflictDeletionAsync(id, token).ConfigureAwait(false);
                if (completed?.Phase == MirrorPulseConflictDeletionPhase.Completed) ClearCompleted(completed);
            }
            catch (Exception error) when (IsDecisionFailure(error)) { }
        }
        if (faults.Count < 64) _faultAfter = null;
        var pending = await catalog.ReadPendingConflictDeletionsAsync(_after, token).ConfigureAwait(false);
        foreach (Guid id in pending)
        {
            token.ThrowIfCancellationRequested(); _after = id;
            try
            {
                var decision = await catalog.ReadConflictDeletionAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending deletion decision disappeared.");
                var root = router.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
                if (root.State != RootRegistrationState.Active || !mayDispatch(root.InstanceId)) continue;
                _ = await resume(id, token).ConfigureAwait(false);
                var completed = await catalog.ReadConflictDeletionAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The completed deletion decision disappeared.");
                ClearCompleted(completed);
            }
            catch (Exception error) when (IsDecisionFailure(error))
            {
                runner.RetainConflictDeletionRecoveryFault(id);
                try
                {
                    await log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "ConflictDeletionRecoveryFailed", DateTimeOffset.UtcNow,
                        [new("commandId", id.ToString("D")), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                         new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
            }
        }
        if (pending.Count < 64) _after = null;
    }

    private void ClearCompleted(MirrorPulseConflictDeletion completed)
    {
        if (completed.Phase != MirrorPulseConflictDeletionPhase.Completed) throw new InvalidDataException("Deletion recovery did not settle the original group.");
        runner.ClearConflictDeletionRecoveryFault(completed.Intent.CommandId);
        foreach (Guid original in completed.Intent.OriginalOperationIds) runner.ClearRecoveredFault(original);
        if (completed.Intent.PlanId is { } planId) runner.ClearCoalescingRecoveryFault(planId);
    }

    private static bool IsDecisionFailure(Exception error) =>
        error is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException or JsonException;
}
