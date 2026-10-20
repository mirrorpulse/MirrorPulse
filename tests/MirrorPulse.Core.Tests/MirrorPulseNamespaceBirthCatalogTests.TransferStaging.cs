using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("source-disabled")]
    [DataRow("target-disabled")]
    [DataRow("target-inactive")]
    [DataRow("target-legacy")]
    public async Task TransferMaterializationRequiresBothCurrentCatalogScopesBeforeAnyNativeAccess(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan;
        var original = await catalog.ReadAdapterTopologyAsync();
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, original.Roots);
        if (change is "source-disabled" or "target-disabled")
            await catalog.SetInstanceEnabledAsync(change == "source-disabled" ? plan.SourceRoute.InstanceId : plan.TargetRoute.InstanceId, false);
        else if (change == "target-inactive")
            await catalog.SaveAdapterTopologyAsync(original with
            {
                Roots = original.Roots.Select(root => root.RootId == plan.TargetRoute.RootId ?
                new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey, root.Label, root.DirectoryName,
                    root.CustomEntry, RootRegistrationState.Disabled, root.RegisteredAt, root.IdentityScope) : root).ToArray()
            });
        await using var role = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
        var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(state)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        // The library is deliberately not started or registered; its ordinary fixture directory is empty. The stale
        // positive callback cannot bypass the current catalog or reach native inspection.
        var coordinator = new MirrorPulseNamespaceTransferCoordinator(fileSystem, state, catalog, router, role, actor,
            new MirrorPulseLocalTransferContentStore(fixture.Paths), _ => true,
            id => id == plan.SourceRoute.InstanceId ? plan.SourceRoute.ProtocolVersion : change == "target-legacy" ? 1 : 2);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.MaterializeSourceAsync(plan.Source, plan.TargetParent, CancellationToken.None));
        StringAssert.Contains(error.Message, change == "target-legacy" ? "enabled original instance" : "current catalog registration");
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Paths.SyncRootPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Paths.DataRootPath, "state", "transfer-inputs")));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId)); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalTransferContentSurvivesCatalogAndStorageOwnersWithoutSharingImportInputs(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        string sourcePath = Path.Combine(fixture.Paths.DataRootPath, "source.bin");
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first, sameInstance)).Plan;
            await File.WriteAllBytesAsync(sourcePath, TransferBytes);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            source.Position = 17;
            var content = await new MirrorPulseLocalTransferContentStore(fixture.Paths).StageRetainedAsync(plan.PrivateContentId, source);
            Assert.AreEqual(17L, source.Position); Assert.IsTrue(source.CanRead);
            Assert.AreEqual(plan.Length, content.Length); Assert.AreEqual(plan.Sha256, content.Sha256);
            await first.PrepareNamespaceTransferAsync(plan);
            // The same GUID in another staging namespace does not alias these retained transfer bytes.
            string input = Path.Combine(fixture.Paths.DataRootPath, "other-input.bin"); await File.WriteAllBytesAsync(input, [8, 9]);
            var import = new MirrorPulseLocalImportStagingStore(fixture.Paths);
            var imported = await import.StageAsync(plan.PrivateContentId, input); import.RemoveCompleted(plan.PrivateContentId);
            Assert.AreNotEqual(content, imported);
        }
        File.Delete(sourcePath);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        plan = (await reopened.ReadNamespaceTransferAsync(plan.OperationId))!.Plan;
        await using var retained = await new MirrorPulseLocalTransferContentStore(fixture.Paths).OpenLeaseAsync(plan);
        Assert.AreEqual(plan.OperationId, retained.OperationId); Assert.AreEqual(plan.PrivateContentId, retained.PrivateContentId);
        Assert.AreEqual(0L, retained.Data.Position); Assert.IsFalse(retained.Data.CanWrite);
        using var received = new MemoryStream(); await retained.Data.CopyToAsync(received);
        CollectionAssert.AreEqual(TransferBytes, received.ToArray());
        string privatePath = TransferInputPath(fixture, plan);
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(privatePath, [0]));
        Assert.Throws<IOException>(() => File.Delete(privatePath));
        var uploads = new TransferUploads();
        var accepted = await new MirrorPulseTransferCopyExecutor(reopened, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1)))
            .CopyAsync(plan.OperationId, retained.Data);
        Assert.AreEqual("accepted", accepted.AcceptedRevision); Assert.AreEqual(1, uploads.Calls);
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await reopened.ReadMutationAsync(plan.DeleteOperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("missing")]
    public async Task DamagedOriginalTransferStagingCannotReachTheFirstWorkerInvocation(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan;
        string sourcePath = Path.Combine(fixture.Paths.DataRootPath, "source.bin"); await File.WriteAllBytesAsync(sourcePath, TransferBytes);
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await new MirrorPulseLocalTransferContentStore(fixture.Paths).StageRetainedAsync(plan.PrivateContentId, source);
        await catalog.PrepareNamespaceTransferAsync(plan);
        string privatePath = TransferInputPath(fixture, plan);
        if (damage == "missing") File.Delete(privatePath);
        else
        {
            byte[] changed = damage == "length" ? [0] : TransferBytes.ToArray(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(privatePath, changed);
        }
        var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
        if (damage == "missing") await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => inputs.OpenLeaseAsync(plan));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => inputs.OpenLeaseAsync(plan));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.CopyStartedAt);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        CollectionAssert.AreEqual(TransferBytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [TestMethod]
    public async Task RepeatedTransferStagingCannotReplaceOriginalPrivateBytesOrCloseItsSourceHandle()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan;
        string path = Path.Combine(fixture.Paths.DataRootPath, "source.bin"); await File.WriteAllBytesAsync(path, TransferBytes);
        var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            source.Position = 31; await inputs.StageRetainedAsync(plan.PrivateContentId, source);
            await inputs.StageRetainedAsync(plan.PrivateContentId, source); Assert.AreEqual(31L, source.Position);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => inputs.StageRetainedAsync(plan.PrivateContentId, source, canceled.Token));
            Assert.AreEqual(31L, source.Position); Assert.IsTrue(source.CanRead);
        }
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => inputs.StageRetainedAsync(plan.PrivateContentId, source));
        await using var original = await inputs.OpenLeaseAsync(plan);
        Assert.AreEqual(TransferBytes[0], original.Data.ReadByte());
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(TransferInputPath(fixture, plan))!));
    }

    [TestMethod]
    public async Task OriginalTransferPrivateBytesRemainAvailableAfterLostUploadReplyAndCatalogRestart()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; var uploads = new TransferUploads();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first)).Plan;
            string path = Path.Combine(fixture.Paths.DataRootPath, "source.bin"); await File.WriteAllBytesAsync(path, TransferBytes);
            var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await inputs.StageRetainedAsync(plan.PrivateContentId, source);
            await first.PrepareNamespaceTransferAsync(plan);
            uploads.OnUpload = (_, _) => ValueTask.FromException<string>(new IOException("Original reply was lost."));
            await using var input = await inputs.OpenLeaseAsync(plan);
            await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseTransferCopyExecutor(first, uploads,
                new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.OperationId, input.Data));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var retained = await new MirrorPulseLocalTransferContentStore(fixture.Paths).OpenLeaseAsync(plan);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            new MirrorPulseTransferCopyExecutor(second, uploads).CopyAsync(plan.OperationId, retained.Data));
        Assert.AreEqual(1, uploads.Calls); Assert.IsNull((await second.ReadNamespaceTransferAsync(plan.OperationId))!.CopyAcceptedAt);
        Assert.AreEqual(TransferBytes[0], retained.Data.ReadByte()); Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
    }

    private static string TransferInputPath(Fixture fixture, MirrorPulseNamespaceTransferPlan plan) =>
        Path.Combine(fixture.Paths.DataRootPath, "state", "transfer-inputs", plan.PrivateContentId.ToString("N") + ".bin");
}
