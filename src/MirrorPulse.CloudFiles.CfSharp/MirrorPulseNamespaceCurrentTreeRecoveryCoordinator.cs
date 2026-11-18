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
        CancellationToken cancellationToken = default) => RecoverCoreAsync(operationId, null, null, null, cancellationToken);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverRootAsync(Guid operationId, RootId rootId,
        CancellationToken cancellationToken) => RecoverCoreAsync(operationId, rootId, null, null, cancellationToken);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverDirectoryTransferRootAsync(Guid operationId, RootId rootId,
        Guid transferId, Guid? startedBirthId, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, rootId, null, transferId, cancellationToken, startedBirthId: startedBirthId);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverFileTransferRootAsync(Guid operationId, RootId rootId,
        Guid transferId, Guid? startedBirthId, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, rootId, null, null, cancellationToken, transferId, startedBirthId);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverReplacementRootAsync(Guid operationId, RootId rootId,
        Guid replacementId, Func<CancellationToken, ValueTask>? executeReplacement, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, rootId, null, null, cancellationToken, replacementId: replacementId,
            executeReplacement: executeReplacement);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverRetirementBatchRootAsync(Guid operationId, RootId rootId,
        Guid retirementBatchId, Func<CancellationToken, ValueTask>? executeBatch, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, rootId, null, null, cancellationToken, retirementBatchId: retirementBatchId, executeBatch: executeBatch);

    internal ValueTask<MirrorPulseNamespaceInitializationResult> RecoverJournalDirectoryMoveAsync(Guid operationId,
        MirrorPulseJournalDirectoryMoveRecovery.ProjectionRecovery move, CancellationToken cancellationToken) =>
        RecoverCoreAsync(operationId, move.Admission.RootId, move, null, cancellationToken);

    private async ValueTask<MirrorPulseNamespaceInitializationResult> RecoverCoreAsync(Guid operationId, RootId? selectedRoot,
        MirrorPulseJournalDirectoryMoveRecovery.ProjectionRecovery? move, Guid? transferId, CancellationToken cancellationToken,
        Guid? fileTransferId = null, Guid? startedBirthId = null, Guid? replacementId = null,
        Func<CancellationToken, ValueTask>? executeReplacement = null, Guid? retirementBatchId = null,
        Func<CancellationToken, ValueTask>? executeBatch = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.", nameof(operationId));
        var retained = await catalog.ReadNamespaceInitializationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (retained?.RequestedInventoryKind is { } requested && requested != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
            throw new InvalidOperationException("The pending initializer requires original-inventory recovery.");
        if (retained is not null && (selectedRoot is { } selected ? !retained.IsScoped || retained.Roots.Count != 1 ||
            retained.Roots[0].RootId != selected : retained.IsScoped))
            throw new InvalidOperationException("The retained initializer requires its explicit complete or selected-root recovery route.");
        if (retained is not null && (retained.DirectoryTransferOperationId != transferId || retained.FileTransferOperationId != fileTransferId ||
            retained.FileTransferStartedBirthId != (fileTransferId is not null ? startedBirthId : null) ||
            retained.DirectoryTransferStartedBirthId != (transferId is not null ? startedBirthId : null)))
            throw new InvalidOperationException("Current recovery requires its exact transfer purpose.");
        if (retained is not null && retained.ReplacementOperationId != replacementId)
            throw new InvalidOperationException("Current recovery requires its exact replacement purpose.");
        if (retained is not null && retained.RetirementBatchOperationId != retirementBatchId)
            throw new InvalidOperationException("Current recovery requires its exact retirement batch purpose.");
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
        Guid? pendingReplacementMember = null;
        if (replacementId is { } originalReplacement)
        {
            var replacement = await catalog.ReadNamespaceReplacementExecutionAsync(originalReplacement, cancellationToken).ConfigureAwait(false);
            if (replacement is null || replacement.Intent.RootId != selectedRoot || replacement.Cancellation is not null)
                throw new InvalidOperationException("Current recovery requires the original replacement in its selected root.");
            if (await catalog.ReadNamespaceReplacementCompletionAsync(originalReplacement, cancellationToken).ConfigureAwait(false) is null)
            {
                if (executeReplacement is null)
                    throw new InvalidOperationException("Only the original replacement executor may resume the pending file.");
                pendingReplacementMember = replacement.Intent.MemberId;
            }
        }
        bool pendingRetirementBatch = false;
        if (retirementBatchId is { } originalBatch)
        {
            var batch = await catalog.ReadNamespaceRetirementBatchAsync(originalBatch, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.RootId != selectedRoot || batch.RemoteEntry is not null || batch.RemoteConflictCommandId is not null ||
                batch.Phase is not (MirrorPulseNamespaceRetirementBatchPhase.Prepared or MirrorPulseNamespaceRetirementBatchPhase.Completed))
                throw new InvalidOperationException("Current recovery requires the original controlled batch in its selected root.");
            pendingRetirementBatch = batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.Prepared;
            if (pendingRetirementBatch && executeBatch is null)
                throw new InvalidOperationException("Only the original retirement executor may resume the pending batch.");
        }
        if (retained is null)
        {
            var roots = await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog, registrations, cancellationToken).ConfigureAwait(false);
            retained = await catalog.PrepareNamespaceInitializationAsync(new(retirementBatchId is not null ? 10 : replacementId is not null ? 9 :
                startedBirthId is not null ? (transferId is not null ? 8 : 7) : fileTransferId is not null ? 6 :
                selectedRoot is not null ? 4 : roots.Count == 0 ? 3 : roots.Any(root => root.BornRootMemberId is not null) ? 2 : 1,
                operationId, roots, DateTimeOffset.UtcNow)
            {
                RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory,
                JournalDirectoryMoveOperationId = move is { IsAcknowledged: false } ? move.Admission.JournalOperationId : null,
                DirectoryTransferOperationId = transferId,
                FileTransferOperationId = fileTransferId,
                FileTransferStartedBirthId = fileTransferId is not null ? startedBirthId : null,
                DirectoryTransferStartedBirthId = transferId is not null ? startedBirthId : null,
                ReplacementOperationId = replacementId,
                RetirementBatchOperationId = retirementBatchId
            }, cancellationToken).ConfigureAwait(false);
        }
        if (!retained.Roots.Select(root => root.RootId).ToHashSet().SetEquals(registrations.Select(root => root.RootId)))
            throw new InvalidDataException("The current initialization root layout changed after admission.");
        var selectedIds = registrations.Select(root => root.RootId).ToHashSet();
        var anchor = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeAnchorRecovery,
            () => new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                .ProtectAsync(cancellationToken)).ConfigureAwait(false);
        bool transferred = false;
        string? memberProgress = null;
        void ReportMemberProgress(string stage)
        {
            memberProgress = stage;
            Progress?.Invoke(stage);
        }
        try
        {
            var anchorMember = await catalog.ReadNamespaceMemberByBindingAsync(anchor.Original.LocalObject, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Original membership initialization must register the sync-root anchor before current recovery.");
            if (anchorMember.ParentMemberId is not null || anchorMember.RootId is not null || !anchorMember.IsDirectory ||
                anchorMember.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || anchorMember.OriginId != anchor.Original.EvidenceId)
                throw new InvalidDataException("Current recovery lost the registered original sync-root anchor.");
            var graph = await catalog.ReadNamespaceMemberTreeAsync(anchorMember.MemberId, cancellationToken).ConfigureAwait(false);
            if (pendingReplacementMember is { } replacementMember && !graph.Any(entry => entry.MemberId == replacementMember))
                throw new InvalidDataException("The pending replacement file is missing from its original current tree.");
            var originals = new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions);
            var born = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
            {
                Progress = ReportMemberProgress
            };
            _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeMemberRecovery, async () =>
            {
                foreach (var entry in graph.Where(entry => entry.MemberId != anchorMember.MemberId && entry.MemberId != pendingReplacementMember))
                {
                    memberProgress = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var member = await catalog.ReadNamespaceMemberAsync(entry.MemberId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("A current recovery member lost its original registration.");
                    if (selectedRoot is not null && (member.RootId is not { } root || !selectedIds.Contains(root))) continue;
                    var moveIdentity = move?.PermissionIdentity(member.MemberId);
                    if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
                    {
                        ReportMemberProgress("NamespaceOriginalMemberBegin");
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
                        ReportMemberProgress("NamespaceBornMemberBegin");
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
            if (pendingReplacementMember is not null)
                await executeReplacement!(cancellationToken).ConfigureAwait(false);
            if (pendingRetirementBatch)
                await executeBatch!(cancellationToken).ConfigureAwait(false);
            Progress?.Invoke("NamespaceAuditBegin");
            var completion = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeInitializationAudit,
                () => new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                    .CompleteInitializationAsync(operationId, anchorMember.MemberId, anchor, cancellationToken)).ConfigureAwait(false);
            Progress?.Invoke("NamespaceAuditCompleted");
            transferred = true; return new(completion, anchor);
        }
        catch (Exception error) when (MirrorPulseRootLabelCancellationFailureDiagnostics.Stage(error) ==
            MirrorPulseRootLabelCancellationStage.CurrentTreeMemberRecovery)
        {
            MirrorPulseTransferFailureDiagnostics.MarkMemberRecoveryStep(error, memberProgress);
            throw;
        }
        finally { if (!transferred) await anchor.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool SamePayload<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));
}
