using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
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
    internal async ValueTask<bool> ResumeAsync(Guid operationId, CancellationToken token)
    {
        var retained = await catalog.ReadJournalDirectoryMoveAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original directory Move was not admitted.");
        if (retained.Mutation.State == MirrorPulseMutationState.Acknowledged) return true;
        var admission = retained.Admission;
        var intent = retained.Mutation.Intent;
        await RequireOriginalAsync(retained, token).ConfigureAwait(false);
        if (retained.Mutation.State == MirrorPulseMutationState.Executing)
        {
            if (!worker.SupportsDurableDirectoryMove(intent.InstanceId)) return false;
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
        if (!retained.AcknowledgementStarted)
        {
            for (int index = 0; index < admission.Members.Count; index++)
            {
                var target = retained.Targets![index];
                var identity = new CloudPlaceholderIdentity(target.ItemId, target.RemoteId, target.Revision);
                var readback = await identityPatch.ApplyAsync(admission, new(admission.Members[index], identity), token).ConfigureAwait(false);
                if (index >= retained.ProjectedMemberCount)
                    retained = await catalog.RecordJournalDirectoryMoveProjectionAsync(operationId, index, readback, token).ConfigureAwait(false);
            }
            await RequireOriginalAsync(retained, token).ConfigureAwait(false);
            retained = await catalog.BeginJournalDirectoryMoveAcknowledgementAsync(operationId, token).ConfigureAwait(false);
        }
        // Repeat the exact public operation if the official commit happened before the Host recorded completion.
        await feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(operationId, retained.Mutation.AcceptedRevision)], token).ConfigureAwait(false);
        await catalog.CompleteJournalDirectoryMoveAcknowledgementAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        return true;
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
