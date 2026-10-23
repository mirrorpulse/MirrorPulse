using System.Runtime.Versioning;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseRootRouterSnapshotTests
{
    [TestMethod]
    public void OnePublishedSnapshotReusesAnImmutableListIndependentOfTheInputArray()
    {
        var original = Root(InstanceId.New(), "docs", "Documents");
        RootRegistration[] input = [original]; var router = Router(input);
        var first = router.Registrations;
        Assert.AreSame(first, router.Registrations);
        input[0] = Root(InstanceId.New(), "other", "Other");
        Assert.AreSame(original, first[0]);
        Assert.AreSame(original, router.GetRegistration(original.InstanceId, "docs"));
        Assert.AreSame(original, router.FindRegistration(original.RootId));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<RootRegistration>)first)[0] = input[0]);
        Assert.AreSame(original, router.Registrations[0]);
    }

    [TestMethod]
    [DataRow(RootRegistrationState.Active)]
    [DataRow(RootRegistrationState.Disabled)]
    public void ReplacementPublishesEveryIndexAndRetainsTheOldImmutableView(RootRegistrationState state)
    {
        var original = Root(InstanceId.New(), "docs", "Documents"); var router = Router([original]);
        var old = router.Registrations;
        var renamed = Changed(original, "Archive", state);
        router.ReplaceRegistrations([renamed], [new(original.RootId, "Documents"), new(original.RootId, "Archive")]);
        Assert.AreNotSame(old, router.Registrations);
        Assert.AreSame(router.Registrations, router.Registrations);
        Assert.AreSame(original, old[0]); Assert.AreEqual("Documents", old[0].Label);
        Assert.AreSame(renamed, router.GetRegistration(original.InstanceId, "docs"));
        Assert.AreSame(renamed, router.FindRegistration(original.RootId));
        Assert.AreEqual(state, router.Registrations[0].State);
        Assert.AreEqual(original.InstanceId, router.ResolvePath("Documents/file").InstanceId);
        Assert.ThrowsExactly<FileNotFoundException>(() => router.ResolveCurrentPath("Documents/file"));
        if (state == RootRegistrationState.Disabled)
            Assert.ThrowsExactly<IOException>(() => router.ResolveUploadPath(original.InstanceId, "docs", "file"));
        else Assert.AreEqual(Path.Combine("Archive", "file"), Path.GetRelativePath(SyncRoot, router.ResolveUploadPath(original.InstanceId, "docs", "file")));
    }

    [TestMethod]
    public void RemovalCannotResolveAFormerRouteOrStableIdThroughAnOldView()
    {
        var original = Root(InstanceId.New(), "docs", "Documents"); var router = Router([original]);
        var old = router.Registrations; router.ReplaceRegistrations([]);
        Assert.HasCount(1, old); Assert.IsEmpty(router.Registrations);
        Assert.IsNull(router.FindRegistration(original.RootId));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.GetRegistration(original.InstanceId, "docs"));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.ResolvePath("Documents/file"));
    }

    [TestMethod]
    public void RouteKeysRemainOrdinalAndScopedToTheirOriginalInstance()
    {
        var instance = InstanceId.New(); var other = InstanceId.New();
        var lower = Root(instance, "docs", "Lower"); var upper = Root(instance, "DOCS", "Upper");
        var second = Root(other, "docs", "Second"); var router = Router([lower, upper, second]);
        Assert.AreSame(lower, router.GetRegistration(instance, "docs"));
        Assert.AreSame(upper, router.GetRegistration(instance, "DOCS"));
        Assert.AreSame(second, router.GetRegistration(other, "docs"));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.GetRegistration(instance, "Docs"));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.GetRegistration(InstanceId.New(), "docs"));
    }

    [TestMethod]
    public void AmbiguousRoutesRetainLookupTimeFailureAndCannotChooseTheFirstObject()
    {
        var original = Root(InstanceId.New(), "docs", "Documents");
        var duplicate = new RootRegistration(original.AdapterId, original.InstanceId, RootId.New(), "docs", "Other", "Other",
            false, RootRegistrationState.Active, original.RegisteredAt, RootIdentityScope.InstanceRoot);
        var unrelated = Root(InstanceId.New(), "docs", "Unrelated");
        var router = Router([original, duplicate, unrelated]);
        Assert.HasCount(3, router.Registrations);
        Assert.AreSame(original, router.FindRegistration(original.RootId));
        Assert.AreSame(duplicate, router.FindRegistration(duplicate.RootId));
        Assert.ThrowsExactly<InvalidOperationException>(() => router.GetRegistration(original.InstanceId, "docs"));
        Assert.AreSame(unrelated, router.GetRegistration(unrelated.InstanceId, "docs"));
    }

    [TestMethod]
    public void RejectedReplacementCannotPublishSomeNewIndexesAlongsideTheOldNames()
    {
        var original = Root(InstanceId.New(), "docs", "Documents"); var router = Router([original]); var old = router.Registrations;
        var collision = Root(InstanceId.New(), "other", "documents");
        Assert.ThrowsExactly<InvalidDataException>(() => router.ReplaceRegistrations([original, collision]));
        Assert.AreSame(old, router.Registrations);
        Assert.AreSame(original, router.GetRegistration(original.InstanceId, "docs"));
        Assert.AreSame(original, router.FindRegistration(original.RootId)); Assert.IsNull(router.FindRegistration(collision.RootId));
    }

    [TestMethod]
    [DataRow(RootRegistrationState.Pending)]
    [DataRow(RootRegistrationState.Conflicted)]
    [DataRow(RootRegistrationState.Removed)]
    public void NonRoutableStatesRemainAbsentFromEveryIndex(RootRegistrationState state)
    {
        var root = Root(InstanceId.New(), "docs", "Documents", state); var router = Router([root]);
        Assert.IsEmpty(router.Registrations); Assert.IsNull(router.FindRegistration(root.RootId));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.GetRegistration(root.InstanceId, "docs"));
    }

    [TestMethod]
    public async Task ConcurrentReplacementCannotMutateAnObservedViewOrExposeAPartialRoute()
    {
        var original = Root(InstanceId.New(), "docs", "Documents"); var disabled = Changed(original, "Archive", RootRegistrationState.Disabled);
        var router = Router([original]); var old = router.Registrations;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 1000; i++) router.ReplaceRegistrations([i % 2 == 0 ? disabled : original]);
        }, CancellationToken.None);
        start.SetResult();
        for (int i = 0; i < 2000; i++)
        {
            var route = router.GetRegistration(original.InstanceId, "docs");
            Assert.IsTrue(ReferenceEquals(route, original) || ReferenceEquals(route, disabled));
            var retained = router.Registrations; Assert.HasCount(1, retained);
            var member = retained[0]; Assert.AreSame(member, retained[0]);
            Assert.IsNotNull(router.FindRegistration(original.RootId));
        }
        await writer; Assert.AreSame(original, old[0]);
    }

    private static readonly string SyncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", "routing-snapshot-" + Guid.NewGuid().ToString("N"));
    private static MirrorPulseRootRouter Router(IEnumerable<RootRegistration> roots) => new(SyncRoot, roots);
    private static RootRegistration Root(InstanceId instance, string key, string label, RootRegistrationState state = RootRegistrationState.Active) =>
        AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.snapshot"), instance, new AdapterRootDefinition(key, label, label, false),
            state, registeredAt: DateTimeOffset.UnixEpoch, identityScope: RootIdentityScope.InstanceRoot);
    private static RootRegistration Changed(RootRegistration root, string label, RootRegistrationState state) =>
        new(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey, label, label, root.CustomEntry, state, root.RegisteredAt, root.IdentityScope);
}
