using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryConversionCorrelatesOnlyItsRetainedUploadTimeIdentityAcrossRestart(bool originalFile)
    {
        using var fixture = new Fixture(); MoveFixture setup;
        MirrorPulseNamespaceIdentityAcceptance first, second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (originalFile) setup = await WithOriginalMovableFileAsync(catalog, setup);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            var identity = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
            var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1),
                ordinary: true, namespaceIdentity: UploadIdentity(identity));
            first = await CorrelateAsync(catalog, setup.Member.MemberId, identity, upload.Proof.OperationId);
            identity = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
            var next = await PrepareIdentityUploadAsync(catalog, setup, root, first.Confirmation.ObservedAt.AddSeconds(1),
                ordinary: true, namespaceIdentity: UploadIdentity(identity));
            second = await CorrelateAsync(catalog, setup.Member.MemberId, identity, next.Proof.OperationId);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(first, await reopened.ReadNamespaceIdentityAcceptanceAsync(first.Proof.OperationId));
        Assert.AreEqual(second, await reopened.ReadNamespaceIdentityAcceptanceAsync(second.Proof.OperationId));
        Assert.AreEqual(first.Proof.OperationId, second.PreviousAcceptanceOperationId);
        Assert.AreEqual(second.Proof.OperationId, (await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId)).AcceptanceOperationId);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("member")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("head")]
    public async Task OrdinaryConversionCannotSubstituteAnotherMemberIdentityOrUploadPredecessor(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var identity = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var capture = UploadIdentity(identity);
        capture = mismatch switch
        {
            "member" => capture with { MemberId = Guid.NewGuid() },
            "item" => capture with { ItemId = Guid.NewGuid() },
            "remote" => capture with { RemoteId = "foreign:identity" },
            _ => capture with { AcceptanceOperationId = Guid.NewGuid() }
        };
        var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1),
            ordinary: true, namespaceIdentity: capture);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CorrelateAsync(catalog, setup.Member.MemberId, identity, upload.Proof.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
        Assert.AreEqual(identity, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        Assert.AreEqual(upload.Proof, await catalog.ReadContentAcceptanceProofAsync(upload.Proof.OperationId));
    }

    [TestMethod]
    public async Task LegacyOrdinaryUploadCannotInventItsMissingMemberIdentityFromTheCurrentCatalog()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var identity = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1), ordinary: true);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => CorrelateAsync(catalog, setup.Member.MemberId, identity, upload.Proof.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
        Assert.IsFalse(JsonSerializer.Serialize(upload.Proof.UploadBinding).Contains("NamespaceIdentity", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareMutationAsync(new(Guid.NewGuid(), root.InstanceId,
            root.UniquenessKey, MirrorPulse.Core.Sync.MirrorPulseWorkerChangeKind.ContentUpdate, "new.txt", null, false, null,
            4, new string('B', 64), MirrorPulseMutationOrigin.Journal,
            new(setup.Member.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, "AQID") { NamespaceIdentity = UploadIdentity(identity) })));
    }

    private static MirrorPulseNamespaceUploadIdentity UploadIdentity(MirrorPulseNamespaceMemberIdentity identity) =>
        new(identity.MemberId, identity.ItemId!.Value, identity.RemoteId!, identity.AcceptanceOperationId);
}
