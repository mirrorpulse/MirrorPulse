using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Separate product permission verification and library native/projection facts.</summary>
/// <remarks>
/// A verified permission is a historical object fact, not tree readiness or synchronization
/// acceptance. A missing permission result means the callback did not finish its product work.
/// A missing receipt is possible only when retained recovery already forbids native admission.
/// Callers must retain both facts and independently audit the whole tree before enabling it.
/// </remarks>
public sealed record MirrorPulseProtectedNamespacePermissionResult(MirrorPulseNamespacePermissionResult? Permission,
    CloudProtectedLocalOperationResult? Receipt);

internal sealed record MirrorPulseProtectedLocalIdentityObservation(Guid? ItemId, string? RemoteId,
    bool IsPlaceholder, CloudPlaceholderIdentity? PlaceholderIdentity);

/// <summary>Adapts only public same-object CfSharp work; it owns no additional native handle.</summary>
internal interface IMirrorPulseProtectedNamespacePermissionLease : IMirrorPulseNamespacePermissionLease
{
    ValueTask<MirrorPulseProtectedLocalIdentityObservation> InspectLocalIdentityAsync(CancellationToken cancellationToken);
    ValueTask PrepareLocalIdentityAsync(CloudPlaceholderIdentity identity, CancellationToken cancellationToken);
}

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    /// <summary>Validates and prepares the original file before applying its immutable permission intent.</summary>
    /// <remarks>
    /// Product admission and durable capture checks precede native scope acquisition. Originals
    /// and known official identity are retained before conversion; unknown identity is supplied by
    /// the Host and selected once. That candidate must carry no accepted remote revision. Existing
    /// placeholders use Access-only work and keep their accepted identity. No Worker, provider,
    /// source access or user interaction belongs in this operation. The scope drains before return.
    /// </remarks>
    public Task<MirrorPulseProtectedNamespacePermissionResult> ApplyProtectedAsync(Guid operationId, CloudItem item,
        MirrorPulseRootRouter router, CloudPlaceholderIdentity? initialLocalIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        if (initialLocalIdentity?.RemoteRevision is not null)
            throw new ArgumentException("Local permission preparation cannot introduce a remote acceptance revision.", nameof(initialLocalIdentity));
        if (item.Kind == CloudItemKind.Directory && initialLocalIdentity is not null)
            throw new ArgumentException("Directory permission work does not prepare a file identity.", nameof(initialLocalIdentity));
        return RunAdmittedPermissionWorkAsync(operationId,
            (change, baseline, stop) => ApplyProtectedAdmittedAsync(change, baseline, item, router, initialLocalIdentity, stop),
            cancellationToken);
    }

    private async Task<MirrorPulseProtectedNamespacePermissionResult> ApplyProtectedAdmittedAsync(
        MirrorPulseNamespacePermissionChange change, MirrorPulseNamespacePermissionBaseline baseline, CloudItem item,
        MirrorPulseRootRouter router, CloudPlaceholderIdentity? initialLocalIdentity, CancellationToken stop)
    {
        if (initialLocalIdentity?.RemoteRevision is not null)
            throw new ArgumentException("Local permission preparation cannot introduce a remote acceptance revision.", nameof(initialLocalIdentity));
        if (item.Kind == CloudItemKind.Directory && initialLocalIdentity is not null)
            throw new ArgumentException("Directory permission work does not prepare a file identity.", nameof(initialLocalIdentity));
        if (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
            return new MirrorPulseProtectedNamespacePermissionResult(
                new(change.Intent.OperationId, MirrorPulseNamespacePermissionOutcome.RecoveryRequired, change.RecoveryReason), null);
        MirrorPulseNamespacePermissionResult? permission = null;
        CloudProtectedLocalOperationResult receipt = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(
            item, router, baseline.LocalObject, async (lease, token) =>
            {
                permission = await ApplyWithinProtectionAsync(change, baseline, lease, initialLocalIdentity, token).ConfigureAwait(false);
            }, stop).ConfigureAwait(false);
        if (receipt.Outcome == CloudProtectedLocalOperationOutcome.LocalObjectMismatch)
            permission = await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged).ConfigureAwait(false);
        return new(permission, receipt);
    }

    /// <summary>Runs only inside admitted product work and one library-owned protected callback.</summary>
    internal async Task<MirrorPulseNamespacePermissionResult> ApplyWithinProtectionAsync(
        MirrorPulseNamespacePermissionChange change, MirrorPulseNamespacePermissionBaseline baseline,
        IMirrorPulseProtectedNamespacePermissionLease lease, CloudPlaceholderIdentity? initialLocalIdentity,
        CancellationToken cancellationToken)
    {
        if (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
            return new(change.Intent.OperationId, MirrorPulseNamespacePermissionOutcome.RecoveryRequired, change.RecoveryReason);
        MirrorPulseNamespacePermissionObject observed = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        MirrorPulseNamespacePermissionRecoveryReason? mismatch = MatchApplicationObservation(baseline, change, observed);
        if (mismatch is not null) return await FenceAsync(change, mismatch.Value).ConfigureAwait(false);
        var identityFailure = await PrepareRetainedLocalIdentityAsync(change, baseline, lease, initialLocalIdentity, cancellationToken).ConfigureAwait(false);
        if (identityFailure is not null) return identityFailure;
        return await ApplyAdmittedAsync(change, baseline, lease, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<MirrorPulseNamespacePermissionResult?> PrepareRetainedLocalIdentityAsync(MirrorPulseNamespacePermissionChange change,
        MirrorPulseNamespacePermissionBaseline baseline, IMirrorPulseProtectedNamespacePermissionLease lease,
        CloudPlaceholderIdentity? initialLocalIdentity, CancellationToken cancellationToken)
    {
        if (baseline.IsDirectory)
            return await RetainExistingDirectoryIdentityAsync(change, baseline, lease, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsDirectory)
        {
            MirrorPulseNamespacePermissionLocalIdentity? retained = await _catalog.ReadNamespacePermissionLocalIdentityAsync(
                baseline.EvidenceId, cancellationToken).ConfigureAwait(false);
            if (retained is null)
            {
                IReadOnlyList<MirrorPulseNamespacePermissionChange> history = await _catalog.ReadNamespacePermissionObjectHistoryAsync(
                    baseline.EvidenceId, cancellationToken).ConfigureAwait(false);
                if (change.Phase != MirrorPulseNamespacePermissionPhase.Prepared ||
                    change.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Protect || history.Count != 1)
                    return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.Interrupted).ConfigureAwait(false);
            }
            MirrorPulseProtectedLocalIdentityObservation local = await lease.InspectLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
            var registered = await _catalog.ReadNamespaceMemberByBindingAsync(baseline.LocalObject, cancellationToken).ConfigureAwait(false);
            var current = registered is null ? null : await _catalog.ReadNamespaceMemberIdentityAsync(registered.MemberId, cancellationToken).ConfigureAwait(false);
            if (registered is not null && (registered.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || registered.OriginId != baseline.EvidenceId))
                throw new InvalidDataException("Original permission preparation cannot adopt another member's identity.");
            if (!MatchesRetainedIdentity(local, retained, current))
                return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged).ConfigureAwait(false);
            CloudPlaceholderIdentity identity = SelectLocalIdentity(local, retained, current, initialLocalIdentity);
            if (retained is null)
                await _catalog.PrepareNamespacePermissionLocalIdentityAsync(new(1, baseline.EvidenceId, change.Intent.OperationId,
                    baseline.LocalObject, identity.ItemId, identity.RemoteId, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            // Native presence does not establish that a previous official projection committed.
            // The public preparation replay repairs that projection without reconverting the
            // matching native identity. Accepted placeholders remain Access-only.
            if (!local.IsPlaceholder || local.PlaceholderIdentity!.RemoteRevision is null)
                await lease.PrepareLocalIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<MirrorPulseNamespacePermissionResult?> RetainExistingDirectoryIdentityAsync(
        MirrorPulseNamespacePermissionChange change, MirrorPulseNamespacePermissionBaseline baseline,
        IMirrorPulseProtectedNamespacePermissionLease lease, CancellationToken token)
    {
        var retained = await _catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId, token).ConfigureAwait(false);
        var local = await lease.InspectLocalIdentityAsync(token).ConfigureAwait(false);
        // The sync-root registration is the namespace anchor, not a remote member.
        // Windows can classify it as a placeholder without a CfSharp item identity.
        // Its original native binding, owner and descriptor were already checked.
        if (baseline.RootId is null && baseline.RelativePath.Length == 0)
            return baseline.LocalObject.LocalFileId == baseline.LocalObject.SyncRootFileId && retained is null
                ? null : await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged).ConfigureAwait(false);
        // Ordinary directories have no placeholder identity to retain. Permission work
        // must neither convert them nor invent a remote identity for them.
        if (retained is null && !local.IsPlaceholder && local.ItemId is null && local.RemoteId is null) return null;
        if (!local.IsPlaceholder || local.PlaceholderIdentity is null || local.ItemId is null || local.RemoteId is null ||
            !MatchesRetainedIdentity(local, retained, null))
            return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged).ConfigureAwait(false);
        if (retained is not null) return null;
        var history = await _catalog.ReadNamespacePermissionObjectHistoryAsync(baseline.EvidenceId, token).ConfigureAwait(false);
        if (change.Phase != MirrorPulseNamespacePermissionPhase.Prepared ||
            change.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Protect || history.Count != 1)
            return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.Interrupted).ConfigureAwait(false);
        await _catalog.PrepareNamespacePermissionLocalIdentityAsync(new(2, baseline.EvidenceId, change.Intent.OperationId,
            baseline.LocalObject, local.ItemId.Value, local.RemoteId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        return null;
    }

    private static bool MatchesRetainedIdentity(MirrorPulseProtectedLocalIdentityObservation local,
        MirrorPulseNamespacePermissionLocalIdentity? retained, MirrorPulseNamespaceMemberIdentity? current)
    {
        if ((local.ItemId is null) != (local.RemoteId is null) || local.ItemId == Guid.Empty ||
            local.RemoteId is not null && string.IsNullOrWhiteSpace(local.RemoteId)) return false;
        if (local.IsPlaceholder && (local.PlaceholderIdentity is null || local.PlaceholderIdentity.ItemId != local.ItemId ||
            local.PlaceholderIdentity.RemoteId != local.RemoteId)) return false;
        return local.ItemId is null || (current?.ItemId is { } item ? local.ItemId == item && local.RemoteId == current.RemoteId :
            retained is null || local.ItemId == retained.ItemId && local.RemoteId == retained.RemoteId);
    }

    private static CloudPlaceholderIdentity SelectLocalIdentity(MirrorPulseProtectedLocalIdentityObservation local,
        MirrorPulseNamespacePermissionLocalIdentity? retained, MirrorPulseNamespaceMemberIdentity? current, CloudPlaceholderIdentity? candidate)
    {
        CloudPlaceholderIdentity selected = current?.ItemId is { } item ? new(item, current.RemoteId!) :
            retained is not null ? new(retained.ItemId, retained.RemoteId) :
            local.ItemId is { } known ? new(known, local.RemoteId!) :
            candidate ?? throw new InvalidOperationException("An unknown original requires a Host-selected local identity before conversion.");
        if (selected.RemoteRevision is not null)
            throw new ArgumentException("Local preparation cannot introduce an accepted revision.", nameof(candidate));
        _ = selected.Encode();
        return selected;
    }
}
