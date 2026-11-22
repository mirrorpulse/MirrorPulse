using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    public async Task RegisteredMemberCannotConfirmWithoutTheHostsSharedNamespaceOwnership()
    {
        await using var fixture = await Fixture.OpenAsync(); var upload = await PrepareAcceptedIdentityAsync(fixture);
        var topology = await fixture.Catalog.ReadAdapterTopologyAsync();
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, topology.Roots);
        // Builder requires an existing ordinary directory; this fixture never starts or registers it.
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).Build();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MirrorPulseNamespaceContentConfirmation(fixture.Catalog, router)
            .ConfirmAsync(fileSystem.GetFile(Fixture.FirstPath), upload.Proof, default).AsTask());
        Assert.AreEqual(global::CfSharp.CloudFileSystemLifecycleState.Created, fileSystem.LifecycleState);
        Assert.IsNull(await fixture.Catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
        Assert.AreEqual(upload.Receipt, await fixture.Catalog.ReadContentConfirmationReceiptAsync(upload.Proof.OperationId));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("invalid-previous")]
    [DataRow("previous")]
    [DataRow("observed-item")]
    [DataRow("observed-revision")]
    [DataRow("unconfirmed")]
    [DataRow("unretained-receipt")]
    public async Task PublicConfirmationCorrelationRejectsInvalidOrUnretainedIdentitiesBeforePromotion(string mismatch)
    {
        await using var fixture = await Fixture.OpenAsync();
        var initial = await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId);
        var upload = await PrepareAcceptedIdentityAsync(fixture, mismatch);
        await using var actor = new MirrorPulseNamespaceActor();
        var receipt = mismatch == "unretained-receipt" ? upload.Receipt with { ObservedAt = upload.Receipt.ObservedAt.AddSeconds(1) } : upload.Receipt;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => actor.RunAsync(async token =>
            await MirrorPulseNamespaceContentConfirmation.RetainConfirmedIdentityAsync(fixture.Catalog, upload.Proof, receipt, actor, token)).AsTask());
        Assert.AreEqual(initial, await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId));
        Assert.IsNull(await fixture.Catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
        Assert.AreEqual(upload.Proof, await fixture.Catalog.ReadContentAcceptanceProofAsync(upload.Proof.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task UnchangedPublicIdentityNeedsNoRedundantCorrelationAndCancellationCreatesNoHistory()
    {
        await using var fixture = await Fixture.OpenAsync(); var upload = await PrepareAcceptedIdentityAsync(fixture, "unchanged");
        var initial = await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId);
        await using var actor = new MirrorPulseNamespaceActor();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseNamespaceContentConfirmation.RetainConfirmedIdentityAsync(
            fixture.Catalog, upload.Proof, upload.Receipt, actor).AsTask());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await actor.RunAsync(async token =>
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseNamespaceContentConfirmation.RetainConfirmedIdentityAsync(
                fixture.Catalog, upload.Proof, upload.Receipt, actor, cancellation.Token).AsTask());
            Assert.IsNull(await MirrorPulseNamespaceContentConfirmation.RetainConfirmedIdentityAsync(fixture.Catalog, upload.Proof, upload.Receipt, actor, token));
            return true;
        });
        Assert.AreEqual(initial, await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId));
        Assert.IsNull(await fixture.Catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
    }
}
