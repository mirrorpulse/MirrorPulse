using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRemoteRetirementCheckpoint { AfterAdmission, AfterStart, AfterSdkApply, AfterSettlement, AfterTreeOutcome, AfterMemberPage }

/// <summary>Lets the original SDK batch delete a registered leaf, then settles that exact member.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteRetirementBatchCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteRetirementCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    internal async ValueTask<CloudRemoteApplyResult> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        var retained = await catalog.ReadNamespaceRemoteRetirementAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        Guid memberId;
        if (retained is null)
            memberId = (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
                .InspectAsync(change.RelativePath, change.ItemKind == CloudItemKind.Directory, token).ConfigureAwait(false)).MemberId;
        else { RequireOriginal(retained.Intent, instance, batch, ordinal, options); memberId = retained.Intent.MemberId; }
        return await actor.RunAsync(async admitted =>
        {
            var child = retained?.Intent.RemoteEntry?.Child ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote delete child is not protected membership.");
            var parent = retained?.Intent.RemoteEntry?.Parent ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(
                child.Context.Location.ParentMemberId ?? throw new InvalidDataException("Remote retirement cannot delete the sync-root anchor."), admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote delete parent is missing.");
            var owningRoot = RequireCurrentRoute(instance, change, child, parent);
            if (retained?.Intent.RemoteEntry?.RootKey is { } originalRootKey && originalRootKey != owningRoot.UniquenessKey)
                throw new InvalidDataException("The original retirement changed its retained root key.");
            if (retained is null)
            {
                if ((await catalog.ReadNamespaceDispatchFencedRootsAsync(admitted).ConfigureAwait(false)).Contains(child.Context.Member.RootId!.Value))
                    throw new InvalidOperationException("Another namespace owner prevents original remote deletion.");
            }
            else
            {
                if (retained.Phase != MirrorPulseNamespaceRetirementPhase.Prepared ||
                    !await catalog.CanResumeNamespaceRemoteRetirementAsync(retained.Intent, admitted).ConfigureAwait(false))
                    throw new InvalidOperationException("The original remote retirement cannot be redispatched from its retained state.");
                _ = await catalog.ValidateNamespaceRetirementExecutionAsync(retained.OperationId, admitted).ConfigureAwait(false);
            }
            CloudRemoteApplyResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                CloudItem item = change.ItemKind == CloudItemKind.Directory ? fileSystem.GetDirectory(change.RelativePath) : fileSystem.GetFile(change.RelativePath);
                var parentItem = fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parentItem, router, admitted).ConfigureAwait(false);
                var parentBefore = await InspectParentAsync(parent, parentItem, guard, admitted).ConfigureAwait(false);
                var snapshot = await item.InspectAsync(admitted).ConfigureAwait(false);
                var projection = await ReadItemAsync(change.ItemId!.Value, admitted).ConfigureAwait(false);
                RequireProjection(change, projection);
                if (retained is null)
                {
                    if (!snapshot.Exists || projection!.IsTombstone)
                        throw new InvalidDataException("A missing member cannot invent a remote deletion admission.");
                    await InspectChildAsync(child, item, null, admitted, requireRevision: false).ConfigureAwait(false);
                    RequireEmptyDirectory(child.Context.Member.IsDirectory, item);
                    string? revision = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).RemoteRevision;
                    var intent = new MirrorPulseNamespaceRetirementIntent(2, Guid.NewGuid(), memberId, child.Context.Member.RootId!.Value,
                        child.Context.Member.LocalObject, child.Context.Location.RelativePath, child.Context.Member.IsDirectory,
                        child.Context.Member.OwnerSid, execution.RoleSid.Value, DateTimeOffset.UtcNow)
                    { RemoteEntry = Reference(instance, batch, ordinal, options, child, parent, revision, projection.RemoteRevision, owningRoot.UniquenessKey) };
                    retained = await catalog.PrepareNamespaceRetirementAsync(intent, admitted).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
                }
                var original = retained.Intent; var remote = original.RemoteEntry!;
                if (snapshot.Exists)
                {
                    await InspectChildAsync(child, item, remote.NativeRemoteRevision, admitted).ConfigureAwait(false);
                    if (projection!.IsTombstone || projection.RemoteRevision != remote.OfficialRemoteRevision)
                        throw new InvalidDataException("The existing original delete child lost its retained public SDK revision.");
                    RequireEmptyDirectory(original.IsDirectory, item);
                }
                else if (retained.StartedAt is null || (projection!.IsTombstone ? projection.RemoteRevision != remote.RemoteRevision :
                    projection.RemoteRevision != remote.OfficialRemoteRevision))
                    throw new InvalidDataException("A missing original child requires its retained start and exact SDK recovery projection.");
                await RequirePrefixAsync(batch, ordinal, false, admitted).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await guard.InspectAsync(admitted).ConfigureAwait(false));
                if (retained.StartedAt is null)
                    retained = await catalog.RecordNamespaceRetirementStartAsync(original.OperationId, DateTimeOffset.UtcNow, remote.ItemId, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterStart, admitted).ConfigureAwait(false);
                result = await fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply, CancellationToken.None).ConfigureAwait(false);
                if (result.AppliedEntryCount <= ordinal) return;
                if (result.AppliedEntryCount != ordinal + 1)
                    throw new InvalidDataException("The SDK crossed more than one original retirement boundary.");
                await SettleAdmittedAsync(retained, instance, batch, ordinal, result, conflicts, parentItem, guard, parentBefore, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token).ConfigureAwait(false);
    }

    internal async ValueTask SettleRecordedAsync(MirrorPulseNamespaceRetirement retained, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        RequireOriginal(retained.Intent, instance, batch, ordinal, options);
        await actor.RunAsync(async admitted =>
        {
            retained = await catalog.ValidateNamespaceRetirementExecutionAsync(retained.OperationId, admitted).ConfigureAwait(false);
            if (retained.Phase is MirrorPulseNamespaceRetirementPhase.Completed or MirrorPulseNamespaceRetirementPhase.RemoteRejected) return true;
            var remote = retained.Intent.RemoteEntry!;
            RequireCurrentRoute(instance, batch.Changes[ordinal], remote.Child, remote.Parent);
            if (!await catalog.CanResumeNamespaceRemoteRetirementAsync(retained.Intent, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents original remote retirement settlement.");
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var parent = fileSystem.GetDirectory(remote.Parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, admitted).ConfigureAwait(false);
                var before = await InspectParentAsync(remote.Parent, parent, guard, admitted).ConfigureAwait(false);
                await SettleAdmittedAsync(retained, instance, batch, ordinal, null, conflicts, parent, guard, before, admitted).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task SettleAdmittedAsync(MirrorPulseNamespaceRetirement retained, InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyResult? result, MirrorPulseRemoteConflictProjector? conflicts, CloudDirectory parent,
        MirrorPulseWindowsNamespacePermissionLease guard, MirrorPulseNamespacePermissionObject before, CancellationToken token)
    {
        var original = retained.Intent; var remote = original.RemoteEntry!;
        if (retained.StartedAt is not { } started) throw new InvalidDataException("The original SDK deletion lost its before-native start.");
        await RequirePrefixAsync(batch, ordinal, true, token).ConfigureAwait(false);
        var records = await execution.RunNormalUserOperationAsync(async () => await fileSystem.ListRemoteConflictsAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        var matches = records.Where(record => record.Conflict.DetectedAt >= started && Matches(batch, ordinal, record.Conflict.Change)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The original SDK delete has ambiguous durable conflict records.");
        if (result is not null)
        {
            var entry = result.Entries.SingleOrDefault(value => value.ChangeId == remote.ChangeId);
            if (entry is null || entry.Status is not (CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied or CloudRemoteApplyEntryStatus.Conflict) ||
                (entry.Status == CloudRemoteApplyEntryStatus.Conflict) != (matches.Length == 1) ||
                result.ConflictIds.Count != matches.Length || matches.Length == 1 && result.ConflictIds[0] != matches[0].ConflictId)
                throw new InvalidDataException("The actual SDK delete result and its durable original outcome disagree.");
        }
        var projection = await ReadItemAsync(remote.ItemId, token).ConfigureAwait(false);
        RequireProjection(batch.Changes[ordinal], projection);
        CloudItem item = original.IsDirectory ? fileSystem.GetDirectory(original.RelativePath) : fileSystem.GetFile(original.RelativePath);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseNamespacePermissionVerification? child = null;
        if (matches.Length == 1)
        {
            if (!snapshot.Exists || projection!.IsTombstone || projection.RemoteRevision != remote.OfficialRemoteRevision)
                throw new InvalidDataException("A rejected SDK delete lost its original child or official revision.");
            child = await InspectChildAsync(remote.Child, item, remote.NativeRemoteRevision, token).ConfigureAwait(false);
            if (conflicts is not null) await conflicts.CaptureAsync(instance, matches[0].ConflictId, matches[0].Conflict, token).ConfigureAwait(false);
        }
        else if (snapshot.Exists || !projection!.IsTombstone || projection.RemoteRevision != remote.RemoteRevision ||
            snapshot.ItemId != remote.ItemId || !snapshot.IsTombstone)
            throw new InvalidDataException("Original SDK delete settlement requires actual native absence and the exact public item tombstone.");
        var parentAfter = await InspectParentAsync(remote.Parent, parent, guard, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, parentAfter);
        _ = await catalog.SettleNamespaceRemoteRetirementAsync(new(1, original.OperationId,
            matches.Length == 0 ? null : matches[0].ConflictId, matches.Length == 0 ? null : matches[0].Conflict.DetectedAt,
            DateTimeOffset.UtcNow, !snapshot.Exists, projection.IsTombstone, projection.RemoteRevision,
            Verification(parentAfter), child), token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSettlement, token).ConfigureAwait(false);
    }

    internal RootRegistration RequireCurrentRoute(InstanceId instance, CloudRemoteChange change,
        MirrorPulseNamespaceMemberProtectionSnapshot child, MirrorPulseNamespaceMemberProtectionSnapshot parent)
    {
        var route = router.ResolveCurrentPath(change.RelativePath); var root = router.GetRegistration(route.InstanceId, route.RootKey);
        if (route.InstanceId != instance || root.State != RootRegistrationState.Active || child.Context.Member.RootId != root.RootId ||
            parent.Context.Member.RootId != root.RootId || child.Context.Location.ParentMemberId != parent.Context.Member.MemberId ||
            child.Context.Location.RelativePath != change.RelativePath.Replace('\\', '/') ||
            child.Context.Member.IsDirectory != (change.ItemKind == CloudItemKind.Directory) ||
            child.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Protection.RoleSid != execution.RoleSid.Value ||
            child.Context.Member.OwnerSid != execution.OwnerSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidDataException("Remote retirement requires its current active root, original direct parent and protected role.");
        return root;
    }

    internal static async Task<MirrorPulseNamespacePermissionObject> InspectParentAsync(MirrorPulseNamespaceMemberProtectionSnapshot original,
        CloudDirectory item, MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
        var facts = await guard.InspectAsync(token).ConfigureAwait(false);
        RequireObject(original, facts);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await item.InspectAsync(token).ConfigureAwait(false),
            original.Identity, original.Observation is not null);
        return facts;
    }

    internal async Task<MirrorPulseNamespacePermissionVerification> InspectChildAsync(MirrorPulseNamespaceMemberProtectionSnapshot original,
        CloudItem item, string? revision, CancellationToken token, bool requireRevision = true)
    {
        await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
        var facts = await guard.InspectAsync(token).ConfigureAwait(false); RequireObject(original, facts);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, original.Identity, original.Observation is not null);
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            requireRevision && CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).RemoteRevision != revision)
            throw new InvalidDataException("The original SDK delete child lost its native binding, placeholder or retained revision.");
        return Verification(facts);
    }

    private static void RequireEmptyDirectory(bool directory, CloudItem item)
    {
        if (directory && Directory.EnumerateFileSystemEntries(item.FullPath).Any())
            throw new NotSupportedException("A remote directory containing native children requires its complete recursive retirement plan.");
    }

    private static void RequireObject(MirrorPulseNamespaceMemberProtectionSnapshot original, MirrorPulseNamespacePermissionObject actual)
    {
        if (actual.LocalObject != original.Context.Member.LocalObject || actual.OwnerSid != original.Context.Member.OwnerSid ||
            actual.RootId != original.Context.Member.RootId || actual.RelativePath != original.Context.Location.RelativePath ||
            actual.IsDirectory != original.Context.Member.IsDirectory || actual.LinkCount != 1 || actual.Dacl != original.Context.Verification.Dacl)
            throw new InvalidDataException("The original retirement object, position, owner, descriptor or single-link constraint changed.");
    }

    internal async Task<CloudItemState?> ReadItemAsync(Guid item, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var result = await transaction.Items.GetByItemIdAsync(item, token).ConfigureAwait(false);
        await transaction.RollbackAsync(token).ConfigureAwait(false); return result;
    }

    private static void RequireProjection(CloudRemoteChange change, [NotNull] CloudItemState? item)
    {
        if (item is null || item.ItemId != change.ItemId || item.RemoteId != change.RemoteId || item.Kind != change.ItemKind ||
            item.RelativePath.Replace('\\', '/') != change.RelativePath.Replace('\\', '/'))
            throw new InvalidDataException("The original SDK delete does not identify its actual public item projection.");
    }

    internal async Task RequirePrefixAsync(CloudRemoteChangeBatch batch, int ordinal, bool applied, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
        int expected = ordinal + (applied ? 1 : 0);
        if (progress is null ? expected != 0 : progress.AppliedEntryCount != expected || progress.TotalEntryCount != batch.Changes.Count ||
            !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) || progress.LastAppliedChangeId != (expected == 0 ? null : batch.Changes[expected - 1].ChangeId) ||
            !progress.Cursor.Span.SequenceEqual(MirrorPulseRemoteBirthBatchCoordinator.PrefixCursor(batch, expected).Span))
            throw new InvalidDataException("The original remote retirement lost its exact SDK batch prefix.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }

    internal static CloudRemoteChange RequireChange(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(options);
        if (instance.Value == Guid.Empty || !batch.BatchId.StartsWith(instance + "/", StringComparison.Ordinal) ||
            batch.Changes.Count > 65_536 || ordinal < 0 || ordinal >= batch.Changes.Count)
            throw new InvalidDataException("Remote retirement requires a bounded original instance and batch entry.");
        var change = batch.Changes[ordinal];
        if (change.Kind != CloudRemoteChangeKind.Delete || change.ItemId is null || change.ItemId == Guid.Empty || change.PreviousRelativePath is not null ||
            options.ConflictResolver is not null || !options.PreserveUnsynchronizedLocalContent || options.MaximumEntries is < 1 or > 65_536 ||
            options.EchoSuppressionObservationCount is < 1 or > 64 || options.EchoSuppressionLifetime <= TimeSpan.Zero || options.EchoSuppressionLifetime > TimeSpan.FromHours(1))
            throw new NotSupportedException("Original remote deletion requires an explicit identity and durable preservation policy without transient resolution.");
        return change;
    }

    internal static void RequireOriginal(MirrorPulseNamespaceRetirementIntent original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        if (original.Version != 2 || original.RemoteEntry is not { } remote || original.RelativePath != change.RelativePath.Replace('\\', '/') ||
            original.IsDirectory != (change.ItemKind == CloudItemKind.Directory) ||
            !MirrorPulseRemoteLibraryProvenance.IsSupported(remote.PackageVersion, remote.PackageSource) ||
            remote != (Reference(instance, batch, ordinal, options, remote.Child, remote.Parent, remote.NativeRemoteRevision, remote.OfficialRemoteRevision, remote.RootKey) with
            { PackageVersion = remote.PackageVersion, PackageSource = remote.PackageSource }))
            throw new InvalidDataException("The original remote retirement changed its immutable batch, identity, path or public policy.");
    }

    internal static bool Matches(CloudRemoteChangeBatch batch, int ordinal, CloudRemoteChange change)
    {
        if (change.ChangeId != batch.Changes[ordinal].ChangeId) return false;
        var entries = batch.Changes.ToArray(); entries[ordinal] = change;
        return new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, entries, batch.FinalCursor).Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span);
    }

    internal static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject actual) =>
        new(actual.LocalObject, actual.OwnerSid, actual.Dacl, actual.ObservedAt);

    internal static MirrorPulseNamespaceRemoteRetirementReference Reference(InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options, MirrorPulseNamespaceMemberProtectionSnapshot child,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, string? nativeRevision, string? officialRevision, string? rootKey = null)
    {
        var change = batch.Changes[ordinal];
        return new(rootKey is null ? 1 : 2, instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), batch.Changes.Count, ordinal, change.ChangeId,
            change.ItemId!.Value, change.RemoteId, change.PreviousRemoteRevision, change.RemoteRevision, nativeRevision, officialRevision,
            child, parent, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit,
            new(options.PreserveUnsynchronizedLocalContent, options.SuppressLocalEcho, options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount))
        { RootKey = rootKey };
    }
}
