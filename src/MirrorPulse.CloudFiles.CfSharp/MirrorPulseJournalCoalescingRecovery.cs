using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Repairs one bounded page without letting a retained group block its peers.</summary>
internal sealed class MirrorPulseJournalCoalescingRecovery(MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, MirrorPulseJournalPumpRunner runner, LocalRollingLogWriter log)
{
    private Guid? _after;

    internal async ValueTask RepairAsync(CancellationToken token)
    {
        var groups = await catalog.ReadPendingJournalCoalescingRetryGroupsAsync(_after, token).ConfigureAwait(false);
        foreach (Guid id in groups)
        {
            token.ThrowIfCancellationRequested();
            _after = id;
            MirrorPulseJournalCoalescingPlan plan;
            try { plan = await catalog.ReadJournalCoalescingPlanAsync(id, token).ConfigureAwait(false); }
            catch (Exception error) when (IsGroupFailure(error))
            {
                await RetainFailureAsync(id, error, token).ConfigureAwait(false);
                continue;
            }

            // Failure to read the shared official store remains a source failure.
            // Missing originals alone never prove that an effect was accepted.
            bool pending = false;
            await using (var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                foreach (var member in plan.Members)
                    if (await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false) is not null)
                    { pending = true; break; }
                await transaction.RollbackAsync(token).ConfigureAwait(false);
            }
            if (pending) continue;
            try
            {
                await catalog.CompleteJournalCoalescingRetryCommandsAsync(id, token).ConfigureAwait(false);
                runner.ClearCoalescingRecoveryFault(id);
            }
            catch (Exception error) when (IsGroupFailure(error))
            {
                await RetainFailureAsync(id, error, token).ConfigureAwait(false);
            }
        }
        if (groups.Count < 64) _after = null;
    }

    private static bool IsGroupFailure(Exception error) =>
        error is InvalidDataException or InvalidOperationException or MirrorPulseMutationAmbiguousException or FileNotFoundException or JsonException;

    private async ValueTask RetainFailureAsync(Guid id, Exception error, CancellationToken token)
    {
        runner.RetainCoalescingRecoveryFault(id);
        try
        {
            await log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "JournalGroupRecoveryFailed", DateTimeOffset.UtcNow,
                [new("planId", id.ToString("D")), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                 new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
        }
        catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
    }
}
