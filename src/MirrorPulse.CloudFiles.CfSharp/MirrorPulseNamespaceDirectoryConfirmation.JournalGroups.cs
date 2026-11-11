using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed partial class MirrorPulseNamespaceDirectoryConfirmation
{
    /// <summary>Reads current native ownership while the caller retains its root work lease; performs no Worker RPC or ACK.</summary>
    internal async ValueTask RequireJournalGroupAsync(CloudFileSystem fileSystem, MirrorPulseJournalCoalescingPlan plan,
        IReadOnlyList<MirrorPulseWorkerChangeCommand> currentWindow, long currentUpperSequence, CancellationToken token)
    {
        RequireOriginalDirectoryWindow(plan, currentWindow, currentUpperSequence);
        if (plan.Effect is not (MirrorPulseCoalescedEffect.CreateDirectory or MirrorPulseCoalescedEffect.VerifyDirectoryAbsence) ||
            actor is null || execution is null)
            throw new NotSupportedException("Directory journal groups require the shared namespace owner and qualified effect boundary.");
        await actor.RunAsync(async admitted =>
        {
            actor.AssertAccess();
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected directory journal groups require Windows 11 version 24H2 or later.");
            var root = router.GetRegistration(plan.InstanceId, plan.RootKey);
            if (root.State != RootRegistrationState.Active ||
                (await catalog.ReadNamespaceRecoveryRootsAsync(admitted).ConfigureAwait(false)).Contains(root.RootId))
                throw new InvalidOperationException("Directory journal settlement requires its active unfenced owning root.");
            string localPath = router.ResolveUploadPath(plan.InstanceId, plan.RootKey, plan.FinalPath);
            var directory = fileSystem.GetDirectory(Path.GetRelativePath(fileSystem.Root.FullPath, localPath).Replace('\\', '/'));
            if (plan.Effect == MirrorPulseCoalescedEffect.VerifyDirectoryAbsence)
                await RequireRetiredDirectoryAsync(directory, plan, root.RootId, execution, admitted).ConfigureAwait(false);
            else
                await RequireCreatedDirectoryAsync(directory, plan, root.RootId, execution, admitted).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    internal static void RequireOriginalDirectoryWindow(MirrorPulseJournalCoalescingPlan plan,
        IReadOnlyList<MirrorPulseWorkerChangeCommand> currentWindow, long currentUpperSequence)
    {
        ArgumentNullException.ThrowIfNull(currentWindow);
        // A scan bound is not a global watermark: acknowledging unrelated high
        // headers can lower a later complete scan. Every original must still be
        // present, unchanged and contained by that fresh complete scan.
        if (currentUpperSequence < plan.Members.Max(member => member.Sequence) ||
            currentWindow.Any(command => command.OperationId == Guid.Empty || command.Sequence <= 0 || command.Sequence > currentUpperSequence) ||
            currentWindow.Select(command => command.OperationId).Distinct().Count() != currentWindow.Count ||
            currentWindow.Select(command => command.Sequence).Distinct().Count() != currentWindow.Count)
            throw new InvalidDataException("A retained directory group requires a complete current official observation window.");
        var originals = new List<MirrorPulseWorkerChangeCommand>(plan.Members.Count);
        foreach (var member in plan.Members)
        {
            var original = currentWindow.SingleOrDefault(command => command.OperationId == member.OperationId);
            if (original is null || original.ItemId is { } itemId && itemId != plan.ItemId || original.Sequence != member.Sequence ||
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(original with { ItemId = plan.ItemId }))) != member.Fingerprint)
                throw new InvalidDataException("A retained directory group cannot adopt a missing or changed original observation.");
            originals.Add(original);
        }
        var owned = plan.Members.Select(member => member.OperationId).ToHashSet();
        if (currentWindow.Any(other => !owned.Contains(other.OperationId) && originals.Any(original =>
            MirrorPulseJournalOrderPolicy.DependsOn(other, original) || MirrorPulseJournalOrderPolicy.DependsOn(original, other))))
            throw new InvalidOperationException("Later or transient directory dependencies require their own complete effect witnesses.");
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RequireCreatedDirectoryAsync(CloudDirectory directory, MirrorPulseJournalCoalescingPlan plan,
        RootId rootId, MirrorPulseNamespaceExecutionSession role, CancellationToken token)
    {
        var snapshot = await directory.InspectAsync(token).ConfigureAwait(false);
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding)
            throw new InvalidDataException("Directory creation must retain its actual original local placeholder.");
        var member = await catalog.ReadNamespaceMemberByBindingAsync(new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Directory creation cannot adopt an unregistered occupant.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        var birth = member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth
            ? await catalog.ReadNamespaceBirthPlanAsync(member.OriginId, token).ConfigureAwait(false) : null;
        if (!member.IsDirectory || member.RootId != rootId || birth is null || birth.ItemId != plan.ItemId || birth.RemoteRevision is not null ||
            location.RelativePath != directory.RelativePath.Replace('\\', '/'))
            throw new InvalidDataException("The original directory creation lost its protected birth or committed position.");
        var current = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
        var dispatch = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, token).ConfigureAwait(false);
        Guid? effectId = dispatch?.Steps.Single(step => step.Kind == MirrorPulseCoalescingStepKind.CreateDirectory).OperationId;
        var accepted = effectId is { } operation ? await catalog.ReadDirectoryAcceptanceAsync(operation, token).ConfigureAwait(false) : null;
        if (accepted is not null && (accepted.MemberId != member.MemberId || accepted.RootId != rootId ||
            accepted.PreviousItemId != plan.ItemId || accepted.PreviousRemoteId != birth.RemoteId ||
            accepted.PreviousAcceptanceOperationId is not null || accepted.LocalObject != member.LocalObject ||
            accepted.PositionOperationId != location.CurrentPositionOperationId || accepted.RelativePath != location.RelativePath))
            throw new InvalidDataException("Directory group recovery cannot borrow another accepted member or position.");
        if (current.AcceptanceOperationId is null ? current.ItemId != plan.ItemId :
            accepted is null || current.AcceptanceOperationId != accepted.OperationId || current.ItemId != accepted.AcceptedItemId ||
            accepted.MemberId != member.MemberId || accepted.PreviousItemId != plan.ItemId || accepted.LocalObject != member.LocalObject ||
            accepted.PositionOperationId != location.CurrentPositionOperationId || accepted.RelativePath != location.RelativePath)
            throw new InvalidDataException("A later directory identity cannot complete the original creation group.");
        bool nativeAccepted = accepted is not null && snapshot.PlaceholderIdentity.Span.SequenceEqual(
            new CloudPlaceholderIdentity(accepted.AcceptedItemId, accepted.RemoteId, accepted.AcceptedRevision).Encode());
        if (!nativeAccepted && !snapshot.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(plan.ItemId, birth.RemoteId, birth.RemoteRevision).Encode()))
            throw new InvalidDataException("Directory group creation requires its original native identity or exact retained acceptance.");
        await RequireCurrentParentAndDirectoryAsync(directory, member, location, role, requireEmpty: true, token).ConfigureAwait(false);
        await RequireOriginalDirectoryHeadersAsync(plan, allowClearedReference: nativeAccepted, token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RequireRetiredDirectoryAsync(CloudDirectory directory, MirrorPulseJournalCoalescingPlan plan,
        RootId rootId, MirrorPulseNamespaceExecutionSession role, CancellationToken token)
    {
        var retirements = await catalog.ReadNamespaceControlledRetirementGenerationsAsync(plan.ItemId, token).ConfigureAwait(false);
        if (retirements.Count != 1) throw new InvalidDataException("Directory absence requires its sole original controlled retirement.");
        var retained = await catalog.ReadNamespaceControlledRetirementContextAsync(retirements[0].OperationId, token).ConfigureAwait(false);
        var retirement = retained.Retirement; var member = retained.Member; var location = retained.Location;
        var birth = member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth
            ? await catalog.ReadNamespaceBirthPlanAsync(member.OriginId, token).ConfigureAwait(false) : null;
        var identity = retained.Identity;
        if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.NativeDeletedAt is null || retirement.CompletedAt is null ||
            retirement.StartedItemId != plan.ItemId || retirement.Intent.BatchOperationId is not null ||
            retirement.Intent.RootId != rootId || !retirement.Intent.IsDirectory || retirement.Intent.LocalObject != member.LocalObject ||
            member.RootId != rootId || !member.IsDirectory || birth is null || birth.ItemId != plan.ItemId || birth.RemoteRevision is not null ||
            identity.ItemId != plan.ItemId || identity.AcceptanceOperationId is not null ||
            location.RelativePath != retirement.Intent.RelativePath || location.RelativePath != directory.RelativePath.Replace('\\', '/'))
            throw new InvalidDataException("Backend absence cannot replace the original unuploaded directory's completed native retirement.");
        await using (var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var original = await transaction.Items.GetByItemIdAsync(plan.ItemId, token).ConfigureAwait(false);
            if (original is null || !original.IsTombstone || original.Kind != CloudItemKind.Directory || original.RemoteRevision is not null ||
                original.RemoteId != birth.RemoteId || original.RelativePath.Replace('\\', '/') != retirement.Intent.RelativePath)
                throw new InvalidDataException("Directory absence requires its original official tombstone, not a current path lookup.");
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        await RequireCurrentParentAndDirectoryAsync(directory, member, location, role, requireEmpty: false, token).ConfigureAwait(false);
        await RequireOriginalDirectoryHeadersAsync(plan, allowClearedReference: false, token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RequireCurrentParentAndDirectoryAsync(CloudDirectory directory, MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberLocation location, MirrorPulseNamespaceExecutionSession role, bool requireEmpty, CancellationToken token)
    {
        var parentMember = await catalog.ReadNamespaceMemberAsync(location.ParentMemberId
            ?? throw new InvalidDataException("Directory journal settlement requires its committed parent."), token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original directory parent is missing.");
        var parentLocation = await catalog.ReadNamespaceMemberLocationAsync(parentMember.MemberId, token).ConfigureAwait(false);
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        var parent = directory.Parent ?? throw new InvalidDataException("Directory journal settlement requires its actual direct parent.");
        if (parent.RelativePath.Replace('\\', '/') != parentLocation.RelativePath || parentProof.RoleSid != role.RoleSid.Value ||
            parentMember.OwnerSid != role.OwnerSid.Value || member.OwnerSid != role.OwnerSid.Value ||
            parentMember.RootId is { } root && root != member.RootId)
            throw new InvalidDataException("Directory journal settlement requires its current parent, owner and role.");
        await role.RunNamespaceOperationAsync(async () =>
        {
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            var before = await parentLease.InspectAsync(token).ConfigureAwait(false);
            RequireProtected(before, await parent.InspectAsync(token).ConfigureAwait(false), parentMember, parentProof, parentLocation.RelativePath);
            if (requireEmpty)
            {
                var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
                if (childProof.RoleSid != role.RoleSid.Value) throw new InvalidDataException("Directory creation lost its owning role.");
                await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(directory, router, token).ConfigureAwait(false);
                RequireProtected(await childLease.InspectAsync(token).ConfigureAwait(false), await directory.InspectAsync(token).ConfigureAwait(false), member, childProof, location.RelativePath);
                await using var children = directory.EnumerateLocalChildrenAsync(cancellationToken: token).GetAsyncEnumerator(token);
                if (await children.MoveNextAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("A nonempty directory requires its own sealed child effect witnesses.");
            }
            else if ((await directory.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new InvalidDataException("A later native occupant cannot complete the original directory absence.");
            RequireProtected(await parentLease.InspectAsync(token).ConfigureAwait(false), await parent.InspectAsync(token).ConfigureAwait(false), parentMember, parentProof, parentLocation.RelativePath);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentLease.InspectAsync(token).ConfigureAwait(false));
            if (await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false) != parentProof)
                throw new InvalidDataException("Parent protection changed across directory journal verification.");
        }).ConfigureAwait(false);
    }

    private async Task RequireOriginalDirectoryHeadersAsync(MirrorPulseJournalCoalescingPlan plan, bool allowClearedReference, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (var member in plan.Members)
        {
            var original = await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false);
            if (original is null || original.Sequence != member.Sequence ||
                original.ItemId != plan.ItemId && !(allowClearedReference && original.ItemId is null) ||
                original.Kind is not (CloudStateOperationKind.Create or CloudStateOperationKind.Move or CloudStateOperationKind.Delete))
                throw new InvalidDataException("Directory settlement lost an original official operation or its stable sequence and reference.");
        }
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }
}
