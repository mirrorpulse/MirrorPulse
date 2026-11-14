using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Resumes a catalog-owned Move using its original Worker request and target identities.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseJournalDirectoryMoveRecovery(MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, MirrorPulseRootRouter router, CloudLocalChangeFeed feed,
    IMirrorPulseWorkerMutationTransport worker, IMirrorPulseWorkerDirectoryPageSource directories,
    MirrorPulseJournalDirectoryMoveIdentityPatch identityPatch)
{
    internal async ValueTask<bool> ResumeAsync(Guid operationId, CancellationToken token, bool mayRequestSource = true)
    {
        var work = await PrepareProjectionAsync(operationId, mayRequestSource, token).ConfigureAwait(false);
        if (work is null) return false;
        foreach (var member in work.Admission.Members)
            await work.ProjectMemberAsync(member.MemberId, token).ConfigureAwait(false);
        await work.AcknowledgeAsync(token).ConfigureAwait(false);
        return true;
    }

    internal async ValueTask<ProjectionRecovery?> PrepareProjectionAsync(Guid operationId, bool mayRequestSource, CancellationToken token)
    {
        var retained = await catalog.ReadJournalDirectoryMoveAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original directory Move was not admitted.");
        if (retained.Mutation.State == MirrorPulseMutationState.Acknowledged) return new(this, retained);
        var admission = retained.Admission;
        var intent = retained.Mutation.Intent;
        if (router.FindRegistration(admission.RootId) is not { State: RootRegistrationState.Active } root ||
            root.InstanceId != intent.InstanceId || root.UniquenessKey != intent.RootKey) return null;
        if (!mayRequestSource && (retained.Mutation.State == MirrorPulseMutationState.Executing || retained.Targets is null))
            return null;
        await MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.DirectoryMoveOriginal,
            () => RequireOriginalAsync(retained, token)).ConfigureAwait(false);
        if (retained.Mutation.State == MirrorPulseMutationState.Executing)
        {
            if (!worker.SupportsDurableDirectoryMove(intent.InstanceId)) return null;
            var request = new MirrorPulseWorkerMoveRequest(intent.InstanceId, intent.PreviousRelativePath!, intent.RelativePath,
                intent.ExpectedRevision, true, operationId, intent.PreviousRootKey ?? intent.RootKey, intent.RootKey)
            { RequireDurableDirectoryMove = true };
            string revision = await worker.MoveAsync(request, token).ConfigureAwait(false);
            retained = await catalog.AcceptJournalDirectoryMoveAsync(operationId, revision, token).ConfigureAwait(false);
        }
        if (retained.Targets is null)
        {
            var plan = await MirrorPulseJournalDirectoryMoveProjectionPlan.CreateAsync(admission, retained.Mutation.AcceptedRevision!,
                router, directories, token).ConfigureAwait(false);
            var targets = plan.Select(value => new MirrorPulseDirectoryMoveTarget(value.Original.MemberId, value.TargetIdentity.ItemId,
                value.TargetIdentity.RemoteId, value.TargetIdentity.RemoteRevision)).ToArray();
            retained = await catalog.SaveJournalDirectoryMoveTargetsAsync(operationId, targets, token).ConfigureAwait(false);
        }
        return new(this, retained);
    }

    internal sealed class ProjectionRecovery(MirrorPulseJournalDirectoryMoveRecovery owner, MirrorPulseJournalDirectoryMove initial)
    {
        private readonly Dictionary<Guid, int> _ordinals = initial.Admission.Members
            .Select((member, index) => (member.MemberId, Index: index)).ToDictionary(member => member.MemberId, member => member.Index);
        private MirrorPulseJournalDirectoryMove _retained = initial;
        internal MirrorPulseJournalDirectoryMoveAdmission Admission => _retained.Admission;
        internal bool IsAcknowledged => _retained.Mutation.State == MirrorPulseMutationState.Acknowledged;

        internal MirrorPulseDirectoryMovePermissionIdentity? PermissionIdentity(Guid memberId) =>
            !_retained.AcknowledgementStarted && _ordinals.TryGetValue(memberId, out int index) && index >= _retained.ProjectedMemberCount
                ? owner.CreatePermissionIdentity(Projection(index)) : null;

        internal async ValueTask ProjectMemberAsync(Guid memberId, CancellationToken token)
        {
            if (_retained.AcknowledgementStarted || !_ordinals.TryGetValue(memberId, out int index)) return;
            if (index > _retained.ProjectedMemberCount)
                throw new InvalidDataException("The current inventory changed the original Move projection order.");
            var readback = await owner.ProjectAsync(Admission, Projection(index), token).ConfigureAwait(false);
            if (index == _retained.ProjectedMemberCount)
                _retained = await owner.RecordProjectionAsync(Admission.JournalOperationId, index, readback, token).ConfigureAwait(false);
        }

        internal async ValueTask AcknowledgeAsync(CancellationToken token)
        {
            if (!IsAcknowledged) _retained = await owner.AcknowledgeAsync(_retained, token).ConfigureAwait(false);
        }

        private MirrorPulseJournalDirectoryMoveProjection Projection(int index)
        {
            var target = _retained.Targets![index];
            return new(Admission.Members[index], new CloudPlaceholderIdentity(target.ItemId, target.RemoteId, target.Revision));
        }
    }

    private MirrorPulseDirectoryMovePermissionIdentity CreatePermissionIdentity(MirrorPulseJournalDirectoryMoveProjection projection) =>
        identityPatch.PermissionIdentity(projection);

    private ValueTask<MirrorPulseDirectoryMoveProjectionReadback> ProjectAsync(MirrorPulseJournalDirectoryMoveAdmission admission,
        MirrorPulseJournalDirectoryMoveProjection projection, CancellationToken token) =>
        MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.DirectoryMoveProjection,
            () => identityPatch.ApplyAsync(admission, projection, token));

    private Task<MirrorPulseJournalDirectoryMove> RecordProjectionAsync(Guid operationId, int index,
        MirrorPulseDirectoryMoveProjectionReadback readback, CancellationToken token) =>
        catalog.RecordJournalDirectoryMoveProjectionAsync(operationId, index, readback, token);

    private async ValueTask<MirrorPulseJournalDirectoryMove> AcknowledgeAsync(MirrorPulseJournalDirectoryMove retained, CancellationToken token)
    {
        Guid operationId = retained.Admission.JournalOperationId;
        if (!retained.AcknowledgementStarted)
        {
            retained = await MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.DirectoryMoveOriginal,
                async () =>
                {
                    await RequireOriginalAsync(retained, token).ConfigureAwait(false);
                    return await catalog.BeginJournalDirectoryMoveAcknowledgementAsync(operationId, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        // Repeat the exact public operation if the official commit happened before the Host recorded completion.
        await MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.DirectoryMoveAcknowledgement,
            () => feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(operationId, retained.Mutation.AcceptedRevision)], token)).ConfigureAwait(false);
        return await catalog.CompleteJournalDirectoryMoveAcknowledgementAsync(operationId, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask RequireOriginalAsync(MirrorPulseJournalDirectoryMove retained, CancellationToken token)
    {
        var admission = retained.Admission;
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await transaction.Operations.GetAsync(admission.JournalOperationId, token).ConfigureAwait(false);
        if (row is null)
        {
            if (!retained.AcknowledgementStarted)
                throw new InvalidDataException("The original directory Move row disappeared before acknowledgement started.");
        }
        else if (row.Sequence != admission.JournalSequence || row.Kind != CloudStateOperationKind.Move ||
            (row.ItemId != admission.Root.OriginalItemId && (row.ItemId is not null || retained.Targets is null)) ||
            Convert.ToHexString(SHA256.HashData(row.Payload.Span)) != admission.OriginalJournalPayloadSha256)
            throw new InvalidDataException("The original directory Move journal correlation changed.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }
}
