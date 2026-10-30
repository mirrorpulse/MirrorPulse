using System.Runtime.Versioning;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Observes missing versions without rewriting a rejected request or authorizing its effects.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictRemoteObservationRecovery(
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, IMirrorPulseWorkerStatTransport stats,
    Func<InstanceId, bool> mayDispatch, MirrorPulseInstanceScheduler scheduler, LocalRollingLogWriter log,
    MirrorPulseConflictCenter? center = null, MirrorPulseConflictNotificationBridge? notifications = null,
    MirrorPulseNamespaceWorkAdmission? namespaceWork = null, MirrorPulseNamespaceExecutionSession? execution = null)
{
    private string? _cursor;

    internal async Task RepairAsync(CancellationToken token)
    {
        var page = await catalog.ReadPendingConflictPageAsync(64, _cursor, token).ConfigureAwait(false);
        _cursor = page.NextCursor;
        var groups = new HashSet<Guid>();
        foreach (var conflict in page.Items)
        {
            if (conflict.Source != MirrorPulseConflictSource.Upload || conflict.RemoteRevision is not null ||
                conflict.Reason != MirrorPulseConflictReason.StaleRemoteRevision || !Guid.TryParseExact(conflict.ChangeId, "D", out Guid original)) continue;
            try
            {
                var group = await catalog.ReadJournalCoalescingOwnerAsync(original, token).ConfigureAwait(false);
                if (group is null || !groups.Add(group.PlanId)) continue;
                var root = router.GetRegistration(group.InstanceId, group.RootKey);
                if (!MayObserve(root)) continue;
                MirrorPulseNamespaceWorkAdmission.WorkLease? lease = null;
                if (namespaceWork is not null && !namespaceWork.TryEnter([root.RootId], out lease)) continue;
                using (lease)
                    await scheduler.RunAsync(root.InstanceId, async stop =>
                    {
                        return await ObserveAdmittedAsync(conflict.ConflictId, root.RootId, stop).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsObservationFailure(error, token))
            {
                await LogFailureAsync(conflict.ConflictId, error, token).ConfigureAwait(false);
            }
        }
    }

    // The upload pump already owns the instance scheduler and root admission.
    internal async Task<MirrorPulseCoalescedConflictRemoteObservation?> TryObserveAdmittedAsync(
        Guid conflictId, RootId admittedRoot, CancellationToken token)
    {
        try { return await ObserveAdmittedAsync(conflictId, admittedRoot, token).ConfigureAwait(false); }
        catch (Exception error) when (IsObservationFailure(error, token))
        {
            await LogFailureAsync(conflictId, error, token).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<MirrorPulseCoalescedConflictRemoteObservation?> ObserveAdmittedAsync(
        Guid conflictId, RootId admittedRoot, CancellationToken token)
    {
        var conflict = await catalog.ReadUploadConflictAsync(conflictId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The version observation lost its current conflict.");
        if (!conflict.IsPending || conflict.RemoteRevision is not null || conflict.Reason != MirrorPulseConflictReason.StaleRemoteRevision) return null;
        var target = await catalog.ReadCoalescedConflictRemoteTargetAsync(conflictId, token).ConfigureAwait(false);
        var root = RequireRoot(target, admittedRoot);
        if (!MayObserve(root)) return null;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var command = new MirrorPulseWorkerStatRequest(target.InstanceId, target.RemoteRelativePath, target.RootKey);
        string? revision = execution is null ? await stats.StatAsync(command, token).ConfigureAwait(false) :
            await execution.RunNormalUserOperationAsync(async () => await stats.StatAsync(command, token).ConfigureAwait(false)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(revision)) return null;
        root = RequireRoot(target, admittedRoot);
        if (!MayObserve(root)) return null;
        var observed = await catalog.PrepareCoalescedConflictRemoteObservationAsync(conflictId, target.ItemId, target.LocalRelativePath,
            new(1, Guid.NewGuid(), target.RejectedEffect, revision, started, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        await PublishAsync(observed, token).ConfigureAwait(false);
        return observed;
    }

    internal Task PublishAsync(MirrorPulseCoalescedConflictRemoteObservation observed, CancellationToken token) =>
        MirrorPulseConflictRemoteObservationProjection.PublishAsync(catalog, observed, center, notifications, token);

    private RootRegistration RequireRoot(MirrorPulseCoalescedConflictRemoteTarget target, RootId admittedRoot)
    {
        var route = router.ResolvePath(target.LocalRelativePath);
        var root = router.GetRegistration(target.InstanceId, target.RootKey);
        if (route.InstanceId != target.InstanceId || route.RootKey != target.RootKey || root.RootId != admittedRoot || route.RelativePath != target.LocalRootRelativePath ||
            target.RejectedEffect.InstanceId != target.InstanceId || target.RejectedEffect.RootKey != target.RootKey)
            throw new InvalidDataException("A version observation cannot change its admitted instance or root.");
        return root;
    }

    private bool MayObserve(RootRegistration root) => root.State == RootRegistrationState.Active && mayDispatch(root.InstanceId) &&
        (namespaceWork is null || namespaceWork.IsAdmitted(root.RootId));

    private async Task LogFailureAsync(Guid conflictId, Exception error, CancellationToken token)
    {
        try
        {
            await log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "ConflictRemoteObservationFailed", DateTimeOffset.UtcNow,
                [new("conflictId", conflictId.ToString("D")), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                 new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { }
    }

    private static bool IsObservationFailure(Exception error, CancellationToken token) =>
        error is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException or JsonException or TimeoutException ||
        error is OperationCanceledException && !token.IsCancellationRequested;
}
