using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfirmedAcceptedItemIdResolvesOfficialProjectionWithoutAdoptingTheFirstPath(bool born)
    {
        await using var fixture = await Fixture.OpenAsync(born);
        var initial = await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId);
        var proof = await RetainAcceptedIdentityAsync(fixture);
        var current = new CloudItemState(proof.AcceptedItemId, proof.RemoteId, @"Docs\moved", fixture.Kind,
            proof.AcceptedRevision, 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(current);
        var foreign = new CloudItemState(Guid.NewGuid(), "reused:old-path", Fixture.FirstPath, fixture.Kind, null, 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(foreign);
        await fixture.RestartAsync(); await using var actor = new MirrorPulseNamespaceActor();
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.Projected, candidate.State);
        Assert.AreEqual(proof.AcceptedItemId, candidate.ItemId); Assert.AreEqual(proof.RemoteId, candidate.RemoteId);
        Assert.AreEqual(initial.OriginalRemoteId, candidate.OriginalRemoteId); Assert.AreEqual(current.RelativePath, candidate.RelativePath);
        Assert.AreEqual(fixture.Member, candidate.Member);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "existing"));
        await fixture.AssertRowAndNoJournalAsync(current); await fixture.AssertRowAndNoJournalAsync(foreign);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task MissingAcceptedItemDoesNotFallBackToTheFirstItemOrReusedRemoteId()
    {
        await using var fixture = await Fixture.OpenAsync();
        var proof = await RetainAcceptedIdentityAsync(fixture);
        await fixture.PutAsync(fixture.Row());
        var foreign = new CloudItemState(Guid.NewGuid(), proof.RemoteId, @"Docs\foreign", fixture.Kind, null, 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(foreign); await using var actor = new MirrorPulseNamespaceActor();
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.ProjectionMissing, candidate.State);
        Assert.AreEqual(proof.AcceptedItemId, candidate.ItemId); Assert.IsNull(candidate.RelativePath);
        await fixture.AssertRowAndNoJournalAsync(foreign);
    }

    private static async Task<MirrorPulseContentAcceptanceProof> RetainAcceptedIdentityAsync(Fixture fixture)
    {
        var prepared = await PrepareAcceptedIdentityAsync(fixture);
        await using var actor = new MirrorPulseNamespaceActor();
        await actor.RunAsync(async token => await MirrorPulseNamespaceContentConfirmation.RetainConfirmedIdentityAsync(
            fixture.Catalog, prepared.Proof, prepared.Receipt, actor, token));
        return prepared.Proof;
    }

    private static async Task<(MirrorPulseContentAcceptanceProof Proof, MirrorPulseContentConfirmationReceipt Receipt)>
        PrepareAcceptedIdentityAsync(Fixture fixture, string? mismatch = null)
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.identities"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.GetTempPath(), new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(manifest.AdapterId, installation.InstallId, InstanceId.New(), "Storage",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
        var root = new RootRegistration(manifest.AdapterId, instance.InstanceId, fixture.Member.RootId!.Value, "docs", "Docs", "Docs", false,
            RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        await fixture.Catalog.SaveAdapterTopologyAsync(new([installation], [instance], [root]));
        var previous = await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId);
        var oldIdentity = new CloudPlaceholderIdentity(mismatch == "previous" ? Guid.NewGuid() : previous.ItemId!.Value, previous.RemoteId!, "before");
        var binding = new MirrorPulseUploadBinding(fixture.Member.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity,
            mismatch == "invalid-previous" ? "AQID" : Convert.ToBase64String(oldIdentity.Encode()));
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), root.InstanceId, root.UniquenessKey, MirrorPulseWorkerChangeKind.ContentUpdate,
            "existing", null, false, "before", 4, new string('A', 64), MirrorPulseMutationOrigin.Journal, binding);
        await fixture.Catalog.PrepareMutationAsync(intent);
        await fixture.Catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await fixture.Catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "after");
        var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, binding,
            mismatch == "unchanged" ? previous.ItemId!.Value : Guid.NewGuid(), mismatch == "unchanged" ? previous.RemoteId! : "source:" + Guid.NewGuid(), "after", 4, intent.ContentSha256!);
        await fixture.Catalog.SaveContentAcceptanceProofAsync(proof);
        // This managed boundary supplies historical product facts; it makes no native confirmation claim.
        var receipt = new MirrorPulseContentConfirmationReceipt(MirrorPulseContentConfirmationOutcome.Confirmed,
            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete, true, true, true, true,
            4, 1, 0, 0, null, null, true, Convert.ToBase64String(new CloudPlaceholderIdentity(
                mismatch == "observed-item" ? Guid.NewGuid() : proof.AcceptedItemId, proof.RemoteId,
                mismatch == "observed-revision" ? "foreign-revision" : "after").Encode()), fixture.Member.RegisteredAt.AddSeconds(1));
        if (mismatch == "unconfirmed") receipt = receipt with { NativeConfirmationVerified = false };
        await fixture.Catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, receipt);
        return (proof, receipt);
    }
}
