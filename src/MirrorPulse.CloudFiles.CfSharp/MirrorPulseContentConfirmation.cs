using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Translates durable product proof to CfSharp's managed content-confirmation boundary.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public static class MirrorPulseContentConfirmation
{
    public static MirrorPulseUploadBinding CaptureUploadBinding(CloudItemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Exists || snapshot.Kind != CloudItemKind.File || snapshot.LocalBinding is not { } binding ||
            snapshot.IsPlaceholder && snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable)
            throw new InvalidDataException("A complete local file and upload-time native binding are required.");
        if (snapshot.IsPlaceholder && snapshot.PlaceholderIdentity.IsEmpty)
            throw new InvalidDataException("An empty placeholder identity cannot be adopted for content confirmation.");
        return new(new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId),
            snapshot.IsPlaceholder ? MirrorPulseContentPreparation.ReplacePlaceholderIdentity : MirrorPulseContentPreparation.ConvertRegularFile,
            snapshot.IsPlaceholder ? Convert.ToBase64String(snapshot.PlaceholderIdentity.Span) : null);
    }

    public static CloudContentConfirmationRequest CreateRequest(MirrorPulseContentAcceptanceProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        MirrorPulseLocalFileBinding binding = proof.UploadBinding.LocalObject;
        return new(new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId),
            new(proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision ?? string.Empty), proof.Length,
            Convert.FromHexString(proof.Sha256), proof.UploadBinding.Preparation switch
            {
                MirrorPulseContentPreparation.None => CloudContentPreparation.None,
                MirrorPulseContentPreparation.ConvertRegularFile => CloudContentPreparation.ConvertRegularFile,
                MirrorPulseContentPreparation.ReplacePlaceholderIdentity => CloudContentPreparation.ReplacePlaceholderIdentity,
                _ => throw new ArgumentException("The product preparation mode is unsupported.", nameof(proof)),
            }, proof.UploadBinding.PreviousPlaceholderIdentity is { } previous ? Convert.FromBase64String(previous) : []);
    }

    public static async ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken token) =>
        MapReceipt(await file.ConfirmUploadedContentAsync(CreateRequest(proof), token).ConfigureAwait(false));

    public static MirrorPulseContentConfirmationReceipt MapReceipt(CloudContentConfirmationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Outcome switch
        {
            CloudContentConfirmationOutcome.Confirmed => MirrorPulseContentConfirmationOutcome.Confirmed,
            CloudContentConfirmationOutcome.AlreadyConfirmed => MirrorPulseContentConfirmationOutcome.AlreadyConfirmed,
            CloudContentConfirmationOutcome.ContentMismatch => MirrorPulseContentConfirmationOutcome.ContentMismatch,
            CloudContentConfirmationOutcome.IdentityMismatch => MirrorPulseContentConfirmationOutcome.IdentityMismatch,
            CloudContentConfirmationOutcome.LocalObjectMismatch => MirrorPulseContentConfirmationOutcome.LocalObjectMismatch,
            CloudContentConfirmationOutcome.NotFullyLocal => MirrorPulseContentConfirmationOutcome.NotFullyLocal,
            CloudContentConfirmationOutcome.NotApplicable => MirrorPulseContentConfirmationOutcome.NotApplicable,
            CloudContentConfirmationOutcome.Busy => MirrorPulseContentConfirmationOutcome.Busy,
            CloudContentConfirmationOutcome.ProtectionLost => MirrorPulseContentConfirmationOutcome.ProtectionLost,
            CloudContentConfirmationOutcome.DeadlineExceeded => MirrorPulseContentConfirmationOutcome.DeadlineExceeded,
            CloudContentConfirmationOutcome.Canceled => MirrorPulseContentConfirmationOutcome.Canceled,
            CloudContentConfirmationOutcome.Failed => MirrorPulseContentConfirmationOutcome.Failed,
            CloudContentConfirmationOutcome.NativeAppliedProjectionPending => MirrorPulseContentConfirmationOutcome.NativeAppliedProjectionPending,
            CloudContentConfirmationOutcome.ProjectionConflict => MirrorPulseContentConfirmationOutcome.ProjectionConflict,
            _ => throw new InvalidDataException("The library returned an unsupported confirmation outcome."),
        }, MapStage(result.Stage), MapStage(result.NativeStage), result.NativeIdentityPrepared, result.NativeApplied,
            result.NativeConfirmationVerified, result.DurableProjectionCommitted, result.BytesVerified, result.SegmentsRead,
            result.PreparationHResult, result.NativeMarkHResult, result.NativeError?.HResult, result.ProjectionError?.HResult,
            result.ObservedSynchronizationState switch
            {
                CloudSynchronizationState.InSync => true,
                CloudSynchronizationState.NotInSync => false,
                _ => null,
            }, result.ObservedPlaceholderIdentity is { } identity ? Convert.ToBase64String(identity.Span) : null, DateTimeOffset.UtcNow);
    }

    private static MirrorPulseContentConfirmationStage MapStage(CloudContentConfirmationStage stage) => stage switch
    {
        CloudContentConfirmationStage.Open => MirrorPulseContentConfirmationStage.Open,
        CloudContentConfirmationStage.Reference => MirrorPulseContentConfirmationStage.Reference,
        CloudContentConfirmationStage.Read => MirrorPulseContentConfirmationStage.Read,
        CloudContentConfirmationStage.Verify => MirrorPulseContentConfirmationStage.Verify,
        CloudContentConfirmationStage.Prepare => MirrorPulseContentConfirmationStage.Prepare,
        CloudContentConfirmationStage.Mark => MirrorPulseContentConfirmationStage.Mark,
        CloudContentConfirmationStage.Projection => MirrorPulseContentConfirmationStage.Projection,
        CloudContentConfirmationStage.Complete => MirrorPulseContentConfirmationStage.Complete,
        _ => throw new InvalidDataException("The library returned an unsupported confirmation stage."),
    };
}
