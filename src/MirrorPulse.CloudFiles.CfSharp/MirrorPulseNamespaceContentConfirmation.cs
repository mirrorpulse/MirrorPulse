using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains confirmed public identities before a registered member's upload may be acknowledged.</summary>
/// <remarks>
/// Source access has already finished. Registered members share the Host's namespace actor and
/// execution role; CfSharp owns content verification and official projection. Retained receipts
/// are historical facts and grant no current filesystem or permission authority.
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseNamespaceContentConfirmation(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceActor? namespaceActor = null, MirrorPulseNamespaceExecutionSession? namespaceExecution = null)
{
    public async ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(proof.UploadBinding);
        if (namespaceActor is null)
        {
            if (await catalog.ReadNamespaceMemberByBindingAsync(proof.UploadBinding.LocalObject, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Registered member confirmation requires the Host's shared namespace actor and execution role.");
            return await ConfirmAndSaveAsync(file, proof, cancellationToken).ConfigureAwait(false);
        }
        return await namespaceActor.RunAsync(async token =>
        {
            namespaceActor.AssertAccess();
            // Registration uses this same actor. Decide membership under admission so an
            // unregistered file cannot become registered during its confirmation boundary.
            var member = await catalog.ReadNamespaceMemberByBindingAsync(proof.UploadBinding.LocalObject, token).ConfigureAwait(false);
            if (member is null) return await ConfirmAndSaveAsync(file, proof, token).ConfigureAwait(false);
            if (namespaceExecution is null)
                throw new InvalidOperationException("Registered member confirmation requires the Host's namespace execution role.");
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected namespace confirmation requires Windows 11 version 24H2 or later.");
            var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
            var route = router.ResolvePath(file.RelativePath);
            var mutation = await catalog.ReadMutationAsync(proof.OperationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original accepted upload is missing.");
            if (member.IsDirectory || member.RootId is null || location.RelativePath != file.RelativePath.Replace('\\', '/') ||
                router.GetRegistration(route.InstanceId, route.RootKey).RootId != member.RootId ||
                mutation.Intent.InstanceId != route.InstanceId || mutation.Intent.RootKey != route.RootKey ||
                (await catalog.ReadNamespaceRecoveryRootsAsync(token).ConfigureAwait(false)).Contains(member.RootId.Value))
                throw new InvalidOperationException("Content confirmation requires this member's committed location and unfenced owning root.");
            MirrorPulseContentConfirmationReceipt receipt = null!;
            await namespaceExecution.RunNamespaceOperationAsync(async () =>
            {
                receipt = await ConfirmAndSaveAsync(file, proof, token).ConfigureAwait(false);
                if (receipt.MayAcknowledge)
                    await RetainConfirmedIdentityAsync(catalog, proof, receipt, namespaceActor, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return receipt;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmAndSaveAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken token)
    {
        var receipt = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, token).ConfigureAwait(false);
        // Once the native boundary returns, retain its observation even after caller cancellation.
        await catalog.SaveContentConfirmationReceiptAsync(proof.OperationId, receipt, CancellationToken.None).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>Correlates an exact saved successful receipt using CfSharp's public identity codec.</summary>
    /// <remarks>The caller owns confirmation and the shared actor. This method performs no native operation or ACK.</remarks>
    public static async ValueTask<MirrorPulseNamespaceIdentityAcceptance?> RetainConfirmedIdentityAsync(
        MirrorPulseProductCatalog catalog, MirrorPulseContentAcceptanceProof proof, MirrorPulseContentConfirmationReceipt receipt,
        MirrorPulseNamespaceActor namespaceActor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(namespaceActor);
        ArgumentNullException.ThrowIfNull(proof.UploadBinding);
        cancellationToken.ThrowIfCancellationRequested(); namespaceActor.AssertAccess();
        var member = await catalog.ReadNamespaceMemberByBindingAsync(proof.UploadBinding.LocalObject, cancellationToken).ConfigureAwait(false);
        if (member is null) return null;
        if (!receipt.MayAcknowledge || proof.UploadBinding.Preparation != MirrorPulseContentPreparation.ReplacePlaceholderIdentity ||
            await catalog.ReadContentAcceptanceProofAsync(proof.OperationId, cancellationToken).ConfigureAwait(false) != proof ||
            await catalog.ReadContentConfirmationReceiptAsync(proof.OperationId, cancellationToken).ConfigureAwait(false) != receipt)
            throw new InvalidDataException("Identity correlation requires the exact retained acceptance and successful public confirmation.");
        CloudPlaceholderIdentity previous, observed;
        try
        {
            previous = CloudPlaceholderIdentity.Decode(Convert.FromBase64String(proof.UploadBinding.PreviousPlaceholderIdentity ?? string.Empty));
            observed = CloudPlaceholderIdentity.Decode(Convert.FromBase64String(receipt.ObservedPlaceholderIdentity ?? string.Empty));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException)
        { throw new InvalidDataException("Identity correlation requires valid public previous and confirmed placeholder identities.", error); }
        if (observed.ItemId != proof.AcceptedItemId || observed.RemoteId != proof.RemoteId ||
            observed.RemoteRevision != (proof.AcceptedRevision ?? string.Empty))
            throw new InvalidDataException("The confirmed public identity does not match the accepted upload proof.");
        var replay = await catalog.ReadNamespaceIdentityAcceptanceAsync(proof.OperationId, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.MemberId != member.MemberId || replay.Proof != proof ||
                replay.PreviousItemId != previous.ItemId || replay.PreviousRemoteId != previous.RemoteId)
                throw new InvalidDataException("An identity replay cannot replace its retained member or previous public identity.");
            return replay;
        }
        var current = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, cancellationToken).ConfigureAwait(false);
        if (current.ItemId != previous.ItemId || current.RemoteId != previous.RemoteId)
            throw new InvalidDataException("The upload-time identity does not follow this registered member's retained identity.");
        // Content-only uploads commonly keep their public identity; retain no redundant history.
        if (current.ItemId == observed.ItemId && current.RemoteId == observed.RemoteId) return null;
        namespaceActor.AssertAccess();
        return await catalog.RecordNamespaceIdentityAcceptanceAsync(member.MemberId, proof.OperationId, current.AcceptanceOperationId,
            previous.ItemId, previous.RemoteId, cancellationToken).ConfigureAwait(false);
    }
}
