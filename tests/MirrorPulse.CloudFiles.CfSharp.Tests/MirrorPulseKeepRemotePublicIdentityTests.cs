using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class MirrorPulseKeepRemotePublicIdentityTests
{
    [TestMethod]
    [DataRow("legacy")]
    [DataRow("scoped")]
    [DataRow("generation")]
    public void SelectedRevisionKeepsTheOriginalPublicAddressAndGeneration(string shape)
    {
        var root = Root(shape == "legacy" ? RootIdentityScope.LegacyInstance : RootIdentityScope.InstanceRoot);
        const string backend = "backend-object";
        var original = shape == "generation" ? MirrorPulseGenerationalPlaceholderIdentity.Create(root, backend, Guid.NewGuid(), "old")
            : MirrorPulsePlaceholderIdentity.CreateForRoot(root, backend, "old").ToCfSharp();
        string encoded = Convert.ToBase64String(original.Encode());
        var retained = MirrorPulseKeepRemotePublicIdentity.ReadOriginal(root, original.ItemId, backend, encoded);
        var selected = MirrorPulseKeepRemotePublicIdentity.SelectRevision(root, original.ItemId, backend, encoded, "selected");
        Assert.AreEqual(original, retained);
        Assert.AreEqual(original.ItemId, selected.ItemId);
        Assert.AreEqual(original.RemoteId, selected.RemoteId);
        Assert.AreEqual("selected", selected.RemoteRevision);
        Assert.AreEqual("old", original.RemoteRevision);
        if (shape != "legacy") Assert.AreNotEqual(backend, selected.RemoteId);
        if (shape == "generation")
        {
            Assert.IsTrue(MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, original, out var before));
            Assert.IsTrue(MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, selected, out var after));
            Assert.AreEqual(before!.Generation, after!.Generation);
        }
    }

    [TestMethod]
    [DataRow("backend")]
    [DataRow("item")]
    [DataRow("instance")]
    [DataRow("root-key")]
    [DataRow("generation-root")]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("noncanonical")]
    public void AForeignOrChangedPublicAddressCannotAuthorizeTheSelectedBackend(string changed)
    {
        var root = Root(RootIdentityScope.InstanceRoot);
        var original = changed == "generation-root" ? MirrorPulseGenerationalPlaceholderIdentity.Create(root, "backend", Guid.NewGuid(), "old")
            : MirrorPulsePlaceholderIdentity.CreateForRoot(root, "backend", "old").ToCfSharp();
        string? encoded = Convert.ToBase64String(original.Encode());
        string backend = changed == "backend" ? "different-backend" : "backend";
        Guid item = changed == "item" ? Guid.NewGuid() : original.ItemId;
        if (changed is "instance" or "root-key" or "generation-root")
            root = new(root.AdapterId, changed == "instance" ? InstanceId.New() : root.InstanceId,
                changed == "generation-root" ? RootId.New() : root.RootId,
                changed == "root-key" ? "other-root" : root.UniquenessKey, root.Label, root.DirectoryName,
                root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
        if (changed == "missing") encoded = null;
        if (changed == "malformed") encoded = "not-base64";
        if (changed == "noncanonical") encoded = " " + encoded;
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseKeepRemotePublicIdentity.ReadOriginal(root, item, backend, encoded));
    }

    private static RootRegistration Root(RootIdentityScope scope) => AdapterRootRegistrationMapper.Map(
        AdapterId.Parse("example.keep-remote-identities"), InstanceId.New(), new("docs", "Docs", "Docs", false),
        RootRegistrationState.Active, identityScope: scope);
}
