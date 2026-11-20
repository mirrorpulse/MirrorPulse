using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed partial class MirrorPulseRemoteUpdateCoordinator
{
    private sealed record UpdateObservation(MirrorPulseNamespacePermissionObject Object, CloudItemSnapshot Snapshot, string? Revision)
    {
        internal MirrorPulseNamespacePermissionVerification Verification => new(Object.LocalObject, Object.OwnerSid, Object.Dacl, Object.ObservedAt);
    }

    internal static CloudRemoteChange RequireChange(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(options);
        if (instance.Value == Guid.Empty || !batch.BatchId.StartsWith(instance + "/", StringComparison.Ordinal) ||
            batch.Changes.Count > 65_536 || ordinal < 0 || ordinal >= batch.Changes.Count)
            throw new InvalidDataException("An existing remote update requires its bounded original instance batch entry.");
        var change = batch.Changes[ordinal];
        if (change.Kind is not (CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert) || change.ItemId is null ||
            change.ItemId == Guid.Empty || change.PreviousRelativePath is not null || !options.PreserveUnsynchronizedLocalContent ||
            options.ConflictResolver is not null || options.MaximumEntries is < 1 or > 65_536 || options.EchoSuppressionObservationCount is < 1 or > 64 ||
            options.EchoSuppressionLifetime <= TimeSpan.Zero || options.EchoSuppressionLifetime > TimeSpan.FromHours(1))
            throw new NotSupportedException("Existing remote updates require explicit identity, same-object upserts and durable preservation without a transient resolver.");
        return change;
    }

    internal static void RequireOriginal(MirrorPulseNamespaceRemoteUpdatePlan plan, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        if (plan.InstanceId != instance || plan.BatchId != batch.BatchId || plan.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) ||
            plan.BatchEntryCount != batch.Changes.Count || plan.Ordinal != ordinal || plan.ChangeId != change.ChangeId ||
            plan.ChangeFingerprint != ChangeFingerprint(change) || plan.ItemId != change.ItemId || plan.RemoteId != change.RemoteId ||
            plan.PreviousRemoteRevision != change.PreviousRemoteRevision || plan.RemoteRevision != change.RemoteRevision ||
            plan.Child.Context.Location.RelativePath != change.RelativePath.Replace('\\', '/') ||
            plan.Child.Context.Member.IsDirectory != (change.ItemKind == CloudItemKind.Directory) || plan.Policy != Policy(options) ||
            !MirrorPulseRemoteLibraryProvenance.IsSupported(plan.PackageVersion, plan.PackageSource))
            throw new InvalidDataException("The original remote update changed its exact entry, identity, object, policy or SDK provenance.");
    }

    internal static string ChangeFingerprint(CloudRemoteChange change) => Convert.ToHexString(
        new CloudRemoteChangeBatch("mirrorpulse/remote-update-entry", ReadOnlyMemory<byte>.Empty, [change], ReadOnlyMemory<byte>.Empty).Fingerprint.Span);

    private static MirrorPulseRemoteBirthPolicy Policy(CloudRemoteApplyOptions options) => new(options.PreserveUnsynchronizedLocalContent,
        options.SuppressLocalEcho, options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount);

    private void RequireRoute(InstanceId instance, string path, MirrorPulseNamespaceMemberProtectionSnapshot child,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, bool requireCurrentRole)
    {
        var route = router.ResolveCurrentPath(path); var root = router.GetRegistration(route.InstanceId, route.RootKey);
        if (route.InstanceId != instance || requireCurrentRole && root.State != RootRegistrationState.Active ||
            child.Context.Member.RootId != root.RootId || parent.Context.Member.RootId != root.RootId ||
            child.Context.Location.ParentMemberId != parent.Context.Member.MemberId || !parent.Context.Member.IsDirectory ||
            child.Context.Member.OwnerSid != execution.OwnerSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value ||
            requireCurrentRole && (child.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Protection.RoleSid != execution.RoleSid.Value))
            throw new InvalidDataException("The existing remote update lost its original routed objects, protected parent, owner or live role.");
    }

    private CloudItem Item(MirrorPulseNamespaceMemberProtectionSnapshot original) => original.Context.Member.IsDirectory
        ? fileSystem.GetDirectory(original.Context.Location.RelativePath) : fileSystem.GetFile(original.Context.Location.RelativePath);

    private static async Task<UpdateObservation> InspectAsync(MirrorPulseNamespaceMemberProtectionSnapshot original,
        CloudItem item, MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
        var facts = await guard.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        if (facts.LocalObject != original.Context.Member.LocalObject || facts.OwnerSid != original.Context.Member.OwnerSid ||
            facts.IsDirectory != original.Context.Member.IsDirectory || facts.RootId != original.Context.Member.RootId ||
            facts.RelativePath != original.Context.Location.RelativePath || facts.LinkCount != 1 ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(original.ExpectedDacl, facts.Dacl) ||
            !snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != facts.LocalObject)
            throw new InvalidDataException("The existing update changed its native binding, position, owner, descriptor, kind or single-link constraint.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, original.Identity, original.Observation is not null);
        return new(facts, snapshot, snapshot.IsPlaceholder ? CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).RemoteRevision : null);
    }

    private async Task<CloudItemState> ReadProjectionAsync(Guid itemId, MirrorPulseNamespaceMemberProtectionSnapshot original, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var projection = await transaction.Items.GetByItemIdAsync(itemId, token).ConfigureAwait(false);
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        if (projection is null || projection.IsTombstone || projection.ItemId != original.Identity.ItemId || projection.RemoteId != original.Identity.RemoteId ||
            projection.Kind != (original.Context.Member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File) ||
            projection.RelativePath.Replace('\\', '/') != original.Context.Location.RelativePath)
            throw new InvalidDataException("The existing update lost its exact public SDK item projection.");
        return projection;
    }

    private async Task RequireProgressAsync(MirrorPulseNamespaceRemoteUpdatePlan plan, bool applied, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await transaction.RemoteBatches.GetAsync(plan.BatchId, token).ConfigureAwait(false);
        int expected = plan.Ordinal + (applied ? 1 : 0);
        if (progress is null ? expected != 0 : progress.TotalEntryCount != plan.BatchEntryCount || progress.AppliedEntryCount != expected ||
            Convert.ToHexString(progress.Fingerprint.Span) != plan.BatchFingerprint || applied && progress.LastAppliedChangeId != plan.ChangeId)
            throw new InvalidDataException("The existing update requires its exact original SDK prefix; an uncertain native start remains fenced.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }
}
