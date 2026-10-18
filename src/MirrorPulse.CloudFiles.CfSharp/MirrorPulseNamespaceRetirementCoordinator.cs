using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceRetirementCheckpoint { AfterNativeDeletion, AfterNativeReceipt }

/// <summary>Executes one protected leaf deletion and settles its durable member name.</summary>
/// <remarks>
/// The caller supplies a shared actor and the Host's execution role. Ordinary filesystem
/// deletion remains denied by the birth/parent DACL. A directory is accepted only when the
/// native filesystem itself confirms it is empty; registered descendants are rejected during
/// catalog admission. Native success and membership settlement are separate receipts.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceRetirementCoordinator(CloudFileSystem fileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal static MirrorPulseNamespaceRetirementBatch RequireControlledBatchReceipt(MirrorPulseNamespaceRetirementBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.RemoteEntry is not null)
            throw new NotSupportedException("Original SDK directory outcomes cannot use the controlled retirement status contract.");
        return batch;
    }

    internal Func<MirrorPulseNamespaceRetirementCheckpoint, ValueTask>? Checkpoint { get; init; }
    internal Func<CloudItemSnapshot, CancellationToken, ValueTask>? BeforeNativeDeletion { get; init; }

    public ValueTask<MirrorPulseNamespaceRetirement> PrepareAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken = default) => actor.RunAsync(token => PrepareAdmittedAsync(operationId, memberId, token), cancellationToken);

    private async ValueTask<MirrorPulseNamespaceRetirement> PrepareAdmittedAsync(Guid operationId, Guid memberId, CancellationToken token,
        Guid? batchOperationId = null)
    {
        actor.AssertAccess();
        if (operationId == Guid.Empty || memberId == Guid.Empty) throw new ArgumentException("Retirement requires operation and member IDs.");
        var replay = await catalog.ReadNamespaceRetirementAsync(operationId, token).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.Intent.MemberId != memberId || replay.Intent.BatchOperationId != batchOperationId)
                throw new InvalidOperationException("A retirement operation is immutable.");
            return replay;
        }
        var member = await catalog.ReadNamespaceMemberAsync(memberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The namespace member is not registered.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(memberId, token).ConfigureAwait(false);
        if (member.RootId is not { } rootId) throw new InvalidOperationException("Retirement requires a managed root member.");
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        return await catalog.PrepareNamespaceRetirementAsync(new(1, operationId, memberId, rootId, member.LocalObject,
            location.RelativePath, member.IsDirectory, member.OwnerSid, proof.RoleSid, DateTimeOffset.UtcNow)
        { BatchOperationId = batchOperationId }, token).ConfigureAwait(false);
    }

    public ValueTask<MirrorPulseNamespaceRetirementBatch> PrepareBatchAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken = default) => actor.RunAsync(async token =>
        await catalog.PrepareNamespaceRetirementBatchAsync(operationId, memberId, token).ConfigureAwait(false), cancellationToken);

    /// <summary>Deletes a sealed subtree leaf-first and resumes using each original member receipt.</summary>
    public ValueTask<MirrorPulseNamespaceRetirementBatch> ExecuteBatchAsync(Guid operationId,
        CancellationToken cancellationToken = default) => actor.RunAsync(async token =>
        {
            var batch = await catalog.ReadNamespaceRetirementBatchAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement batch is missing.");
            if (batch.RemoteEntry is not null) throw new InvalidOperationException("The original SDK batch owns this recursive remote deletion.");
            if (batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.Completed) return batch;
            if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared)
                throw new InvalidOperationException("The retirement batch requires explicit recovery.");
            foreach (var entry in batch.Entries.OrderByDescending(entry => entry.Depth).ThenBy(entry => entry.MemberId))
            {
                token.ThrowIfCancellationRequested();
                Guid memberOperation = batch.MemberOperationId(entry.MemberId);
                _ = await PrepareAdmittedAsync(memberOperation, entry.MemberId, token, batch.OperationId).ConfigureAwait(false);
                _ = await ExecuteAdmittedAsync(memberOperation, token).ConfigureAwait(false);
            }
            return await catalog.CompleteNamespaceRetirementBatchAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask<MirrorPulseNamespaceRetirement> ExecuteAsync(Guid operationId,
        CancellationToken cancellationToken = default) => actor.RunAsync(token => ExecuteAdmittedAsync(operationId, token), cancellationToken);

    private async ValueTask<MirrorPulseNamespaceRetirement> ExecuteAdmittedAsync(Guid operationId, CancellationToken token)
    {
        actor.AssertAccess();
        var current = await catalog.ValidateNamespaceRetirementExecutionAsync(operationId, token).ConfigureAwait(false);
        if (current.Intent.RemoteEntry is not null)
            throw new InvalidOperationException("The original SDK batch owns this remote deletion; controlled deletion cannot pre-execute it.");
        if (current.Phase == MirrorPulseNamespaceRetirementPhase.Completed) return current;
        var member = await catalog.ReadNamespaceMemberAsync(current.Intent.MemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retired member provenance is missing.");
        if (member.RootId != current.Intent.RootId || member.LocalObject != current.Intent.LocalObject || member.OwnerSid != current.Intent.OwnerSid)
            throw new InvalidDataException("The retirement member provenance changed.");
        CloudItem item = member.IsDirectory ? fileSystem.GetDirectory(current.Intent.RelativePath) : fileSystem.GetFile(current.Intent.RelativePath);
        CloudDirectory parent = item.Parent ?? throw new InvalidDataException("A controlled retirement requires a direct parent.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        var parentMember = location.ParentMemberId is { } parentId
            ? await catalog.ReadNamespaceMemberAsync(parentId, token).ConfigureAwait(false)
            : null;
        if (parentMember is null) throw new InvalidDataException("The retirement parent member is missing.");
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        if (parentProof.RoleSid != current.Intent.RoleSid || childProof.RoleSid != current.Intent.RoleSid ||
            childProof.OwnerSid != current.Intent.OwnerSid || childProof.RootId != current.Intent.RootId)
            throw new InvalidOperationException("The retirement parent protection role is not retained.");
        await ExecuteUnderNamespaceRoleAsync(current, member, item, parent, parentProof, childProof, token).ConfigureAwait(false);
        return await catalog.ReadNamespaceRetirementAsync(operationId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("The retirement receipt disappeared after execution.");
    }

    private async Task ExecuteUnderNamespaceRoleAsync(MirrorPulseNamespaceRetirement current,
        MirrorPulseNamespaceMemberRegistration member, CloudItem item, CloudDirectory parent,
        MirrorPulseNamespaceProtectionProof parentProof, MirrorPulseNamespaceProtectionProof childProof, CancellationToken token)
    {
        await execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(current.Intent);
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            var parentFacts = await parentLease.InspectAsync(token).ConfigureAwait(false);
            if (parentFacts.LocalObject != parentProof.Verification.LocalObject || parentFacts.OwnerSid != parentProof.OwnerSid ||
                parentFacts.Dacl != parentProof.Verification.Dacl || parentFacts.LinkCount != 1)
                throw new InvalidDataException("The retirement parent changed before the controlled deletion.");
            var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            if (current.Phase == MirrorPulseNamespaceRetirementPhase.NativeDeleted && snapshot.Exists)
                throw new InvalidDataException("A native-deleted retirement target reappeared before membership settlement.");
            if (!snapshot.Exists && current.Phase == MirrorPulseNamespaceRetirementPhase.Prepared && current.StartedAt is null)
                throw new InvalidDataException("A missing target without a retained native start cannot be adopted as a deletion.");
            if (snapshot.Exists)
            {
                if (snapshot.LocalBinding is not { } binding ||
                    new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != current.Intent.LocalObject ||
                    snapshot.Kind != (member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File) || snapshot.IsTombstone ||
                    current.StartedItemId is { } originalItemId && snapshot.ItemId != originalItemId)
                    throw new InvalidDataException("The retirement target changed before deletion.");
                await using (var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false))
                {
                    var facts = await childLease.InspectAsync(token).ConfigureAwait(false);
                    if (facts.LocalObject != current.Intent.LocalObject || facts.OwnerSid != current.Intent.OwnerSid ||
                        facts.Dacl != childProof.Verification.Dacl || facts.RootId != current.Intent.RootId || facts.LinkCount != 1)
                        throw new InvalidDataException("The retirement target lost its original protected descriptor or binding.");
                }
                if (current.StartedAt is null)
                    current = await catalog.RecordNamespaceRetirementStartAsync(current.OperationId, DateTimeOffset.UtcNow,
                        snapshot.ItemId, token).ConfigureAwait(false);
                if (BeforeNativeDeletion is not null) await BeforeNativeDeletion(snapshot, token).ConfigureAwait(false);
                await item.DeleteAsync(token).ConfigureAwait(false);
                if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceRetirementCheckpoint.AfterNativeDeletion).ConfigureAwait(false);
            }
            var deleted = await item.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            if (deleted.Exists)
                throw new IOException("The controlled retirement target still exists after deletion.");
            if (current.StartedItemId is { } retainedItemId && (deleted.ItemId != retainedItemId || !deleted.IsTombstone))
                throw new InvalidDataException("Native deletion has not settled the original public CfSharp item tombstone.");
            if (current.Phase == MirrorPulseNamespaceRetirementPhase.Prepared)
                _ = await catalog.RecordNamespaceRetirementNativeDeletedAsync(current.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceRetirementCheckpoint.AfterNativeReceipt).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentFacts,
                await parentLease.InspectAsync(CancellationToken.None).ConfigureAwait(false));
            _ = await catalog.CompleteNamespaceRetirementAsync(current.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static void RequireCaller(MirrorPulseNamespaceRetirementIntent intent)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != intent.OwnerSid || !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(intent.RoleSid)))
            throw new UnauthorizedAccessException("Controlled retirement requires the original owner and protected namespace role.");
    }
}
