using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRemoteBirthCheckpoint { AfterAdmission, AfterStart, AfterObservation, AfterRegistration }

/// <summary>Executes an original SDK-owned birth under its protected parent and registers the first observed object.</summary>
/// <remarks>Unknown starts without first native observation remain closed. The original SDK batch owns creation;
/// this boundary never pre-creates a placeholder, invents an outcome or hydrates content.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteBirthBatchCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteBirthCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    internal async ValueTask<CloudRemoteApplyResult> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options, CancellationToken token)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        var birth = await catalog.ReadNamespaceRemoteBirthAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        Guid parentId;
        if (birth is null)
        {
            int separator = change.RelativePath.LastIndexOf('\\');
            if (separator <= 0) throw new InvalidDataException("Remote birth cannot replace a configured first-level root.");
            parentId = (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
                .InspectAsync(change.RelativePath[..separator], true, token).ConfigureAwait(false)).MemberId;
        }
        else { RequireOriginal(birth, instance, batch, ordinal, options); parentId = birth.RemoteEntry!.ParentMemberId; }
        return await actor.RunAsync(async admitted =>
        {
            var parentMember = await catalog.ReadNamespaceMemberAsync(parentId, admitted).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote birth parent is missing.");
            var location = await catalog.ReadNamespaceMemberLocationAsync(parentId, admitted).ConfigureAwait(false);
            var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, admitted).ConfigureAwait(false);
            var route = router.Resolve(change.RelativePath, new CloudPlaceholderIdentity(change.ItemId!.Value, change.RemoteId, change.RemoteRevision).Encode());
            var root = router.GetRegistration(route.InstanceId, route.RootKey);
            if (route.InstanceId != instance || root.State != RootRegistrationState.Active || parentMember.RootId != root.RootId ||
                proof.RoleSid != execution.RoleSid.Value || proof.OwnerSid != execution.OwnerSid.Value || !parentMember.IsDirectory ||
                proof.RelativePath != location.RelativePath || change.RelativePath.Replace('\\', '/') != location.RelativePath + "/" + Path.GetFileName(change.RelativePath))
                throw new InvalidDataException("Remote birth requires its original active root and current protected direct parent.");
            if (birth is not null && (location.CurrentPositionOperationId != birth.RemoteEntry!.ParentPositionOperationId ||
                proof.PermissionOperationId != birth.ParentPermissionOperationId || proof.BornParent != birth.BornParent || birth.RoleSid != execution.RoleSid.Value))
                throw new InvalidOperationException("The original remote birth requires its admitted parent placement and namespace role.");
            var parent = fileSystem.GetDirectory(location.RelativePath);
            CloudItem item = change.ItemKind == CloudItemKind.Directory ? fileSystem.GetDirectory(change.RelativePath) : fileSystem.GetFile(change.RelativePath);
            if (birth is null ? (await catalog.ReadNamespaceDispatchFencedRootsAsync(admitted).ConfigureAwait(false)).Contains(root.RootId) :
                !await catalog.CanResumeNamespaceRemoteBirthAsync(birth, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another durable namespace owner prevents this remote birth.");
            CloudRemoteApplyResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, admitted).ConfigureAwait(false);
                var original = birth ?? new MirrorPulseNamespaceBirthIntent(3, Guid.NewGuid(), root.RootId,
                    proof.BornParent is null ? parentMember.OriginId : Guid.Empty, proof.PermissionOperationId, parentMember.LocalObject,
                    change.RelativePath.Replace('\\', '/'), change.ItemKind == CloudItemKind.Directory,
                    MirrorPulseNamespaceBirthOrigin.RemotePopulation, execution.RoleSid.Value, DateTimeOffset.UtcNow)
                { BornParent = proof.BornParent, RemoteEntry = Reference(instance, batch, ordinal, options, parentId, location.CurrentPositionOperationId) };
                var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(original, parent, proof, guard, admitted).ConfigureAwait(false);
                if (birth is null)
                {
                    if ((await item.InspectAsync(admitted).ConfigureAwait(false)).Exists)
                        throw new NotSupportedException("An occupied remote upsert requires its replacement or conflict lifecycle.");
                    original = await catalog.PrepareNamespaceBirthAsync(original, admitted).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteBirthCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
                }
                var plan = new MirrorPulseNamespaceBirthPlan(1, original.OperationId, change.ItemId.Value,
                    change.RemoteId, change.RemoteRevision, original.PreparedAt);
                await catalog.PrepareNamespaceBirthPlanAsync(plan, admitted).ConfigureAwait(false);
                var start = await catalog.ReadNamespaceBirthStartAsync(original.OperationId, admitted).ConfigureAwait(false);
                if (start is not null)
                    throw new InvalidOperationException("A started remote birth requires first-object outcome settlement before SDK redispatch.");
                await RequirePrefixAsync(batch, ordinal, false, admitted).ConfigureAwait(false);
                if ((await item.InspectAsync(admitted).ConfigureAwait(false)).Exists)
                    throw new IOException("The original remote birth destination became occupied.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(admitted).ConfigureAwait(false));
                await catalog.RecordNamespaceBirthStartAsync(new(1, original.OperationId, DateTimeOffset.UtcNow), admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteBirthCheckpoint.AfterStart, admitted).ConfigureAwait(false);
                result = await fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false);
                if (result.AppliedEntryCount <= ordinal) return; // Preserve actual SDK failure/retry and the original intent.
                var outcome = result.Entries.SingleOrDefault(entry => entry.ChangeId == change.ChangeId);
                if (result.AppliedEntryCount != ordinal + 1 || outcome is null ||
                    outcome.Status is not (CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied))
                    throw new InvalidOperationException("Remote birth requires original conflict settlement before membership or cursor publication.");
                await RequirePrefixAsync(batch, ordinal, true, CancellationToken.None).ConfigureAwait(false);
                await RequireItemProjectionAsync(original, CancellationToken.None).ConfigureAwait(false);
                await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, original.OperationId, item, router, CancellationToken.None).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteBirthCheckpoint.AfterObservation, CancellationToken.None).ConfigureAwait(false);
                await RegisterAsync(original, item, parent, location, before, guard, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token).ConfigureAwait(false);
    }

    internal async ValueTask SettleRecordedAsync(MirrorPulseNamespaceBirthIntent birth, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, CancellationToken token)
    {
        RequireOriginal(birth, instance, batch, ordinal, options);
        await actor.RunAsync(async admitted =>
        {
            var remote = birth.RemoteEntry!;
            if (await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, admitted).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Committed SDK progress cannot invent a missing first native birth observation.");
            var parentMember = await catalog.ReadNamespaceMemberAsync(remote.ParentMemberId, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote birth parent membership is missing.");
            var location = await catalog.ReadNamespaceMemberLocationAsync(remote.ParentMemberId, admitted).ConfigureAwait(false);
            var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, admitted).ConfigureAwait(false);
            var route = router.ResolveCurrentPath(birth.RelativePath); var root = router.GetRegistration(route.InstanceId, route.RootKey);
            if (route.InstanceId != instance || root.RootId != birth.RootId || root.State != RootRegistrationState.Active ||
                proof.RoleSid != birth.RoleSid || proof.RoleSid != execution.RoleSid.Value || proof.OwnerSid != execution.OwnerSid.Value ||
                proof.PermissionOperationId != birth.ParentPermissionOperationId || proof.BornParent != birth.BornParent ||
                location.CurrentPositionOperationId != remote.ParentPositionOperationId)
                throw new InvalidDataException("Original birth settlement requires its unchanged current parent, role and root.");
            var parent = fileSystem.GetDirectory(location.RelativePath);
            CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
            if (await catalog.ReadNamespaceMemberAsync(birth.OperationId, admitted).ConfigureAwait(false) is null &&
                !await catalog.CanResumeNamespaceRemoteBirthAsync(birth, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents original birth settlement.");
            await execution.RunNamespaceOperationAsync(async () =>
            {
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, admitted).ConfigureAwait(false);
                var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent, proof, guard, admitted).ConfigureAwait(false);
                await RequirePrefixAsync(batch, ordinal, true, admitted).ConfigureAwait(false);
                await RequireItemProjectionAsync(birth, admitted).ConfigureAwait(false);
                // This compares the current object against the retained first binding.
                await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, admitted).ConfigureAwait(false);
                await RegisterAsync(birth, item, parent, location, before, guard, admitted).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task RegisterAsync(MirrorPulseNamespaceBirthIntent birth, CloudItem item, CloudDirectory parent,
        MirrorPulseNamespaceMemberLocation location, MirrorPulseNamespacePermissionObject before,
        MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
        var observed = await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The SDK birth lost its first object observation.");
        var prior = await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId, token).ConfigureAwait(false);
        var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, prior?.ProtectionId ?? Guid.NewGuid(),
            birth.OperationId, item, parent, router, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(token).ConfigureAwait(false));
        var member = new MirrorPulseNamespaceMemberRegistration(location.CurrentPositionOperationId is null ? 1 : 2, birth.OperationId,
            birth.RemoteEntry!.ParentMemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, birth.OperationId, protection.ProtectionId,
            birth.RootId, birth.RelativePath, observed.LocalObject, birth.IsDirectory, observed.OwnerSid, DateTimeOffset.UtcNow)
        { FirstParentPositionOperationId = location.CurrentPositionOperationId };
        var historical = await catalog.ReadNamespaceMemberAsync(member.MemberId, token).ConfigureAwait(false);
        await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, historical ?? member, item, parent, router, actor, token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteBirthCheckpoint.AfterRegistration, token).ConfigureAwait(false);
    }

    private async Task RequireItemProjectionAsync(MirrorPulseNamespaceBirthIntent birth, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var remote = birth.RemoteEntry!;
        var item = await transaction.Items.GetByItemIdAsync(remote.ItemId, token).ConfigureAwait(false);
        if (item is null || item.IsTombstone || item.RemoteId != remote.RemoteId || item.RemoteRevision != remote.RemoteRevision ||
            item.RelativePath.Replace('\\', '/') != birth.RelativePath || (item.Kind == CloudItemKind.Directory) != birth.IsDirectory)
            throw new InvalidDataException("The original SDK birth item projection does not match its admitted identity and path.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }

    private async Task RequirePrefixAsync(CloudRemoteChangeBatch batch, int ordinal, bool applied, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
        int expected = ordinal + (applied ? 1 : 0);
        if (progress is null ? expected != 0 : progress.AppliedEntryCount != expected || progress.TotalEntryCount != batch.Changes.Count ||
            !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) ||
            progress.LastAppliedChangeId != (expected == 0 ? null : batch.Changes[expected - 1].ChangeId) ||
            !progress.Cursor.Span.SequenceEqual(PrefixCursor(batch, expected).Span))
            throw new InvalidDataException("The original birth does not own this exact SDK batch prefix.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }

    internal static ReadOnlyMemory<byte> PrefixCursor(CloudRemoteChangeBatch batch, int count)
    {
        if (count < 0 || count > batch.Changes.Count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == batch.Changes.Count && (count == 0 || batch.Changes[count - 1].CursorAfter.IsEmpty)) return batch.FinalCursor;
        for (int index = count - 1; index >= 0; index--)
            if (!batch.Changes[index].CursorAfter.IsEmpty) return batch.Changes[index].CursorAfter;
        return batch.InitialCursor;
    }

    internal static CloudRemoteChange RequireChange(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(options);
        if (instance.Value == Guid.Empty || !batch.BatchId.StartsWith(instance + "/", StringComparison.Ordinal) ||
            ordinal < 0 || ordinal >= batch.Changes.Count || batch.Changes.Count > 65_536)
            throw new InvalidDataException("Remote birth requires its original bounded instance batch entry.");
        var change = batch.Changes[ordinal];
        if (change.Kind is not (CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert) || change.ItemId is null ||
            change.PreviousRelativePath is not null || change.PreviousRemoteRevision is not null)
            throw new NotSupportedException("Remote birth requires a new explicit-identity upsert, without replacement or movement semantics.");
        if (options.ConflictResolver is not null || options.MaximumEntries is < 1 or > 65_536 ||
            options.EchoSuppressionObservationCount is < 1 or > 64 || options.EchoSuppressionLifetime <= TimeSpan.Zero || options.EchoSuppressionLifetime > TimeSpan.FromHours(1))
            throw new NotSupportedException("Remote birth requires a durable bounded apply policy without a transient resolver.");
        return change;
    }

    internal static void RequireOriginal(MirrorPulseNamespaceBirthIntent birth, InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        if (birth.RemoteEntry is not { } remote || birth.Version != 3 || birth.RelativePath != change.RelativePath.Replace('\\', '/') ||
            birth.IsDirectory != (change.ItemKind == CloudItemKind.Directory) ||
            remote != Reference(instance, batch, ordinal, options, remote.ParentMemberId, remote.ParentPositionOperationId))
            throw new InvalidDataException("The original remote birth changed its immutable batch, identity, path or public apply policy.");
    }

    private static MirrorPulseNamespaceRemoteBirthReference Reference(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal,
        CloudRemoteApplyOptions options, Guid parent, Guid? placement)
    {
        var change = batch.Changes[ordinal];
        return new(1, instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), batch.Changes.Count, ordinal,
            change.ChangeId, change.ItemId!.Value, change.RemoteId, change.RemoteRevision, parent, placement, "0.1.0-preview.6",
            "3caa382e341d260543d3afc05e25fa034fb32e16", new(options.PreserveUnsynchronizedLocalContent, options.SuppressLocalEcho,
                options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount));
    }
}
