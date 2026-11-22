using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseGenerationalPlaceholderIdentityTests
{
    [TestMethod]
    [DataRow(RootIdentityScope.LegacyInstance, null)]
    [DataRow(RootIdentityScope.InstanceRoot, null)]
    [DataRow(RootIdentityScope.LegacyInstance, "")]
    [DataRow(RootIdentityScope.InstanceRoot, "")]
    [DataRow(RootIdentityScope.LegacyInstance, " opaque-revision:雪|α% ")]
    [DataRow(RootIdentityScope.InstanceRoot, " opaque-revision:雪|α% ")]
    public void PublicCodecRetainsExactOpaqueAddressAndSelectedGeneration(RootIdentityScope scope, string? revision)
    {
        var root = Root(scope); Guid generation = Guid.NewGuid(); const string remote = " opaque-id:/雪|α% ";
        var identity = MirrorPulseGenerationalPlaceholderIdentity.Create(root, remote, generation, revision);
        var roundTrip = CloudPlaceholderIdentity.Decode(identity.Encode());
        Assert.IsTrue(MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, roundTrip, out var decoded));
        Assert.AreEqual(root.RootId, decoded!.RootId); Assert.AreEqual(root.UniquenessKey, decoded.RootKey);
        Assert.AreEqual(remote, decoded.RemoteId); Assert.AreEqual(generation, decoded.Generation);
        Assert.AreEqual(revision, decoded.RemoteRevision);
        Assert.IsTrue(MirrorPulseGenerationalPlaceholderIdentity.BelongsToRoot(root, roundTrip));
        Assert.AreEqual(identity, MirrorPulseGenerationalPlaceholderIdentity.Create(root, remote, generation, revision));
    }

    [TestMethod]
    [DataRow(RootIdentityScope.LegacyInstance)]
    [DataRow(RootIdentityScope.InstanceRoot)]
    public void RevisionAndLabelChangesKeepOneGenerationWhileReappearanceGetsADistinctIdentity(RootIdentityScope scope)
    {
        var root = Root(scope); Guid generation = Guid.NewGuid();
        var first = MirrorPulseGenerationalPlaceholderIdentity.Create(root, "same-source", generation, "v1");
        var revised = MirrorPulseGenerationalPlaceholderIdentity.Create(CopyRoot(root, label: "Renamed"), "same-source", generation, "v2");
        var later = MirrorPulseGenerationalPlaceholderIdentity.Create(root, "same-source", Guid.NewGuid(), "v2");
        Assert.AreEqual(first.ItemId, revised.ItemId); Assert.AreEqual(first.RemoteId, revised.RemoteId);
        Assert.AreNotEqual(first.ItemId, later.ItemId); Assert.AreNotEqual(first.RemoteId, later.RemoteId);
        Assert.IsTrue(MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, later, out var decoded));
        Assert.AreEqual("same-source", decoded!.RemoteId);
        var original = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "same-source", "v1").ToCfSharp();
        Assert.AreNotEqual(original.ItemId, first.ItemId);
        Assert.AreEqual(original, MirrorPulsePlaceholderIdentity.CreateForRoot(root, "same-source", "v1").ToCfSharp());
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("root-id")]
    [DataRow("root-key")]
    public void AGenerationCannotCrossItsInstanceOrStableRoot(string change)
    {
        var root = Root(RootIdentityScope.InstanceRoot);
        var identity = MirrorPulseGenerationalPlaceholderIdentity.Create(root, "same-source", Guid.NewGuid());
        var other = change switch
        {
            "instance" => CopyRoot(root, instance: InstanceId.New()),
            "root-id" => CopyRoot(root, rootId: RootId.New()),
            _ => CopyRoot(root, rootKey: "another"),
        };
        Assert.IsFalse(MirrorPulseGenerationalPlaceholderIdentity.BelongsToRoot(other, identity));
    }

    [TestMethod]
    [DataRow("item-id")]
    [DataRow("padding")]
    [DataRow("empty-generation")]
    [DataRow("uppercase-generation")]
    [DataRow("extra-part")]
    public void AlteredOrNoncanonicalEnvelopesCannotClaimAGeneration(string alteration)
    {
        var root = Root(RootIdentityScope.InstanceRoot);
        var identity = MirrorPulseGenerationalPlaceholderIdentity.Create(root, "same-source", Guid.Parse("abcdef12-abcd-4abc-8abc-abcdef123456"));
        string[] parts = identity.RemoteId.Split(':'); Guid itemId = identity.ItemId;
        if (alteration == "item-id") itemId = Guid.NewGuid();
        if (alteration == "padding") parts[5] += "=";
        if (alteration == "empty-generation") parts[4] = Guid.Empty.ToString("N");
        if (alteration == "uppercase-generation") parts[4] = parts[4].ToUpperInvariant();
        string remote = string.Join(':', parts) + (alteration == "extra-part" ? ":extra" : string.Empty);
        var altered = new CloudPlaceholderIdentity(itemId, remote, identity.RemoteRevision);
        Assert.IsFalse(MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, altered, out _));
        Assert.IsFalse(MirrorPulseGenerationalPlaceholderIdentity.BelongsToRoot(root, altered));
    }

    [TestMethod]
    public void AnEmptyGenerationCannotBeCreated()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MirrorPulseGenerationalPlaceholderIdentity.Create(
            Root(RootIdentityScope.InstanceRoot), "same-source", Guid.Empty));
    }

    private static RootRegistration Root(RootIdentityScope scope) => AdapterRootRegistrationMapper.Map(
        AdapterId.Parse("example.generation"), InstanceId.New(), new("docs", "Docs", "Docs", false),
        RootRegistrationState.Active, identityScope: scope);

    private static RootRegistration CopyRoot(RootRegistration root, InstanceId? instance = null,
        RootId? rootId = null, string? rootKey = null, string? label = null) =>
        new(root.AdapterId, instance ?? root.InstanceId, rootId ?? root.RootId, rootKey ?? root.UniquenessKey,
            label ?? root.Label, label ?? root.DirectoryName, root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
}
