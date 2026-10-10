using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulsePlaceholderIdentityMapperTests
{
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, "accepted-v1")]
    [DataRow(true, "accepted-v1")]
    [DataRow(false, "")]
    [DataRow(true, "")]
    public void PublicCfSharpEncodingPreservesAbsentAndExplicitRevisionsWithoutChangingStableIds(bool scoped, string? revision)
    {
        var instance = InstanceId.New();
        var identity = MirrorPulsePlaceholderIdentity.Create(instance, "local:42", revision) with { RootKey = scoped ? "docs" : null };
        var native = identity.ToCfSharp();
        var decodedNative = CloudPlaceholderIdentity.Decode(native.Encode());
        Assert.AreEqual(revision, decodedNative.RemoteRevision);
        Assert.AreEqual(native.ItemId, decodedNative.ItemId);
        Assert.AreEqual(native.RemoteId, decodedNative.RemoteId);
        Assert.AreEqual(identity, MirrorPulsePlaceholderIdentity.Decode(instance, native.Encode()));
        Assert.AreEqual(native.ItemId, (identity with { RemoteRevision = "another-v2" }).ToCfSharp().ItemId);
        if (revision is null) Assert.IsNull(decodedNative.RemoteRevision);
        // Existing explicit empty envelopes remain readable; only absent values stop becoming empty.
    }

    [TestMethod]
    public void RootRouterPlansLocalBirthWithNoAcceptedNativeRevision()
    {
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.local-birth"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), "MirrorPulse-identity", Guid.NewGuid().ToString("N")), [root]);
        var planned = router.CreateFileIdentity(root.InstanceId, "docs", "local:planned");
        Assert.IsNull(planned.RemoteRevision);
        Assert.IsNull(CloudPlaceholderIdentity.Decode(planned.Encode()).RemoteRevision);
        Assert.IsTrue(MirrorPulsePlaceholderIdentity.BelongsToRoot(root, planned));
        Assert.AreEqual(planned.ItemId, router.CreateFileIdentity(root.InstanceId, "docs", "local:planned", "accepted-v1").ItemId);
    }

    [TestMethod]
    public void MappingIsStablePerInstanceAndRoundTripsThroughCfSharpEncoding()
    {
        var firstInstance = new InstanceId(Guid.Parse("f7f2d1c4-3fc6-4f56-b4c7-3bb4c1f16e02"));
        var secondInstance = new InstanceId(Guid.Parse("e2a5b269-9a7b-4fc7-a22c-8e2f56f2f747"));
        var first = MirrorPulsePlaceholderIdentity.Create(firstInstance, "remote-42", "rev-7");
        var repeated = MirrorPulsePlaceholderIdentity.Create(firstInstance, "remote-42", "rev-7");
        var isolated = MirrorPulsePlaceholderIdentity.Create(secondInstance, "remote-42", "rev-7");

        Assert.AreEqual(first.ToCfSharp().ItemId, repeated.ToCfSharp().ItemId);
        Assert.AreNotEqual(first.ToCfSharp().ItemId, isolated.ToCfSharp().ItemId);

        var decoded = MirrorPulsePlaceholderIdentity.Decode(firstInstance, first.Encode());
        Assert.AreEqual(first.RemoteId, decoded.RemoteId);
        Assert.AreEqual(first.RemoteRevision, decoded.RemoteRevision);
        Assert.AreEqual(first.ToCfSharp().ItemId, decoded.ToCfSharp().ItemId);
    }
}
