using System.Runtime.Versioning;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseRootRouterLifecycleTests
{
    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    [DataRow(RootRegistrationState.Active, RootRegistrationState.Active)]
    [DataRow(RootRegistrationState.Active, RootRegistrationState.Disabled)]
    [DataRow(RootRegistrationState.Disabled, RootRegistrationState.Active)]
    [DataRow(RootRegistrationState.Disabled, RootRegistrationState.Disabled)]
    public void LocalLifecycleRefreshPublishesEveryIndexWithoutChangingNamesOrOldViews(RootRegistrationState before, RootRegistrationState after)
    {
        var original = Root("Docs", before); var peer = Root("Peer", RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(SyncRoot, [original, peer], [new(original.RootId, "Former")]);
        var old = router.Registrations; var current = Changed(original, after);
        router.RefreshLocalCommandLifecycleRegistration(current);
        Assert.AreEqual(current, router.GetRegistration(current.InstanceId, current.UniquenessKey));
        Assert.AreEqual(current, router.FindRegistration(current.RootId));
        Guid publicId = MirrorPulsePlaceholderIdentity.Create(current.InstanceId, $"mirrorpulse-root:{current.RootId}").ToCfSharp().ItemId;
        Assert.AreEqual(current, router.FindRootByPublicItemId(publicId));
        Assert.AreSame(peer, router.GetRegistration(peer.InstanceId, peer.UniquenessKey));
        Assert.AreSame(original, old.Single(root => root.RootId == original.RootId));
        Assert.AreEqual(before, old.Single(root => root.RootId == original.RootId).State);
        Assert.AreEqual(current.InstanceId, router.ResolvePath("Former/file").InstanceId);
        Assert.ThrowsExactly<FileNotFoundException>(() => router.ResolveCurrentPath("Former/file"));
        if (after == RootRegistrationState.Disabled)
            Assert.ThrowsExactly<IOException>(() => router.ResolveUploadPath(current.InstanceId, current.UniquenessKey, "file"));
        else
            Assert.AreEqual(Path.Combine("Docs", "file"), Path.GetRelativePath(SyncRoot, router.ResolveUploadPath(current.InstanceId, current.UniquenessKey, "file")));
        var published = router.Registrations; router.RefreshLocalCommandLifecycleRegistration(current);
        Assert.AreSame(published, router.Registrations);
    }

    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    [DataRow("adapter")]
    [DataRow("instance")]
    [DataRow("root")]
    [DataRow("key")]
    [DataRow("label")]
    [DataRow("directory")]
    [DataRow("custom")]
    [DataRow("registered-at")]
    [DataRow("identity-scope")]
    [DataRow("pending")]
    [DataRow("conflicted")]
    [DataRow("removed")]
    public void LocalLifecycleRefreshRejectsAnyScopeIdentityNameOrUnsupportedStateChange(string damage)
    {
        var original = Root("Docs", RootRegistrationState.Active); var router = new MirrorPulseRootRouter(SyncRoot, [original]);
        var old = router.Registrations;
        var altered = new RootRegistration(damage == "adapter" ? AdapterId.Parse("example.other") : original.AdapterId,
            damage == "instance" ? InstanceId.New() : original.InstanceId, damage == "root" ? RootId.New() : original.RootId,
            damage == "key" ? "other" : original.UniquenessKey, damage == "label" ? "Other" : original.Label,
            damage == "directory" ? "Other" : original.DirectoryName, damage == "custom" || original.CustomEntry,
            damage switch
            {
                "pending" => RootRegistrationState.Pending,
                "conflicted" => RootRegistrationState.Conflicted,
                "removed" => RootRegistrationState.Removed,
                _ => RootRegistrationState.Disabled
            },
            damage == "registered-at" ? original.RegisteredAt.AddSeconds(1) : original.RegisteredAt,
            damage == "identity-scope" ? RootIdentityScope.LegacyInstance : original.IdentityScope);
        Assert.ThrowsExactly<InvalidOperationException>(() => router.RefreshLocalCommandLifecycleRegistration(altered));
        Assert.AreSame(old, router.Registrations); Assert.AreSame(original, router.FindRegistration(original.RootId));
        Assert.AreSame(original, router.GetRegistration(original.InstanceId, original.UniquenessKey));
    }

    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    public void LifecycleRefreshPreservesAnAmbiguousBackendKeyInsteadOfSelectingOneRoot()
    {
        var original = Root("Docs", RootRegistrationState.Active);
        var duplicate = new RootRegistration(original.AdapterId, original.InstanceId, RootId.New(), original.UniquenessKey,
            "Other", "Other", false, RootRegistrationState.Active, original.RegisteredAt, original.IdentityScope);
        var router = new MirrorPulseRootRouter(SyncRoot, [original, duplicate]);
        var disabled = Changed(original, RootRegistrationState.Disabled); router.RefreshLocalCommandLifecycleRegistration(disabled);
        Assert.ThrowsExactly<InvalidOperationException>(() => router.GetRegistration(original.InstanceId, original.UniquenessKey));
        Assert.AreSame(disabled, router.FindRegistration(original.RootId)); Assert.AreSame(duplicate, router.FindRegistration(duplicate.RootId));
    }

    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    public void LifecycleRefreshCannotRestoreAnAlreadyRemovedRoute()
    {
        var original = Root("Docs", RootRegistrationState.Active); var router = new MirrorPulseRootRouter(SyncRoot, [original]);
        router.ReplaceRegistrations([]); var empty = router.Registrations;
        Assert.ThrowsExactly<InvalidOperationException>(() => router.RefreshLocalCommandLifecycleRegistration(Changed(original, RootRegistrationState.Disabled)));
        Assert.AreSame(empty, router.Registrations); Assert.IsNull(router.FindRegistration(original.RootId));
    }

    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    public async Task ConcurrentLifecycleRefreshesRetainBothIndependentRootUpdates()
    {
        var left = Root("Left", RootRegistrationState.Active); var right = Root("Right", RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(SyncRoot, [left, right]);
        await Task.WhenAll(Task.Run(() => Update(left)), Task.Run(() => Update(right)));
        Assert.AreEqual(RootRegistrationState.Disabled, router.GetRegistration(left.InstanceId, left.UniquenessKey).State);
        Assert.AreEqual(RootRegistrationState.Disabled, router.GetRegistration(right.InstanceId, right.UniquenessKey).State);
        void Update(RootRegistration root)
        {
            for (int index = 0; index < 300; index++)
                router.RefreshLocalCommandLifecycleRegistration(Changed(root, index % 2 == 0 ? RootRegistrationState.Active : RootRegistrationState.Disabled));
        }
    }

    [TestMethod]
    [TestCategory("RootLabelLifecycleRoute")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActualCatalogLifecycleSwitchLeavesACachedRouteUntilItsValidatedLocalRefresh(bool initiallyEnabled)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "MirrorPulse-router-lifecycle-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(temporary, "sync"), Path.Combine(temporary, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var manifest = new AdapterManifest(1, AdapterId.Parse("example.lifecycle"), "Example", "1.0.0", new(1, 1),
                new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
            var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(temporary, "installed"),
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            await catalog.AddInstallationAsync(installation);
            var instance = await catalog.CreateInstanceAsync(installation.InstallId, "Example", new Dictionary<string, string>(), [],
                Path.Combine(temporary, "worker-files"), Path.Combine(temporary, "worker-transfers"), initiallyEnabled);
            var original = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [original]);
            await catalog.SetInstanceEnabledAsync(instance.InstanceId, !initiallyEnabled);
            var current = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
            Assert.AreNotEqual(current, router.FindRegistration(current.RootId));
            Assert.AreEqual(original, router.FindRegistration(current.RootId));
            router.RefreshLocalCommandLifecycleRegistration(current);
            Assert.AreEqual(current, router.FindRegistration(current.RootId));
            Assert.AreEqual(original.RootId, current.RootId); Assert.AreEqual(original.DirectoryName, current.DirectoryName);
            Assert.AreEqual(original.RegisteredAt, current.RegisteredAt);
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath)); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static readonly string SyncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-router-lifecycle-path", Guid.NewGuid().ToString("N"));
    private static RootRegistration Root(string label, RootRegistrationState state) =>
        AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.lifecycle"), InstanceId.New(), new("docs", label, label, false), state,
            registeredAt: DateTimeOffset.UnixEpoch, identityScope: RootIdentityScope.InstanceRoot);
    private static RootRegistration Changed(RootRegistration root, RootRegistrationState state) =>
        new(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey, root.Label, root.DirectoryName, root.CustomEntry, state, root.RegisteredAt, root.IdentityScope);
}
