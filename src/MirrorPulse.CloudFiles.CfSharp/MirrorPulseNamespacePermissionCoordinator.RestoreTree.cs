using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    /// <summary>Recovers retained applications, rotates their owned ACLs, then audits the current role.</summary>
    /// <remarks>
    /// The original capture, owner, binding and restoration descriptors are never rewritten.
    /// Unfinished native work is replayed before a new rotation is prepared. All current-role
    /// rotation intents are durable before any of them is applied. Recovery uses local public
    /// CfSharp operations only; no source access, credentials or namespace mutation belongs here.
    /// The Host must keep dispatch offline until it separately admits the audited tree.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task<MirrorPulseNamespacePermissionTreeResult> RestoreTreeAsync(Guid manifestId,
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
            result = await RestoreTreeCoreAsync(manifestId, session.OwnerSid.Value, session.RoleSid.Value,
                directory => MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(session, directory), lease.InspectObjectsAsync,
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
                }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result! with { FailedOperation = failedOperation, FailedOperationId = failedOperationId };
    }

    internal async Task<MirrorPulseNamespacePermissionTreeResult> RestoreTreeCoreAsync(Guid manifestId,
        string ownerSid, string roleSid, Func<bool, string> targetDacl,
        Func<CancellationToken, IAsyncEnumerable<MirrorPulseNamespacePermissionObject>> inspect,
        Func<MirrorPulseNamespacePermissionPreparation, CancellationToken, Task<bool>> apply, CancellationToken stop)
    {
        _ = new SecurityIdentifier(ownerSid);
        _ = new SecurityIdentifier(roleSid);
        ArgumentNullException.ThrowIfNull(targetDacl);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(apply);
        int completed = 0;
        return await RunAdmittedTreeWorkAsync(manifestId, async tree =>
        {
            if (tree.Definition.Anchor.Baseline.OwnerSid != ownerSid)
                return Recover(MirrorPulseNamespacePermissionRecoveryReason.OwnerChanged);
            var expected = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            await foreach (var page in ReadTreePagesAsync(tree, stop).ConfigureAwait(false))
            {
                await _catalog.PrepareNamespacePermissionChangesAsync(page.Select(member => member.Preparation).ToArray(), stop).ConfigureAwait(false);
                foreach (var member in page)
                {
                    var current = await ReadLatestTreePreparationAsync(member, stop).ConfigureAwait(false);
                    if (current.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                        return Recover(MirrorPulseNamespacePermissionRecoveryReason.Interrupted);
                    expected.Add(current.Intent.RelativePath, current.Intent.OperationId);
                }
            }
            var mismatch = await AuditAsync(expected, inspect, requireVerified: false, stop).ConfigureAwait(false);
            if (mismatch is not null) return Recover(mismatch.Value);
            // A prior role may have disappeared after Prepared or Applied. Complete that exact
            // retained intent on the original object before selecting a replacement role.
            if (!await ApplyLatestTreeBottomUpAsync(tree, expected, apply, () => completed++, stop).ConfigureAwait(false))
                return Recover(MirrorPulseNamespacePermissionRecoveryReason.Failed);
            mismatch = await AuditAsync(expected, inspect, requireVerified: true, stop).ConfigureAwait(false);
            if (mismatch is not null) return Recover(mismatch.Value);
            var rotations = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            await foreach (var page in ReadTreePagesAsync(tree, stop).ConfigureAwait(false))
            {
                var planned = new List<MirrorPulseNamespacePermissionPreparation>(page.Count);
                foreach (var member in page)
                {
                    var current = await ReadLatestTreePreparationAsync(member, stop).ConfigureAwait(false);
                    var verified = await _catalog.ReadNamespacePermissionChangeForApplicationAsync(current.Intent.OperationId, stop).ConfigureAwait(false);
                    if (verified.Phase != MirrorPulseNamespacePermissionPhase.Verified)
                        return Recover(MirrorPulseNamespacePermissionRecoveryReason.Interrupted);
                    if (current.Intent.RoleSid == roleSid && !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                        targetDacl(current.Baseline.IsDirectory), verified.Verification!.Dacl))
                        return Recover(MirrorPulseNamespacePermissionRecoveryReason.DaclChanged);
                    if (current.Intent.RoleSid != roleSid)
                    {
                        var rotation = current.Intent with
                        {
                            OperationId = Guid.NewGuid(),
                            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                            RoleSid = roleSid,
                            ExpectedDacl = verified.Verification!.Dacl,
                            TargetDacl = targetDacl(current.Baseline.IsDirectory),
                            PreparedAt = DateTimeOffset.UtcNow,
                        };
                        current = new(current.Baseline, rotation);
                        planned.Add(current);
                    }
                    rotations.Add(current.Intent.RelativePath, current.Intent.OperationId);
                }
                if (planned.Count != 0) await _catalog.PrepareNamespacePermissionChangesAsync(planned, stop).ConfigureAwait(false);
            }
            // Recheck the retained expected descriptor for every rotation before writing one.
            mismatch = await AuditAsync(rotations, inspect, requireVerified: false, stop).ConfigureAwait(false);
            if (mismatch is not null) return Recover(mismatch.Value);
            completed = 0;
            if (!await ApplyLatestTreeBottomUpAsync(tree, rotations, apply, () => completed++, stop).ConfigureAwait(false))
                return Recover(MirrorPulseNamespacePermissionRecoveryReason.Failed);
            mismatch = await AuditAsync(rotations, inspect, requireVerified: true, stop).ConfigureAwait(false);
            return mismatch is null ? new(manifestId, MirrorPulseNamespacePermissionTreeOutcome.Verified,
                completed, DateTimeOffset.UtcNow) : Recover(mismatch.Value);
        }, stop).ConfigureAwait(false);

        MirrorPulseNamespacePermissionTreeResult Recover(MirrorPulseNamespacePermissionRecoveryReason reason) =>
            new(manifestId, MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, completed, DateTimeOffset.UtcNow, reason);
    }

    private async IAsyncEnumerable<IReadOnlyList<MirrorPulseNamespacePermissionTreeMember>> ReadTreePagesAsync(
        MirrorPulseNamespacePermissionTree tree, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken stop)
    {
        int after = -1;
        while (after + 1 < tree.CapturedMembers)
        {
            var page = await _catalog.ReadNamespacePermissionTreeMembersAsync(tree.Definition.ManifestId,
                after, TreeApplicationPageSize, stop).ConfigureAwait(false);
            if (page.Count == 0) throw new InvalidDataException("The original tree capture lost a page.");
            after = page[^1].Sequence;
            yield return page;
        }
    }

    private async Task<MirrorPulseNamespacePermissionPreparation> ReadLatestTreePreparationAsync(
        MirrorPulseNamespacePermissionTreeMember original, CancellationToken stop)
    {
        var history = await _catalog.ReadNamespacePermissionObjectHistoryAsync(original.Preparation.Baseline.EvidenceId, stop).ConfigureAwait(false);
        if (history.Count == 0) throw new InvalidDataException("The original tree member has no retained permission intent.");
        var latest = history[^1].Intent;
        if (latest.LocalObject != original.Preparation.Baseline.LocalObject || latest.RootId != original.Preparation.Intent.RootId)
            throw new InvalidDataException("The latest permission intent escaped its original managed root.");
        return new(original.Preparation.Baseline, latest);
    }

    private async Task<bool> ApplyLatestTreeBottomUpAsync(MirrorPulseNamespacePermissionTree tree,
        Dictionary<string, Guid> expected, Func<MirrorPulseNamespacePermissionPreparation, CancellationToken, Task<bool>> apply,
        Action completed, CancellationToken stop)
    {
        int remaining = tree.CapturedMembers;
        while (remaining > 0)
        {
            int start = Math.Max(0, remaining - TreeApplicationPageSize);
            var page = await _catalog.ReadNamespacePermissionTreeMembersAsync(tree.Definition.ManifestId,
                start - 1, remaining - start, stop).ConfigureAwait(false);
            if (page.Count != remaining - start) throw new InvalidDataException("The original tree capture lost a page.");
            for (int index = page.Count - 1; index >= 0; index--)
            {
                stop.ThrowIfCancellationRequested();
                var current = await ReadLatestTreePreparationAsync(page[index], stop).ConfigureAwait(false);
                if (!expected.TryGetValue(current.Intent.RelativePath, out Guid operationId) || operationId != current.Intent.OperationId)
                    return false;
                if (!await apply(current, stop).ConfigureAwait(false)) return false;
                completed();
            }
            remaining = start;
        }
        return true;
    }
}
