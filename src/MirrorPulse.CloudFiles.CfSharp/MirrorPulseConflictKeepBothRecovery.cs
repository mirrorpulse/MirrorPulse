using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Rotates bounded retained preservation decisions before ordinary journal dispatch.</summary>
internal sealed class MirrorPulseConflictKeepBothRecovery(MirrorPulseProductCatalog catalog,
    Func<Guid, CancellationToken, ValueTask<MirrorPulseConflictResolution>> resume, MirrorPulseRootRouter router,
    Func<InstanceId, bool> mayDispatch, MirrorPulseJournalPumpRunner runner, LocalRollingLogWriter log,
    MirrorPulseNamespaceWorkAdmission? namespaceWork = null)
{
    private Guid? _after;
    private Guid? _faultAfter;

    internal async ValueTask RepairAsync(CancellationToken token)
    {
        var faults = runner.ReadKeepBothRecoveryFaults(_faultAfter);
        foreach (Guid id in faults)
        {
            _faultAfter = id;
            try { await ClearCompletedAsync(id, token).ConfigureAwait(false); }
            catch (Exception error) when (IsDecisionFailure(error)) { }
        }
        if (faults.Count < 64) _faultAfter = null;
        var pending = await catalog.ReadPendingConflictKeepBothAsync(_after, token).ConfigureAwait(false);
        foreach (Guid id in pending)
        {
            token.ThrowIfCancellationRequested(); _after = id;
            try
            {
                var decision = await catalog.ReadConflictKeepBothAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original pending preservation decision disappeared.");
                var root = router.GetRegistration(decision.Intent.InstanceId, decision.Intent.RemoteSource.RootKey);
                if (decision.OriginalRetriesPrepared) { runner.ClearKeepBothRecoveryFault(id); continue; }
                if (root.State != RootRegistrationState.Active || !mayDispatch(root.InstanceId) ||
                    namespaceWork is not null && !namespaceWork.IsAdmitted(root.RootId)) continue;
                _ = await resume(id, token).ConfigureAwait(false);
                var resumed = await catalog.ReadConflictKeepBothAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original preservation decision disappeared after execution.");
                if (!resumed.OriginalRetriesPrepared)
                    throw new InvalidDataException("Preservation recovery did not reach its durable original retry boundary.");
                // A successful resume may only have authorized a queued retry. Clear
                // this decision's read/copy fault; actual effect faults remain owned by the pump.
                runner.ClearKeepBothRecoveryFault(id);
                await ClearCompletedAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsDecisionFailure(error))
            {
                runner.RetainKeepBothRecoveryFault(id);
                try
                {
                    await log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "ConflictKeepBothRecoveryFailed", DateTimeOffset.UtcNow,
                        [new("commandId", id.ToString("D")), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                         new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
            }
        }
        if (pending.Count < 64) _after = null;
    }

    private async Task ClearCompletedAsync(Guid id, CancellationToken token)
    {
        var command = await catalog.ReadUserCommandAsync(id, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The preservation command disappeared during recovery.");
        if (command.State is not ("resolved" or "superseded")) return;
        var decision = await catalog.ReadConflictKeepBothAsync(id, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The completed preservation decision is missing.");
        if (!decision.RetryAuthorized) throw new InvalidDataException("The original preservation command never authorized its retry.");
        runner.ClearKeepBothRecoveryFault(id);
        if (command.State != "resolved") return;
        foreach (Guid original in decision.Intent.OriginalOperationIds) runner.ClearRecoveredFault(original);
        runner.ClearCoalescingRecoveryFault(decision.Intent.PlanId);
    }

    private static bool IsDecisionFailure(Exception error) =>
        error is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException or JsonException;
}
