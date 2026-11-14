using System.Runtime.Versioning;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Recovers registered current original and born membership before its full native audit.</summary>
/// <remarks>Retains first capture, birth, membership and completed positions. Unregistered or partially
/// executed namespace operations require their own recovery before current initialization can complete.
/// No source operation, journal acknowledgment or automatic Host readiness is implied.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    IMirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    internal Action<string>? Progress { get; init; }

    public ValueTask<MirrorPulseNamespaceInitializationResult> RecoverAsync(Guid operationId,
        CancellationToken cancellationToken = default) => RecoverCoreAsync(operationId, null, null, cancellationToken);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverRootAsync(Guid operationId, RootId rootId,
        CancellationToken cancellationToken) => RecoverCoreAsync(operationId, rootId, null, cancellationToken);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverJournalDirectoryMoveAsync(Guid operationId,
        MirrorPulseJournalDirectoryMoveRecovery.ProjectionRecovery move, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, move.Admission.RootId, move, cancellationToken);

    private async ValueTask<MirrorPulseNamespaceInitializationResult> RecoverCoreAsync(Guid operationId, RootId? selectedRoot,
        MirrorPulseJournalDirectoryMoveRecovery.ProjectionRecovery? move, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.", nameof(operationId));
        var retained = await catalog.ReadNamespaceInitializationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (retained?.RequestedInventoryKind is { } requested && requested != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
            throw new InvalidOperationException("The pending initializer requires original-inventory recovery.");
        if (retained is not null && (selectedRoot is { } selected ? retained.Version is not (4 or 5) || retained.Roots.Count != 1 ||
            retained.Roots[0].RootId != selected : retained.Version is 4 or 5))
            throw new InvalidOperationException("The retained initializer requires its explicit complete or selected-root recovery route.");
        if (retained?.JournalDirectoryMoveOperationId is { } original)
        {
            if (move is not null && move.Admission.JournalOperationId != original ||
                move is null && (await catalog.ReadJournalDirectoryMoveAsync(original, cancellationToken).ConfigureAwait(false))?.Mutation.State != MirrorPulseMutationState.Acknowledged)
                throw new InvalidOperationException("Current recovery requires its exact original directory Move.");
        }
        else if (retained is not null && move is { IsAcknowledged: false })
            throw new InvalidOperationException("An ordinary initializer cannot adopt an unfinished directory Move.");
        if (await catalog.ReadNamespaceInitializationCompletionAsync(operationId, cancellationToken).ConfigureAwait(false) is { } historical)
        {
            if (historical.InventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
                throw new InvalidOperationException("This operation completed the original inventory rather than the current inventory.");
            return new(historical, null);
        }
        var registrations = router.Registrations.Where(root => selectedRoot is null || root.RootId == selectedRoot.Value)
            .OrderBy(root => root.RootId.Value).ToArray();
        if (selectedRoot is not null && registrations.Length != 1)
            throw new InvalidOperationException("The selected recovery root is not configured.");
        if (retained is null)
        {
            var roots = await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog, registrations, cancellationToken).ConfigureAwait(false);
            retained = await catalog.PrepareNamespaceInitializationAsync(new(selectedRoot is not null ? 4 : roots.Count == 0 ? 3 : roots.Any(root => root.BornRootMemberId is not null) ? 2 : 1,
                operationId, roots, DateTimeOffset.UtcNow)
            {
                RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory,
                JournalDirectoryMoveOperationId = move is { IsAcknowledged: false } ? move.Admission.JournalOperationId : null
            }, cancellationToken).ConfigureAwait(false);
        }
        if (!retained.Roots.Select(root => root.RootId).ToHashSet().SetEquals(registrations.Select(root => root.RootId)))
            throw new InvalidDataException("The current initialization root layout changed after admission.");
        var selectedIds = registrations.Select(root => root.RootId).ToHashSet();
        var anchor = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeAnchorRecovery,
            () => new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                .ProtectAsync(cancellationToken)).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            var anchorMember = await catalog.ReadNamespaceMemberByBindingAsync(anchor.Original.LocalObject, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Original membership initialization must register the sync-root anchor before current recovery.");
            if (anchorMember.ParentMemberId is not null || anchorMember.RootId is not null || !anchorMember.IsDirectory ||
                anchorMember.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || anchorMember.OriginId != anchor.Original.EvidenceId)
                throw new InvalidDataException("Current recovery lost the registered original sync-root anchor.");
            var graph = await catalog.ReadNamespaceMemberTreeAsync(anchorMember.MemberId, cancellationToken).ConfigureAwait(false);
            var originals = new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions);
            var born = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions) { Progress = Progress };
            _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeMemberRecovery, async () =>
            {
                foreach (var entry in graph.Where(entry => entry.MemberId != anchorMember.MemberId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var member = await catalog.ReadNamespaceMemberAsync(entry.MemberId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("A current recovery member lost its original registration.");
                    if (selectedRoot is not null && (member.RootId is not { } root || !selectedIds.Contains(root))) continue;
                    var moveIdentity = move?.PermissionIdentity(member.MemberId);
                    if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
                    {
                        Progress?.Invoke("NamespaceOriginalMemberBegin");
                        var restored = await originals.RecoverAsync(operationId, member.MemberId, moveIdentity, cancellationToken).ConfigureAwait(false);
                        if (restored.Permission?.Outcome is not (MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified) ||
                            restored.Receipt is not { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true })
                            throw new IOException($"A registered original requires pending native recovery before the current graph can complete. " +
                                $"Directory={member.IsDirectory}; permission={restored.Permission?.Outcome.ToString() ?? "none"}; " +
                                $"reason={restored.Permission?.RecoveryReason?.ToString() ?? "none"}; " +
                                $"scope={restored.Receipt?.Outcome.ToString() ?? "none"}; drained={restored.Receipt?.Drained.ToString() ?? "none"}; " +
                                $"nativeObservation={restored.FailureDiagnostic ?? "unavailable"}");
                        Progress?.Invoke("NamespaceOriginalMemberCompleted");
                    }
                    else
                    {
                        Progress?.Invoke("NamespaceBornMemberBegin");
                        var selection = await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(operationId, member.MemberId, cancellationToken).ConfigureAwait(false);
                        var child = selection.Lineage[0];
                        var parent = selection.Parent;
                        var latest = selection.LatestAction;
                        // A cancellation can have just completed this initializer's born work.
                        // Retain that exact drained epoch when its current parent and live role
                        // still match. This only avoids repeating recovery; the complete fresh
                        // native audit below remains mandatory before initialization can finish.
                        if (latest is not null && latest.InitializationOperationId == operationId &&
                            latest.Child.Member == child.Member && latest.Child.Location == child.Location && latest.Child.Identity == child.Identity &&
                            child.BirthProtection is { } protection && protection.RoleSid == execution.RoleSid.Value &&
                            member.OwnerSid == execution.OwnerSid.Value && parent.Context.Protection.RoleSid == execution.RoleSid.Value &&
                            SamePayload(latest.Parent, parent) &&
                            selection.LatestCompletion is { Drained: true } completed &&
                            completed.ActionId == latest.ActionId && completed.ProtectionId == protection.ProtectionId &&
                            MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(latest.TargetDacl, protection.Verification.Dacl))
                        {
                            if (move is not null) await move.ProjectMemberAsync(member.MemberId, cancellationToken).ConfigureAwait(false);
                            Progress?.Invoke("NamespaceBornMemberCompleted");
                            continue;
                        }
                        bool resume = latest is not null && latest.InitializationOperationId == operationId && latest.Child == child && latest.Parent == parent &&
                            selection.LatestCompletion is null;
                        var restored = await born.RecoverAsync(operationId, resume ? latest!.ActionId : Guid.NewGuid(), member.MemberId,
                            moveIdentity, cancellationToken).ConfigureAwait(false);
                        if (restored.Completion is null) throw new IOException("A registered born object requires pending native recovery before the current graph can complete.");
                        Progress?.Invoke("NamespaceBornMemberCompleted");
                    }
                    if (move is not null) await move.ProjectMemberAsync(member.MemberId, cancellationToken).ConfigureAwait(false);
                }
                return true;
            }).ConfigureAwait(false);
            if (move is not null) await move.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
            Progress?.Invoke("NamespaceAuditBegin");
            var completion = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeInitializationAudit,
                () => new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                    .CompleteInitializationAsync(operationId, anchorMember.MemberId, anchor, cancellationToken)).ConfigureAwait(false);
            Progress?.Invoke("NamespaceAuditCompleted");
            transferred = true; return new(completion, anchor);
        }
        finally { if (!transferred) await anchor.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool SamePayload<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));
}
