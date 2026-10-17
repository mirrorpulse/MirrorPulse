using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseJournalUploadPump
{
    private async Task<MirrorPulseCoalescingObservedContent?> TryObserveAcceptedContentAsync(MirrorPulseJournalCoalescingPlan plan,
        CloudItemSnapshot snapshot, MirrorPulseCoalescingContent content, string baseline, CancellationToken token)
    {
        if (plan.OriginalPath != plan.FinalPath && plan.Effect != MirrorPulseCoalescedEffect.MoveAndUpdateFile ||
            !snapshot.Exists || snapshot.IsTombstone || !snapshot.IsPlaceholder ||
            snapshot.ItemId != plan.ItemId || snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable ||
            content.UploadBinding.Preparation != MirrorPulseContentPreparation.ReplacePlaceholderIdentity ||
            content.UploadBinding.PreviousPlaceholderIdentity != Convert.ToBase64String(snapshot.PlaceholderIdentity.Span))
            return await ClassifiedAsync(null, "Unavailable").ConfigureAwait(false);
        var proof = await _catalog.ReadAcknowledgedContentProofAsync(plan.InstanceId, plan.RootKey, plan.ItemId, baseline,
            content.UploadBinding.LocalObject, token).ConfigureAwait(false);
        if (proof is null) return await ClassifiedAsync(null, "NoAcceptedProof").ConfigureAwait(false);
        if (proof.Length != content.Length || !string.Equals(proof.Sha256, content.Sha256, StringComparison.OrdinalIgnoreCase))
            return await ClassifiedAsync(null, "ContentChanged").ConfigureAwait(false);
        if (snapshot.RemoteId != proof.RemoteId || snapshot.RemoteRevision != proof.AcceptedRevision ||
            !snapshot.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(proof.AcceptedItemId, proof.RemoteId, baseline).Encode()))
            return await ClassifiedAsync(null, "IdentityChanged").ConfigureAwait(false);
        return await ClassifiedAsync(new(proof.OperationId, content, proof.AcceptedItemId, proof.RemoteId, baseline), "Selected").ConfigureAwait(false);

        async Task<MirrorPulseCoalescingObservedContent?> ClassifiedAsync(MirrorPulseCoalescingObservedContent? result, string decision)
        {
            try
            {
                await _log.WriteAsync(new(LogLevel.Information, "CloudFiles.Upload", "JournalContentObservation", DateTimeOffset.UtcNow,
                    [new("planId", plan.PlanId.ToString("D")), new("contentObservation", decision)]), token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
            return result;
        }
    }

    private async ValueTask ReconfirmObservedEffectAsync(MirrorPulseJournalCoalescingPlan plan, MirrorPulseJournalCoalescingExecution execution,
        MirrorPulseMutationRecord accepted, CancellationToken token)
    {
        var observed = execution.ObservedContent ?? throw new InvalidDataException("The verification effect has no retained content observation.");
        if (accepted.State != MirrorPulseMutationState.RemoteAccepted || accepted.AcceptedRevision != execution.ExpectedRevision)
            throw new InvalidDataException("Observed content completion requires the actual unchanged remote verification.");
        var proof = await _catalog.ReadContentAcceptanceProofAsync(observed.AcceptanceOperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The observed verification lost its original acceptance proof.");
        string path = _router.ResolveUploadPath(plan.InstanceId, plan.RootKey, plan.FinalPath);
        string relative = Path.GetRelativePath(_syncRootPath, path).Replace(Path.DirectorySeparatorChar, '/');
        var receipt = await _contentConfirmation.ReconfirmObservedContentAsync(_fileSystem!.GetFile(relative), proof, token).ConfigureAwait(false);
        if (!receipt.MayAcknowledge)
            throw new MirrorPulseMutationAmbiguousException("The unchanged content has not completed protected native reconfirmation.");
    }
}
