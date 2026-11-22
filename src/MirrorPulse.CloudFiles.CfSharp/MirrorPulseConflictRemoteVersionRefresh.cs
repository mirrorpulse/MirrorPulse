using System.Runtime.Versioning;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Owns one explicit current-version selection, without authorizing an effect or acknowledging a journal ID.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictRemoteVersionRefresh(
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    Func<MirrorPulseWorkerStatRequest, CancellationToken, ValueTask<string?>> normalUserStat,
    Func<InstanceId, bool> mayDispatch, MirrorPulseNamespaceWorkAdmission? namespaceWork,
    Func<MirrorPulseCoalescedConflictRemoteObservation, CancellationToken, Task> publish)
{
    internal sealed class Admission(MirrorPulseCoalescedConflictRemoteTarget target, RootId rootId,
        MirrorPulseNamespaceWorkAdmission.WorkLease? lease) : IDisposable
    {
        internal MirrorPulseCoalescedConflictRemoteTarget Target { get; } = target;
        internal RootId RootId { get; } = rootId;
        public void Dispose() => Interlocked.Exchange(ref lease, null)?.Dispose();
    }

    internal async Task<MirrorPulseCoalescedConflictRemoteObservation?> ReadCompletedAsync(Guid commandId, Guid conflictId, CancellationToken token)
    {
        if (commandId == Guid.Empty || conflictId == Guid.Empty) throw new ArgumentException("The explicit command and selected conflict are required.");
        var retained = await catalog.ReadCoalescedConflictRemoteRefreshAsync(commandId, token).ConfigureAwait(false);
        if (retained is not null && retained.EntryConflictId != conflictId)
            throw new InvalidDataException("A completed refresh cannot target another selected epoch.");
        return retained;
    }

    internal async Task<Admission> RetainAsync(Guid conflictId, CancellationToken token)
    {
        var target = await catalog.ReadCoalescedConflictRemoteTargetAsync(conflictId, token).ConfigureAwait(false);
        var root = RequireCurrentRoot(target);
        return new(target, root.RootId, namespaceWork?.Enter([root.RootId]));
    }

    // The runtime retains this admission, its current library and instance scheduler.
    internal async ValueTask<MirrorPulseCoalescedConflictRemoteObservation?> ExecuteAdmittedAsync(
        Admission admission, Guid commandId, CancellationToken token)
    {
        var replay = await ReadCompletedAsync(commandId, admission.Target.ConflictId, token).ConfigureAwait(false);
        if (replay is not null) return replay;
        var current = await catalog.ReadCoalescedConflictRemoteTargetAsync(admission.Target.ConflictId, token).ConfigureAwait(false);
        if (current != admission.Target || RequireCurrentRoot(current).RootId != admission.RootId)
            throw new InvalidDataException("The admitted original group changed before its explicit version observation.");
        var command = await catalog.BeginCoalescedConflictRemoteRefreshAsync(commandId, current.ConflictId, token).ConfigureAwait(false);
        if (command.State == "resolved") return await ReadCompletedAsync(commandId, current.ConflictId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The completed refresh lost its original observation.");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string? revision = await normalUserStat(new(current.InstanceId, current.RemoteRelativePath, current.RootKey), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(revision)) return null;
        if (RequireCurrentRoot(current).RootId != admission.RootId)
            throw new InvalidDataException("The selected root changed during the separate version observation.");
        var observed = await catalog.RefreshCoalescedConflictRemoteObservationAsync(commandId, current.ConflictId, current.ItemId,
            current.LocalRelativePath, new(1, Guid.NewGuid(), current.RejectedEffect, revision, started, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        await publish(observed, token).ConfigureAwait(false); return observed;
    }

    private RootRegistration RequireCurrentRoot(MirrorPulseCoalescedConflictRemoteTarget target)
    {
        var route = router.ResolvePath(target.LocalRelativePath); var root = router.GetRegistration(target.InstanceId, target.RootKey);
        if (route.InstanceId != target.InstanceId || route.RootKey != target.RootKey || route.RelativePath != target.LocalRootRelativePath ||
            target.RejectedEffect.InstanceId != target.InstanceId || target.RejectedEffect.RootKey != target.RootKey)
            throw new InvalidDataException("A refresh cannot change the selected item, root or instance.");
        if (root.State != RootRegistrationState.Active || !mayDispatch(target.InstanceId) || namespaceWork is not null && !namespaceWork.IsAdmitted(root.RootId))
            throw new InvalidOperationException("The selected root must be enabled and freshly admitted before observing its source.");
        return root;
    }
}
