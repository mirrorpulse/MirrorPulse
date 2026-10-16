using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
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
public sealed partial class MirrorPulseNamespaceContentConfirmation(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceActor? namespaceActor = null, MirrorPulseNamespaceExecutionSession? namespaceExecution = null)
{
    public ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken cancellationToken) => ConfirmCoreAsync(file, proof, false, cancellationToken);

    /// <summary>Reconfirms an unchanged accepted placeholder without replacing its historical acceptance or receipt.</summary>
    public async ValueTask<MirrorPulseContentConfirmationReceipt> ReconfirmObservedContentAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(proof);
        var accepted = await catalog.ReadMutationAsync(proof.OperationId, cancellationToken).ConfigureAwait(false);
        var route = router.ResolvePath(file.RelativePath);
        var snapshot = await file.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (accepted is not { State: MirrorPulseMutationState.Acknowledged } || accepted.Intent.InstanceId != route.InstanceId ||
            accepted.Intent.RootKey != route.RootKey || router.GetRegistration(route.InstanceId, route.RootKey).State != RootRegistrationState.Active ||
            await catalog.ReadContentAcceptanceProofAsync(proof.OperationId, cancellationToken).ConfigureAwait(false) != proof ||
            (await catalog.ReadContentConfirmationReceiptAsync(proof.OperationId, cancellationToken).ConfigureAwait(false))?.MayAcknowledge != true ||
            !snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.ItemId != proof.AcceptedItemId ||
            !snapshot.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision ?? string.Empty).Encode()))
            throw new InvalidDataException("Observation reconfirmation requires the actual completed acceptance and its unchanged native identity.");
        return await ConfirmCoreAsync(file, proof, true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmCoreAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, bool reconfirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(proof.UploadBinding);
        if (namespaceActor is null)
        {
            if (await catalog.ReadNamespaceMemberByBindingAsync(proof.UploadBinding.LocalObject, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Registered member confirmation requires the Host's shared namespace actor and execution role.");
            return await ConfirmAndSaveAsync(file, proof, reconfirm, cancellationToken).ConfigureAwait(false);
        }
        return await namespaceActor.RunAsync(async token =>
        {
            namespaceActor.AssertAccess();
            // Registration uses this same actor. Decide membership under admission so an
            // unregistered file cannot become registered during its confirmation boundary.
            var member = await catalog.ReadNamespaceMemberByBindingAsync(proof.UploadBinding.LocalObject, token).ConfigureAwait(false);
            if (member is null) return await ConfirmAndSaveAsync(file, proof, reconfirm, token).ConfigureAwait(false);
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
            return await ConfirmProtectedMemberAsync(file, proof, member, location, namespaceExecution, namespaceActor, reconfirm, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task<MirrorPulseContentConfirmationReceipt> ConfirmProtectedMemberAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberLocation location, MirrorPulseNamespaceExecutionSession execution,
        MirrorPulseNamespaceActor actor, bool reconfirm, CancellationToken token)
    {
        // Validate durable acceptance and predecessor identity before the public native mark.
        if (await catalog.ReadContentAcceptanceProofAsync(proof.OperationId, token).ConfigureAwait(false) != proof)
            throw new InvalidDataException("Protected confirmation requires its retained upload proof and previous public identity.");
        var identity = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
        var previous = ReadPreviousIdentity(proof.UploadBinding, member.MemberId);
        bool replay = identity.AcceptanceOperationId == proof.OperationId;
        if (replay ? identity.ItemId != proof.AcceptedItemId || identity.RemoteId != proof.RemoteId :
            identity.ItemId != previous.ItemId || identity.RemoteId != previous.RemoteId)
            throw new InvalidDataException("Protected confirmation cannot rewrite a superseded member identity.");
        if (!replay && proof.UploadBinding.NamespaceIdentity is { } uploaded && uploaded.AcceptanceOperationId != identity.AcceptanceOperationId)
            throw new InvalidDataException("Ordinary conversion must retain its upload-time identity predecessor.");
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        var parentMember = await catalog.ReadNamespaceMemberAsync(location.ParentMemberId!.Value, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The committed confirmation parent is missing.");
        var parentLocation = await catalog.ReadNamespaceMemberLocationAsync(parentMember.MemberId, token).ConfigureAwait(false);
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        CloudDirectory parent = file.Parent ?? throw new InvalidDataException("Confirmation requires the actual direct parent.");
        if (parent.RelativePath.Replace('\\', '/') != parentLocation.RelativePath || parentMember.RootId is { } root && root != member.RootId ||
            childProof.RoleSid != execution.RoleSid.Value || parentProof.RoleSid != execution.RoleSid.Value ||
            member.OwnerSid != execution.OwnerSid.Value || parentMember.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidDataException("Confirmation requires the current owner, namespace role and committed direct parent.");
        MirrorPulseContentConfirmationReceipt receipt = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            var parentBefore = await parentLease.InspectAsync(token).ConfigureAwait(false);
            RequireProtectedObject(parentBefore, await parent.InspectAsync(token).ConfigureAwait(false), parentMember, parentProof, parentLocation.RelativePath);
            RequireProtectedObject(await childLease.InspectAsync(token).ConfigureAwait(false), await file.InspectAsync(token).ConfigureAwait(false),
                member, childProof, location.RelativePath);
            receipt = await ConfirmAndSaveAsync(file, proof, reconfirm, token).ConfigureAwait(false);
            if (!receipt.MayAcknowledge) return;
            CancellationToken durable = CancellationToken.None;
            RequireProtectedObject(await childLease.InspectAsync(durable).ConfigureAwait(false), await file.InspectAsync(durable).ConfigureAwait(false),
                member, childProof, location.RelativePath);
            RequireProtectedObject(await parentLease.InspectAsync(durable).ConfigureAwait(false), await parent.InspectAsync(durable).ConfigureAwait(false),
                parentMember, parentProof, parentLocation.RelativePath);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentLease.InspectAsync(durable).ConfigureAwait(false));
            if (await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, durable).ConfigureAwait(false) != childProof ||
                await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, durable).ConfigureAwait(false) != parentProof)
                throw new InvalidDataException("Protection changed across public content confirmation.");
            actor.AssertAccess();
            if (!reconfirm) await RetainConfirmedIdentityAsync(catalog, proof, receipt, actor, durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return receipt;
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private static void RequireProtectedObject(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceProtectionProof proof, string path)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != member.LocalObject || facts.LocalObject != proof.Verification.LocalObject ||
            facts.OwnerSid != member.OwnerSid || facts.OwnerSid != proof.OwnerSid || facts.OwnerSid != proof.Verification.OwnerSid ||
            facts.RootId != member.RootId || facts.RootId != proof.RootId || facts.RelativePath != path ||
            facts.IsDirectory != member.IsDirectory || facts.IsDirectory != proof.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory || facts.LinkCount != 1 ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("Content confirmation requires the current protected object and its actual committed location.");
    }

    private async ValueTask<MirrorPulseContentConfirmationReceipt> ConfirmAndSaveAsync(CloudFile file,
        MirrorPulseContentAcceptanceProof proof, bool reconfirm, CancellationToken token)
    {
        var receipt = reconfirm ? MirrorPulseContentConfirmation.MapReceipt(await file.ConfirmUploadedContentAsync(
            MirrorPulseContentConfirmation.CreateObservationRequest(proof), token).ConfigureAwait(false)) :
            await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, token).ConfigureAwait(false);
        // Upload confirmation retains its observation after caller cancellation.
        // A new observation must not overwrite the historical acceptance receipt.
        if (!reconfirm) await catalog.SaveContentConfirmationReceiptAsync(proof.OperationId, receipt, CancellationToken.None).ConfigureAwait(false);
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
        if (!receipt.MayAcknowledge ||
            await catalog.ReadContentAcceptanceProofAsync(proof.OperationId, cancellationToken).ConfigureAwait(false) != proof ||
            await catalog.ReadContentConfirmationReceiptAsync(proof.OperationId, cancellationToken).ConfigureAwait(false) != receipt)
            throw new InvalidDataException("Identity correlation requires the exact retained acceptance and successful public confirmation.");
        CloudPlaceholderIdentity previous, observed;
        try
        {
            previous = ReadPreviousIdentity(proof.UploadBinding, member.MemberId);
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

    private static CloudPlaceholderIdentity ReadPreviousIdentity(MirrorPulseUploadBinding binding, Guid memberId) => binding.Preparation switch
    {
        MirrorPulseContentPreparation.ReplacePlaceholderIdentity =>
            CloudPlaceholderIdentity.Decode(Convert.FromBase64String(binding.PreviousPlaceholderIdentity ?? string.Empty)),
        MirrorPulseContentPreparation.ConvertRegularFile when binding.NamespaceIdentity is { } identity && identity.MemberId == memberId =>
            new(identity.ItemId, identity.RemoteId, string.Empty),
        _ => throw new InvalidDataException("Registered conversion requires its original upload-time product identity; it cannot be inferred from the current path."),
    };
}
