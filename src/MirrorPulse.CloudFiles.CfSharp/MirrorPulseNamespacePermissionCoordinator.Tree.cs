using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public enum MirrorPulseNamespacePermissionTreeOutcome { Verified, RecoveryRequired }

/// <summary>A fresh whole-tree permission observation, not an enduring namespace authorization.</summary>
/// <remarks>No journal acknowledgement, remote acceptance or content freeze is implied.</remarks>
public sealed record MirrorPulseNamespacePermissionTreeResult(Guid ManifestId,
    MirrorPulseNamespacePermissionTreeOutcome Outcome, int CompletedMembers, DateTimeOffset ObservedAt,
    MirrorPulseNamespacePermissionRecoveryReason? RecoveryReason = null, Guid? FailedOperationId = null,
    MirrorPulseProtectedNamespacePermissionResult? FailedOperation = null);

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    private const int TreeApplicationPageSize = 256;

    /// <summary>Applies a sealed original capture bottom-up and independently audits all live members.</summary>
    /// <remarks>
    /// The caller retains the namespace lease until admission closes. It keeps names stable,
    /// but does not freeze content writers or directory membership during initialization.
    /// Added, removed or replaced members forbid a verified result. Every file conversion and
    /// ACL write uses a separate public CfSharp protected operation, after product admission.
    /// Startup role rotation and newly created objects require their own retained evidence.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task<MirrorPulseNamespacePermissionTreeResult> ReconcileTreeAsync(Guid manifestId,
        MirrorPulseWindowsNamespacePermissionTreeLease lease, MirrorPulseNamespaceExecutionSession session,
        Func<MirrorPulseNamespacePermissionBaseline, CloudPlaceholderIdentity?>? selectLocalIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(session);
        MirrorPulseNamespacePermissionTreeResult? result = null;
        MirrorPulseProtectedNamespacePermissionResult? failedOperation = null;
        Guid? failedOperationId = null;
        await session.RunNamespaceOperationAsync(async () =>
        {
            result = await ReconcileTreeCoreAsync(manifestId, session.RoleSid.Value, lease.InspectObjectsAsync,
                async (preparation, stop) =>
                {
                    var change = await _catalog.ReadNamespacePermissionChangeForApplicationAsync(
                        preparation.Intent.OperationId, stop).ConfigureAwait(false);
                    var native = await ApplyProtectedAdmittedAsync(change, preparation.Baseline,
                        lease.GetItem(preparation.Intent.RelativePath), lease.Router,
                        selectLocalIdentity?.Invoke(preparation.Baseline), stop).ConfigureAwait(false);
                    bool completed = native.Receipt?.Outcome == CloudProtectedLocalOperationOutcome.Completed &&
                        native.Permission?.Outcome is MirrorPulseNamespacePermissionOutcome.Verified or
                            MirrorPulseNamespacePermissionOutcome.AlreadyVerified;
                    if (!completed) { failedOperation = native; failedOperationId = preparation.Intent.OperationId; }
                    return completed;
                }, cancellationToken, session.OwnerSid.Value).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result! with { FailedOperation = failedOperation, FailedOperationId = failedOperationId };
    }

    // This boundary is internal so policy tests cannot manufacture a public native receipt.
    internal async Task<MirrorPulseNamespacePermissionTreeResult> ReconcileTreeCoreAsync(Guid manifestId,
        string roleSid, Func<CancellationToken, IAsyncEnumerable<MirrorPulseNamespacePermissionObject>> inspect,
        Func<MirrorPulseNamespacePermissionPreparation, CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken, string? ownerSid = null)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(apply);
        _ = new SecurityIdentifier(roleSid);
        int completed = 0;
        return await RunAdmittedTreeWorkAsync(manifestId, async tree =>
        {
            if (ownerSid is not null && tree.Definition.Anchor.Baseline.OwnerSid != ownerSid)
                return Recover(MirrorPulseNamespacePermissionRecoveryReason.OwnerChanged);
            var expected = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            int after = -1;
            while (after + 1 < tree.CapturedMembers)
            {
                var page = await _catalog.ReadNamespacePermissionTreeMembersAsync(manifestId, after,
                    TreeApplicationPageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0) throw new InvalidDataException("The sealed permission capture lost a member.");
                foreach (var member in page)
                {
                    if (member.Preparation.Intent.RoleSid != roleSid ||
                        member.Preparation.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Protect)
                        return Recover(MirrorPulseNamespacePermissionRecoveryReason.Interrupted);
                    expected.Add(member.Preparation.Intent.RelativePath, member.Preparation.Intent.OperationId);
                }
                // All immutable baselines and intents exist before the first permission write.
                await _catalog.PrepareNamespacePermissionChangesAsync(page.Select(member => member.Preparation).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                after = page[^1].Sequence;
            }
            var mismatch = await AuditAsync(expected, inspect, requireVerified: false, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null) return Recover(mismatch.Value);
            int remaining = tree.CapturedMembers;
            while (remaining > 0)
            {
                int start = Math.Max(0, remaining - TreeApplicationPageSize);
                var page = await _catalog.ReadNamespacePermissionTreeMembersAsync(manifestId, start - 1,
                    remaining - start, cancellationToken).ConfigureAwait(false);
                if (page.Count != remaining - start) throw new InvalidDataException("The sealed permission capture lost a page.");
                for (int index = page.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await apply(page[index].Preparation, cancellationToken).ConfigureAwait(false))
                        return Recover(MirrorPulseNamespacePermissionRecoveryReason.Failed);
                    completed++;
                }
                remaining = start;
            }
            mismatch = await AuditAsync(expected, inspect, requireVerified: true, cancellationToken).ConfigureAwait(false);
            return mismatch is null ? new(manifestId, MirrorPulseNamespacePermissionTreeOutcome.Verified,
                completed, DateTimeOffset.UtcNow) : Recover(mismatch.Value);
        }, cancellationToken).ConfigureAwait(false);

        MirrorPulseNamespacePermissionTreeResult Recover(MirrorPulseNamespacePermissionRecoveryReason reason) =>
            new(manifestId, MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, completed, DateTimeOffset.UtcNow, reason);
    }

    private async Task<MirrorPulseNamespacePermissionRecoveryReason?> AuditAsync(Dictionary<string, Guid> expected,
        Func<CancellationToken, IAsyncEnumerable<MirrorPulseNamespacePermissionObject>> inspect,
        bool requireVerified, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var observed in inspect(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!expected.TryGetValue(observed.RelativePath, out Guid operationId) || !seen.Add(observed.RelativePath))
                return MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged;
            var change = await _catalog.ReadNamespacePermissionChangeForApplicationAsync(operationId, cancellationToken).ConfigureAwait(false);
            var history = await _catalog.ReadNamespacePermissionObjectHistoryAsync(change.Intent.EvidenceId, cancellationToken).ConfigureAwait(false);
            if (history.Count == 0 || history[^1].Intent.OperationId != operationId)
                return MirrorPulseNamespacePermissionRecoveryReason.Interrupted;
            var baseline = await _catalog.ReadNamespacePermissionBaselineAsync(change.Intent.EvidenceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original tree permission evidence is missing.");
            if (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
                return change.RecoveryReason ?? MirrorPulseNamespacePermissionRecoveryReason.Failed;
            if (requireVerified && change.Phase != MirrorPulseNamespacePermissionPhase.Verified)
                return MirrorPulseNamespacePermissionRecoveryReason.Interrupted;
            var mismatch = MatchApplicationObservation(baseline, change, observed);
            if (mismatch is not null) return mismatch;
        }
        return seen.Count == expected.Count ? null : MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged;
    }

    private async Task<T> RunAdmittedTreeWorkAsync<T>(Guid manifestId,
        Func<MirrorPulseNamespacePermissionTree, Task<T>> work, CancellationToken cancellationToken)
    {
        MirrorPulseNamespacePermissionTree initial = await _catalog.ReadNamespacePermissionTreeAsync(
            manifestId, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("The original tree capture is missing.");
        if (initial.Phase != MirrorPulseNamespacePermissionTreePhase.Sealed)
            throw new InvalidOperationException("Whole-tree permission application requires a sealed original capture.");
        PermissionAdmissionScope scope = PermissionAdmissionScope.From(initial.Definition.Anchor.Intent);
        EnterApplication(scope);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // Revalidate all original fingerprints before acquiring or applying native scopes.
            var tree = await _catalog.SealNamespacePermissionTreeAsync(manifestId, initial.Seal!.SealedAt,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await work(tree).ConfigureAwait(false);
        }
        finally
        {
            if (entered) _gate.Release();
            ExitApplication(scope);
        }
    }
}
