using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseRootRouterTests
{
    private static readonly string[] ExpectedTopLevelNames = ["Backup", "Documents", "Photos"];

    [TestMethod]
    public void HistoricalJournalNamesFollowTheStableRootButCannotAuthorizeNativeCallbacks()
    {
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        InstanceId instance = InstanceId.New();
        RootRegistration original = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.labels"), instance,
            new AdapterRootDefinition("docs", "Documents", "Documents", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var renamed = new RootRegistration(original.AdapterId, original.InstanceId, original.RootId, original.UniquenessKey,
            "My Files", "My Files", original.CustomEntry, original.State, original.RegisteredAt, original.IdentityScope);
        var router = new MirrorPulseRootRouter(syncRoot, [original]);
        byte[] identity = router.CreateFileIdentity(instance, "docs", "remote-file", "v1").Encode();
        MirrorPulseManagedRootName[] history = [new(original.RootId, "Documents"), new(original.RootId, "My Files")];
        router.ReplaceRegistrations([renamed], history);
        Assert.AreEqual(original.RootId, router.GetRegistration(instance, "docs").RootId);
        Assert.AreEqual("note.txt", router.ResolvePath("Documents/note.txt").RelativePath);
        Assert.AreEqual("docs", router.ResolvePath("Documents/note.txt").RootKey);
        Assert.AreEqual(Path.Combine(syncRoot, "My Files", "note.txt"), router.ResolveUploadPath(instance, "docs", "note.txt"));
        Assert.AreEqual(instance, router.Resolve("My Files/note.txt", identity).InstanceId);
        Assert.ThrowsExactly<FileNotFoundException>(() => router.Resolve("Documents/note.txt", identity));
        Assert.AreEqual(CloudProviderPolicyDecision.Allow, MirrorPulseRootNamespacePolicy.ApproveDelete(router, "Documents"));
        Assert.AreEqual(CloudProviderPolicyDecision.Deny, MirrorPulseRootNamespacePolicy.ApproveDelete(router, "My Files"));
        RootRegistration collision = CreateRoot(InstanceId.New(), "Documents");
        Assert.ThrowsExactly<InvalidDataException>(() => router.ReplaceRegistrations([renamed, collision], history));
        Assert.AreEqual("My Files", router.CreateRootPage().Children.Single().Name);
        var reopened = new MirrorPulseRootRouter(syncRoot, [renamed], history);
        Assert.AreEqual(router.ResolvePath("Documents/note.txt"), reopened.ResolvePath("Documents/note.txt"));
        CollectionAssert.AreEqual(identity, reopened.CreateFileIdentity(instance, "docs", "remote-file", "v1").Encode());
    }

    [TestMethod]
    public async Task OneSyncRootListsMultipleCopiesAndMultipleRootsPerInstance()
    {
        var first = InstanceId.New();
        var second = InstanceId.New();
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var roots = AdapterRootRegistrationMapper.MapAll(
            AdapterId.Parse("example.drive"), first,
            [new AdapterRootDefinition("docs", "Documents", "Documents", false),
             new AdapterRootDefinition("photos", "Photos", "Photos", false)],
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot).Concat(AdapterRootRegistrationMapper.MapAll(
                AdapterId.Parse("example.drive"), second,
                [new AdapterRootDefinition("backup", "Backup", "Backup", false)],
                RootRegistrationState.Active));
        var router = new MirrorPulseRootRouter(syncRoot, roots);
        var source = new RecordingDirectorySource();
        var transport = new RecordingRangeTransport();
        var provider = new MirrorPulseDemandProvider(router, transport, source);

        CloudProviderDirectoryPage top = await provider.FetchChildrenAsync(syncRoot, ReadOnlyMemory<byte>.Empty, null);
        CollectionAssert.AreEqual(ExpectedTopLevelNames, top.Children.Select(child => child.Name).ToArray());
        Assert.IsTrue(top.IsComplete);
        Assert.AreEqual(3, top.Children.Select(child => child.Identity.ItemId).Distinct().Count());

        CloudPlaceholderSpec documents = top.Children.Single(child => child.Name == "Documents");
        CloudProviderDirectoryPage page = await provider.FetchChildrenAsync(
            Path.Combine(syncRoot, "Documents"), documents.Identity.Encode(), null);
        Assert.IsEmpty(page.Children);
        Assert.AreEqual(first, source.InstanceId);
        Assert.AreEqual(string.Empty, source.RelativePath);

        byte[] fileIdentity = MirrorPulsePlaceholderIdentity.Create(second, "file-1").Encode();
        await using Stream stream = await provider.OpenReadAsync(
            Path.Combine(syncRoot, "Backup", "sub", "file.txt"), fileIdentity, 3, 0, 2);
        byte[] bytes = new byte[2];
        await stream.ReadExactlyAsync(bytes);
        Assert.AreEqual(second, transport.Request?.InstanceId);
        Assert.AreEqual(Path.Combine("sub", "file.txt"), transport.Request?.NormalizedPath);
    }

    [TestMethod]
    public void DuplicateTopLevelLabelAcrossInstancesIsRejected()
    {
        var first = CreateRoot(InstanceId.New(), "Shared");
        var second = CreateRoot(InstanceId.New(), "shared");

        Assert.ThrowsExactly<InvalidDataException>(() => new MirrorPulseRootRouter(
            Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N")),
            [first, second]));
    }

    [TestMethod]
    public void DuplicateLabelWithinOneInstanceRejectsReplacementWithoutChangingExistingRouter()
    {
        var instance = InstanceId.New();
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var existing = new MirrorPulseRootRouter(syncRoot, [CreateRoot(instance, "Documents")]);
        RootRegistration duplicate = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.drive"), instance,
            new AdapterRootDefinition("second-key", "documents", "documents", false),
            RootRegistrationState.Active);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new MirrorPulseRootRouter(syncRoot, [CreateRoot(instance, "Documents"), duplicate]));

        CloudProviderDirectoryPage preserved = existing.CreateRootPage();
        Assert.HasCount(1, preserved.Children);
        Assert.AreEqual("Documents", preserved.Children[0].Name);
        Assert.AreEqual(instance, existing.ResolvePath(Path.Combine(syncRoot, "Documents")).InstanceId);
    }

    [TestMethod]
    public async Task DisabledInstanceKeepsItsDirectoryButRejectsRemoteFetch()
    {
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var instance = InstanceId.New();
        RootRegistration disabled = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.drive"), instance,
            new AdapterRootDefinition("offline", "Offline", "Offline", false),
            RootRegistrationState.Disabled);
        var router = new MirrorPulseRootRouter(syncRoot, [disabled]);
        var provider = new MirrorPulseDemandProvider(router, new RecordingRangeTransport(),
            new RecordingDirectorySource());

        CloudProviderDirectoryPage root = await provider.FetchChildrenAsync(
            syncRoot, ReadOnlyMemory<byte>.Empty, null);
        Assert.HasCount(1, root.Children);
        Assert.AreEqual("Offline", root.Children[0].Name);
        Assert.AreEqual(instance, router.ResolvePath(Path.Combine(syncRoot, "Offline", "queued.txt")).InstanceId);
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.FetchChildrenAsync(
            Path.Combine(syncRoot, "Offline"), root.Children[0].Identity.Encode(), null).AsTask());

        RootRegistration collision = CreateRoot(InstanceId.New(), "offline");
        Assert.ThrowsExactly<InvalidDataException>(() => new MirrorPulseRootRouter(syncRoot,
            [disabled, collision]));
    }

    [TestMethod]
    public void WrongInstanceIdentityCannotCrossFirstLevelDirectory()
    {
        var first = InstanceId.New();
        var second = InstanceId.New();
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var router = new MirrorPulseRootRouter(syncRoot, [CreateRoot(first, "One"), CreateRoot(second, "Two")]);
        byte[] identity = MirrorPulsePlaceholderIdentity.Create(second, "file").Encode();

        Assert.ThrowsExactly<InvalidDataException>(() => router.Resolve(Path.Combine(syncRoot, "One", "file"), identity));
        Assert.ThrowsExactly<InvalidDataException>(() => router.Resolve(Path.Combine(syncRoot, "..", "outside"), identity));
    }

    [TestMethod]
    public void JournalUploadPathIncludesItsFirstLevelRootAndCannotEscapeIt()
    {
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var first = InstanceId.New();
        var second = InstanceId.New();
        var router = new MirrorPulseRootRouter(syncRoot,
            [CreateRoot(first, "Documents"), CreateRoot(second, "Backup")]);

        Assert.AreEqual(Path.Combine(syncRoot, "Documents", "sub", "note.txt"),
            router.ResolveUploadPath(first, "Documents", "sub/note.txt"));
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            router.ResolveUploadPath(second, "Documents", "note.txt"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            router.ResolveUploadPath(first, "Documents", "../Backup/note.txt"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            router.ResolveUploadPath(first, "Documents", Path.Combine(syncRoot, "outside.txt")));

        RootRegistration offline = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.drive"), first,
            new AdapterRootDefinition("offline", "Offline", "Offline", false),
            RootRegistrationState.Disabled);
        Assert.ThrowsExactly<IOException>(() => new MirrorPulseRootRouter(syncRoot, [offline])
            .ResolveUploadPath(first, "offline", "queued.txt"));
    }

    [TestMethod]
    public void VolumeRootedNativeCallbackResolvesOnSyncRootVolume()
    {
        string syncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var instance = InstanceId.New();
        var router = new MirrorPulseRootRouter(syncRoot, [CreateRoot(instance, "Documents")]);
        string fullPath = Path.Combine(syncRoot, "Documents", "note.txt");
        string volumeRoot = Path.GetPathRoot(syncRoot)!;
        string volumeRelativePath = Path.DirectorySeparatorChar + fullPath[volumeRoot.Length..];

        Assert.AreEqual(instance, router.ResolvePath(volumeRelativePath).InstanceId);
        Assert.AreEqual("note.txt", router.ResolvePath(volumeRelativePath).RelativePath);
        Assert.ThrowsExactly<InvalidDataException>(() => router.ResolvePath(
            Path.DirectorySeparatorChar + Path.Combine("outside", "note.txt")));
    }

    private static RootRegistration CreateRoot(InstanceId instanceId, string label) =>
        AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.drive"), instanceId,
            new AdapterRootDefinition(label, label, label, false),
            RootRegistrationState.Active);

    private sealed class RecordingDirectorySource : IMirrorPulseDirectoryPageSource
    {
        public InstanceId InstanceId { get; private set; }

        public string? RelativePath { get; private set; }

        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
            InstanceId instanceId,
            string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            InstanceId = instanceId;
            RelativePath = normalizedPath;
            return ValueTask.FromResult(new CloudRemoteDirectoryPage([]));
        }
    }

    private sealed class RecordingRangeTransport : IMirrorPulseWorkerRangeTransport
    {
        public MirrorPulseWorkerReadRangeRequest? Request { get; private set; }

        public ValueTask<Stream> ReadRangeAsync(
            MirrorPulseWorkerReadRangeRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult<Stream>(new MemoryStream([1, 2], writable: false));
        }
    }
}
