using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeControlledSubtreeRetirementRecoversNativeAndMembershipBoundariesAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.controlled-retirement"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseNamespaceMemberRegistration folder = null!, child = null!;
        MirrorPulseNamespaceRetirementBatch batch = null!;
        DateTimeOffset? firstStart = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 8, 9]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1, 2, 3], deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    var docs = members.Values.Single(value => value.IsDirectory && value.RootId is not null);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    folder = await creation.CreateAsync(Guid.NewGuid(), docs.MemberId, "retire", true, deadline.Token);
                    child = await creation.CreateAsync(Guid.NewGuid(), folder.MemberId, "latest.bin", false, deadline.Token);
                    await File.WriteAllBytesAsync(fileSystem.GetFile(child.RelativePath).FullPath, [9, 8, 7, 6], deadline.Token);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() =>
                        Directory.Delete(fileSystem.GetDirectory(folder.RelativePath).FullPath, true), deadline.Token));
                    CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, await File.ReadAllBytesAsync(fileSystem.GetFile(child.RelativePath).FullPath, deadline.Token));
                    var preparation = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, role, actor);
                    batch = await preparation.PrepareBatchAsync(Guid.NewGuid(), folder.MemberId, deadline.Token);
                    Assert.HasCount(2, batch.Entries);
                }
                await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(fileSystem.Root, router, deadline.Token);
                var selected = owner == 0 ? MirrorPulseNamespaceRetirementCheckpoint.AfterNativeDeletion : MirrorPulseNamespaceRetirementCheckpoint.AfterNativeReceipt;
                var retirement = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, role, actor)
                {
                    Checkpoint = owner < 2 ? checkpoint => checkpoint == selected
                        ? ValueTask.FromException(new InvalidOperationException("Injected retirement interruption.")) : ValueTask.CompletedTask : null
                };
                if (owner < 2)
                {
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => retirement.ExecuteBatchAsync(batch.OperationId, deadline.Token).AsTask());
                    var receipt = await catalog.ReadNamespaceRetirementAsync(batch.MemberOperationId(child.MemberId), deadline.Token);
                    Assert.IsNotNull(receipt); Assert.IsNotNull(receipt.StartedAt);
                    firstStart ??= receipt.StartedAt;
                    Assert.AreEqual(firstStart, receipt.StartedAt);
                    Assert.AreEqual(owner == 0 ? MirrorPulseNamespaceRetirementPhase.Prepared : MirrorPulseNamespaceRetirementPhase.NativeDeleted, receipt.Phase);
                    var deleted = await fileSystem.GetFile(child.RelativePath).InspectAsync(deadline.Token);
                    Assert.IsFalse(deleted.Exists); Assert.IsTrue(deleted.IsTombstone);
                    Assert.AreEqual(receipt.StartedItemId, deleted.ItemId);
                    Assert.IsTrue((await fileSystem.GetDirectory(folder.RelativePath).InspectAsync(deadline.Token)).Exists);
                    Assert.AreEqual(child, await catalog.ReadNamespaceMemberChildAsync(folder.MemberId, "latest.bin", deadline.Token));
                    CollectionAssert.Contains((await catalog.ReadNamespaceRecoveryRootsAsync(deadline.Token)).ToArray(), root.RootId);
                }
                else
                {
                    var completed = await retirement.ExecuteBatchAsync(batch.OperationId, deadline.Token);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, completed.Phase);
                    Assert.AreEqual(completed.OperationId, (await retirement.ExecuteBatchAsync(batch.OperationId, deadline.Token)).OperationId);
                    Assert.IsFalse((await fileSystem.GetDirectory(folder.RelativePath).InspectAsync(deadline.Token)).Exists);
                    Assert.IsFalse((await catalog.ReadNamespaceRecoveryRootsAsync(deadline.Token)).Contains(root.RootId));
                    Assert.AreEqual(child, await catalog.ReadNamespaceMemberAsync(child.MemberId, deadline.Token));
                    var receipt = await catalog.ReadNamespaceRetirementAsync(batch.MemberOperationId(child.MemberId), deadline.Token);
                    Assert.AreEqual(firstStart, receipt!.StartedAt);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, receipt.Phase);
                }
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, deadline.Token));
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ControlledSubtreeRetirement: productionCoordinator=True; threeOwners=True; nativeInterruption=True; receiptInterruption=True; publicTombstones=True; leafFirst=True; immutableStart=True; originalMemberRetained=True; rootFenceReleasedOnlyAfterSettlement=True; unrelatedBytesRetained=True; sourceReads=0; hostIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
