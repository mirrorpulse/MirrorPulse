using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseRootLabelReadmissionAttempt(RootId RootId, Exception? Failure);

/// <summary>Accepts local root Label metadata only after a fresh current-role complete native audit.</summary>
/// <remarks>Only the ordinary pump owns this capability. Controlled transfers may already
/// own root work and must use the separate original-only metadata recovery service.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseRootLabelJournalAdmission
{
    private readonly CloudFileSystem _fileSystem;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseNamespaceExecutionSession _execution;
    private readonly MirrorPulseNamespaceActor _actor;
    private readonly MirrorPulseNamespaceWorkAdmission _work;
    private readonly MirrorPulseRootLabelJournalRecovery _recovery;
    private readonly MirrorPulseNamespaceCurrentTreeAuditCoordinator _audit;
    private readonly Func<MirrorPulseNamespaceAnchorProtection?> _anchor;
    private readonly HashSet<RootId> _closed = [];
    private readonly HashSet<RootId> _queued = [];
    private readonly Queue<RootId> _retry = new();
    private readonly Queue<(Guid OperationId, Exception Failure)> _failures = new();

    internal MirrorPulseRootLabelJournalAdmission(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
        MirrorPulseNamespacePermissionCoordinator permissions, MirrorPulseNamespaceWorkAdmission work,
        MirrorPulseRootLabelJournalRecovery recovery, Func<MirrorPulseNamespaceAnchorProtection?> anchor)
    {
        _fileSystem = fileSystem; _catalog = catalog; _router = router; _execution = execution;
        _actor = actor; _work = work; _recovery = recovery; _anchor = anchor;
        _audit = new(fileSystem, catalog, router, execution, actor, permissions);
    }

    internal bool HasPendingReadmission => _closed.Count != 0;
    internal Func<CancellationToken, ValueTask>? AfterAcknowledgement { get; init; }

    internal async ValueTask RecordDeferredAsync(MirrorPulseLocalChangeObservation observation, Exception failure, CancellationToken token)
    {
        var root = observation.ItemId is { } id ? _router.FindRootByPublicItemId(id) : null;
        await _catalog.SaveBlockedLocalOperationAsync(new(observation.OperationId, root?.InstanceId,
            observation.RelativePath, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired, observation.ObservedAt), token).ConfigureAwait(false);
        // Diagnostic retention is bounded independently of immutable operation ownership.
        if (_failures.Count < 64) _failures.Enqueue((observation.OperationId, failure));
    }

    internal IReadOnlyList<(Guid OperationId, Exception Failure)> TakeFailures()
    {
        var failures = _failures.ToArray(); _failures.Clear(); return failures;
    }

    internal async ValueTask<bool> TryAcceptAsync(MirrorPulseLocalChangeObservation observation, CancellationToken token)
    {
        // Original-ID replay has already run. Null identity, shape or paths cannot
        // select a configured owner or acquire new native authority.
        if (observation.ItemId is not { } itemId || itemId == Guid.Empty || observation.OperationId == Guid.Empty ||
            observation.Sequence <= 0 || observation.ObservedAt == default || observation.Kind != CloudLocalChangeKind.Move ||
            !observation.IsDirectory || !IsRootName(observation.RelativePath) || !IsRootName(observation.PreviousRelativePath) ||
            _router.FindRootByPublicItemId(itemId) is not { } root || _anchor() is not { } anchor)
            return false;

        // Drain outside the shared actor and outside namespace impersonation. A
        // cancelled waiter leaves the original journal row untouched for retry.
        await using var drained = await _work.BeginRecoveryAsync([root.RootId], token).ConfigureAwait(false);
        _closed.Add(root.RootId);
        if (_queued.Add(root.RootId)) _retry.Enqueue(root.RootId);
        await _audit.AuditRetainedRootAsync(await ReadAnchorMemberAsync(anchor, token).ConfigureAwait(false), anchor, root.RootId,
            async (audit, stop) =>
            {
                _actor.AssertAccess();
                if (_anchor() != anchor || _router.FindRootByPublicItemId(itemId) != root)
                    throw new InvalidOperationException("Root metadata admission lost its current Host guard or route.");
                var before = await anchor.InspectAsync(stop).ConfigureAwait(false);
                var native = await _fileSystem.GetDirectory(root.DirectoryName).InspectAsync(stop).ConfigureAwait(false);
                if (!native.Exists || native.IsTombstone || native.Kind != CloudItemKind.Directory || !native.IsPlaceholder ||
                    native.ItemId != itemId || native.LocalBinding is not { } binding || native.PlaceholderIdentity.IsEmpty)
                    throw new InvalidDataException("Root metadata admission requires its current public native root identity.");
                var publicIdentity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
                if (publicIdentity.ItemId != itemId)
                    throw new InvalidDataException("The current native root identity disagrees with the original item ID.");
                var identity = new MirrorPulseRootLabelJournalNativeIdentity(itemId,
                    new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId),
                    Convert.ToBase64String(native.PlaceholderIdentity.Span), root.DirectoryName, DateTimeOffset.UtcNow);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await anchor.InspectAsync(stop).ConfigureAwait(false));
                _ = await _catalog.StartRootLabelJournalAcknowledgementAsync(audit.Inventory, _execution.RoleSid.Value, audit.ObservedAt,
                    identity, new(observation.OperationId, observation.Sequence, MirrorPulseWorkerChangeKind.Move, observation.ItemId,
                        observation.RelativePath, observation.PreviousRelativePath, true, observation.ObservedAt), DateTimeOffset.UtcNow, stop).ConfigureAwait(false);
            }, token).ConfigureAwait(false);

        // The native/permission audit scope has expired. Only the actual normal-owner
        // public SDK return can finish this immutable first start.
        if (!await _recovery.TryReplayOwnedAsync(observation.OperationId, token).ConfigureAwait(false))
            throw new InvalidDataException("The accepted root observation lost its original metadata owner.");
        if (AfterAcknowledgement is not null) await AfterAcknowledgement(token).ConfigureAwait(false);
        await ReadmitAsync(root.RootId, drained, token).ConfigureAwait(false);
        _closed.Remove(root.RootId);
        return true;
    }

    internal async ValueTask<IReadOnlyList<MirrorPulseRootLabelReadmissionAttempt>> RecoverClosedAsync(CancellationToken token)
    {
        var results = new List<MirrorPulseRootLabelReadmissionAttempt>();
        // Each closed root has one queue entry. A failed root goes to the tail and
        // cannot monopolize a turn; durable pending metadata remains authoritative.
        int count = Math.Min(4, _retry.Count);
        for (int index = 0; index < count; index++)
        {
            RootId id = _retry.Dequeue();
            _queued.Remove(id);
            if (!_closed.Contains(id)) continue;
            if (_router.FindRegistration(id) is null) { _closed.Remove(id); continue; }
            try
            {
                await using var drained = await _work.BeginRecoveryAsync([id], token).ConfigureAwait(false);
                await ReadmitAsync(id, drained, token).ConfigureAwait(false);
                _closed.Remove(id); results.Add(new(id, null));
            }
            catch (Exception failure) when (!token.IsCancellationRequested && failure is
                (IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException))
            { results.Add(new(id, failure)); }
            finally { if (_closed.Contains(id) && _queued.Add(id)) _retry.Enqueue(id); }
        }
        return results.AsReadOnly();
    }

    private async Task ReadmitAsync(RootId id, MirrorPulseNamespaceWorkAdmission.RecoveryLease drained, CancellationToken token)
    {
        var anchor = _anchor() ?? throw new InvalidOperationException("The current Host has no live startup guard.");
        await _audit.AuditRetainedRootAsync(await ReadAnchorMemberAsync(anchor, token).ConfigureAwait(false), anchor, id,
            (audit, _) =>
            {
                _actor.AssertAccess();
                if (_anchor() != anchor || audit.Inventory.FencedRoots.Count != 0 ||
                    audit.RoleSid != _execution.RoleSid.Value || _router.FindRegistration(id) is not { } current)
                    throw new InvalidOperationException("Root readmission requires a complete unfenced audit under the live Host role.");
                if (current.State == RootRegistrationState.Active) drained.CompleteAfterFreshAudit();
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
    }

    private async Task<Guid> ReadAnchorMemberAsync(MirrorPulseNamespaceAnchorProtection anchor, CancellationToken token) =>
        (await _catalog.ReadNamespaceMemberByBindingAsync(anchor.Original.LocalObject, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The live sync-root guard lost its original registered anchor.")).MemberId;

    private static bool IsRootName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        name == name.Trim() && !name.Contains('/') && !name.Contains('\\');
}
