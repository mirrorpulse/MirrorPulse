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
    public async Task NativeControlledCreationRegistersEmptyFilesDirectoriesAndNestedMembersAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.controlled-creation"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var requests = new List<(Guid Operation, Guid Parent, string Name, bool Directory)>();
        var created = new List<MirrorPulseNamespaceMemberRegistration>();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 8, 9]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
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
                }
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var coordinator = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    var docs = members.Values.Single(value => value.IsDirectory && value.RootId is not null);
                    requests.Add((Guid.NewGuid(), docs.MemberId, "empty.bin", false));
                    requests.Add((Guid.NewGuid(), docs.MemberId, "folder", true));
                    foreach (var request in requests)
                        created.Add(await coordinator.CreateAsync(request.Operation, request.Parent, request.Name, request.Directory, deadline.Token));
                    var folder = created.Single(value => value.IsDirectory);
                    requests.Add((Guid.NewGuid(), folder.MemberId, "nested.bin", false));
                    created.Add(await coordinator.CreateAsync(requests[^1].Operation, folder.MemberId, "nested.bin", false, deadline.Token));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateAsync(requests[0].Operation,
                        docs.MemberId, "different.bin", false, deadline.Token).AsTask());
                    await Assert.ThrowsAsync<IOException>(() => coordinator.CreateAsync(Guid.NewGuid(), docs.MemberId,
                        "original.bin", false, deadline.Token).AsTask());
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(
                        fileSystem.GetFile("Docs/uncontrolled.bin").FullPath, "uncontrolled", deadline.Token));
                }
                for (int index = 0; index < requests.Count; index++)
                {
                    var request = requests[index]; var member = created[index];
                    Assert.AreEqual(member, await coordinator.CreateAsync(request.Operation, request.Parent, request.Name, request.Directory, deadline.Token));
                    CloudItem item = member.IsDirectory ? fileSystem.GetDirectory(member.RelativePath) : fileSystem.GetFile(member.RelativePath);
                    var snapshot = await item.InspectAsync(deadline.Token);
                    Assert.IsTrue(snapshot.IsPlaceholder); Assert.AreEqual(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                    Assert.IsNull(snapshot.RemoteRevision); Assert.IsNotNull(snapshot.LocalBinding);
                    Assert.AreEqual(member.LocalObject.LocalFileId, snapshot.LocalBinding.LocalFileId);
                    Assert.AreEqual(caller.User!.Value, member.OwnerSid);
                    var plan = await catalog.ReadNamespaceBirthPlanAsync(request.Operation, deadline.Token); Assert.IsNotNull(plan);
                    Assert.AreEqual(plan.ItemId, snapshot.ItemId); Assert.AreEqual(plan.RemoteId, snapshot.RemoteId);
                    var inspector = new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, role, actor);
                    Assert.AreEqual(member.MemberId, (await inspector.InspectAsync(member.RelativePath, member.IsDirectory, deadline.Token)).MemberId);
                    if (!member.IsDirectory)
                    {
                        Assert.AreEqual(0L, new FileInfo(item.FullPath).Length);
                        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(item.FullPath), deadline.Token));
                    }
                    else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => Directory.Delete(item.FullPath, true), deadline.Token));
                    Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(request.Parent, request.Name, deadline.Token));
                }
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ControlledCreation: productionCoordinator=True; emptyFile=True; emptyDirectory=True; nestedBornParent=True; twoOwners=True; stableMemberReplay=True; firstBinding=True; normalCreationDenied=True; recursiveDeletionDenied=True; currentUserOwner=True; actualWorkerUpload=False; hostIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
