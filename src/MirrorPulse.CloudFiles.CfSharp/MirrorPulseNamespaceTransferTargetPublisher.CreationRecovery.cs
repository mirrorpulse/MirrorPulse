using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed partial class MirrorPulseNamespaceTransferTargetPublisher
{
    private static CloudPlaceholderSpec OriginalCreationSpecification(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan)
    {
        string name = birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..];
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        return birth.IsDirectory
            ? CloudDirectoryPlaceholderSpec.CreateBuilder(name, identity).WithInSyncState(true)
                .WithCollisionBehavior(CloudPlaceholderCollisionBehavior.Fail).WithPopulationState(CloudDirectoryPopulationState.Complete).Build()
            : CloudFilePlaceholderSpec.CreateBuilder(name, identity, plan.Content!.Length).WithInSyncState(true)
                .WithCollisionBehavior(CloudPlaceholderCollisionBehavior.Fail).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build();
    }

    private async Task<MirrorPulseNamespaceBirthObservation?> RecoverOriginalCreationObservationAsync(
        MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan, CancellationToken token)
    {
        actor.AssertAccess();
        if (await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false) is { } observed) return observed;
        var start = await catalog.ReadNamespaceBirthStartAsync(birth.OperationId, token).ConfigureAwait(false);
        if (start?.Version != 2) return null;
        string parentPath = birth.RelativePath[..birth.RelativePath.LastIndexOf('/')];
        var parent = fileSystem.GetDirectory(parentPath);
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(token).ConfigureAwait(false);
        if (before.LocalObject != birth.ParentLocalObject || before.RelativePath != parentPath || !before.IsDirectory || before.RootId != birth.RootId)
            throw new InvalidDataException("Creation recovery requires the original native parent.");
        var recovered = await parent.RecoverPlaceholderCreationAsync(new(birth.OperationId, OriginalCreationSpecification(birth, plan)), token)
            .ConfigureAwait(false);
        if (recovered.OperationId != birth.OperationId || recovered.Status != CloudPlaceholderCreationRecoveryStatus.Recovered || recovered.Receipt is null)
            throw new InvalidOperationException($"The original transfer target creation remains pending: {recovered.Status}.", recovered.Error);
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        var result = await MirrorPulseNamespaceBirthObserver.RecordRecoveredCreationAsync(catalog, birth.OperationId, item, router,
            recovered.Receipt, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentLease.InspectAsync(token).ConfigureAwait(false));
        return result;
    }
}
