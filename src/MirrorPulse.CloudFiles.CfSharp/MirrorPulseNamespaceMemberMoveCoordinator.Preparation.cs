using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Retains a first file relocation after fresh inspection, without issuing a native move.</summary>
    /// <remarks>
    /// The caller drains previous instance work before admission. Reusing an operation ID returns
    /// its original admission only when the requested member, parent and name agree; this replay
    /// never refreshes protection or authorizes native execution. ExecuteAsync rechecks everything.
    /// </remarks>
    public async ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string targetName, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || memberId == Guid.Empty || targetParentMemberId == Guid.Empty)
            throw new ArgumentException("Operation, member and target parent IDs are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (targetName.Contains('/') || targetName.Contains('\\'))
            throw new ArgumentException("The destination requires one direct child name.", nameof(targetName));
        return await _actor.RunAsync(async token =>
        {
            _actor.AssertAccess();
            var replay = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.MemberId != memberId || replay.Target.ParentMemberId != targetParentMemberId || TargetName(replay) != targetName)
                    throw new InvalidOperationException("An operation ID already identifies a different move request.");
                return replay;
            }
            var member = await _catalog.ReadNamespaceMemberAsync(memberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The source member is not registered.");
            if (member.IsDirectory)
                throw new NotSupportedException("Directory preparation requires public subtree proof and product subtree settlement.");
            var sourceParent = await _catalog.ReadNamespaceMemberAsync(member.ParentMemberId!.Value, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original source parent is not registered.");
            var targetParent = await _catalog.ReadNamespaceMemberAsync(targetParentMemberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The target parent is not registered.");
            if (!targetParent.IsDirectory || targetParent.RootId != member.RootId || targetParent.OwnerSid != member.OwnerSid)
                throw new ArgumentException("The target parent must be a protected directory in the same managed root.", nameof(targetParentMemberId));
            CloudFile source = _fileSystem.GetFile(member.RelativePath);
            CloudDirectory sourceDirectory = source.Parent ?? throw new InvalidDataException("A member requires a direct source parent.");
            CloudDirectory targetDirectory = _fileSystem.GetDirectory(targetParent.RelativePath);
            // Reuse CfSharp's public name validation before retaining any product intent.
            CloudFile target = targetDirectory.GetFile(targetName);
            var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, member, token).ConfigureAwait(false);
            var sourceProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, sourceParent, token).ConfigureAwait(false);
            var targetProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, targetParent, token).ConfigureAwait(false);
            MirrorPulseNamespaceMemberMoveIntent prepared = null!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceDirectory, _router, token).ConfigureAwait(false);
                await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetDirectory, _router, token).ConfigureAwait(false);
                await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(source, _router, token).ConfigureAwait(false);
                var facts = await childLease.InspectAsync(token).ConfigureAwait(false);
                var snapshot = await source.InspectAsync(token).ConfigureAwait(false);
                if (!snapshot.IsPlaceholder || snapshot.PlaceholderIdentity.IsEmpty)
                    throw new InvalidDataException("The source requires its retained public placeholder identity.");
                var identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
                var intent = new MirrorPulseNamespaceMemberMoveIntent(1, operationId, memberId, childProof.ProtectionId,
                    member.RootId!.Value, member.LocalObject, identity.ItemId, identity.RemoteId, member.OwnerSid, _execution.RoleSid.Value,
                    new(sourceParent.MemberId, sourceParent.LocalObject, sourceProof.ProtectionId, source.RelativePath.Replace('\\', '/')),
                    new(targetParent.MemberId, targetParent.LocalObject, targetProof.ProtectionId, target.RelativePath.Replace('\\', '/')),
                    DateTimeOffset.UtcNow);
                RequireCaller(intent);
                MirrorPulseNamespaceMemberMoveInspection.RequireChild(facts, snapshot, childProof, intent.Source.RelativePath, intent);
                var sourceBefore = await InspectParentAsync(sourceDirectory, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
                var targetBefore = await InspectParentAsync(targetDirectory, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
                bool sameName = string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase);
                if (!sameName && (await target.InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new IOException("The requested move destination is occupied.");
                RequireProjection(await ReadProjectionAsync(memberId, token).ConfigureAwait(false), intent, intent.Source.RelativePath);
                if (!await HasUnchangedProofsAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Protection changed during move preparation.");
                MirrorPulseNamespaceMemberMoveInspection.RequireChild(await childLease.InspectAsync(token).ConfigureAwait(false),
                    await source.InspectAsync(token).ConfigureAwait(false), childProof, intent.Source.RelativePath, intent);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(token).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(token).ConfigureAwait(false));
                _actor.AssertAccess();
                prepared = await _catalog.PrepareNamespaceMemberMoveAsync(intent, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return prepared;
        }, cancellationToken).ConfigureAwait(false);
    }
}
