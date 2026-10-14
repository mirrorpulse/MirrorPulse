using System.Security.AccessControl;
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
    public async Task NativeRuntimeInitializationFencesOriginalsAuditsMembershipAndRecoversAcrossOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.initialization"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string note = Path.Combine(paths.SyncRootPath, "Docs", "note.txt");
        const string latest = "latest unaccepted edit between Host owners";
        MirrorPulseNamespaceMemberRegistration[]? firstMembers = null;
        MirrorPulseNamespaceInitializationCompletion? firstCompletion = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token);
                await setup.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                await File.WriteAllTextAsync(note, "original resident content", timeout.Token);
                await setup.GetDirectory("Docs").CreatePlaceholdersAsync([
                    CloudFilePlaceholderSpec.CreateBuilder("cold.bin", router.CreateFileIdentity(registration.InstanceId, "docs", "cold", "v1"), 8)
                        .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("Docs"), setup.GetFile("Docs/note.txt"), setup.GetFile("Docs/cold.bin") })
                    SetProtectedFixtureUserOwner(item, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, mayDispatch: _ => true);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                Guid operation = Guid.NewGuid();
                if (owner == 1)
                {
                    await using (var writer = new FileStream(note, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token));
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    Assert.IsNull((await runtime.ReadNamespaceInitializationStatusAsync(operation, timeout.Token)).Completion);
                }
                var completion = await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token);
                var status = await runtime.ReadNamespaceInitializationStatusAsync(operation, timeout.Token);
                Assert.AreEqual(completion, status.Completion); Assert.AreEqual(4, completion.MemberCount);
                Assert.AreEqual(execution.RoleSid.Value, completion.RoleSid);
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                var tree = await catalog.ReadNamespaceMemberTreeAsync(completion.AnchorMemberId, timeout.Token);
                Assert.HasCount(4, tree);
                var members = new List<MirrorPulseNamespaceMemberRegistration>();
                foreach (var entry in tree) members.Add((await catalog.ReadNamespaceMemberAsync(entry.MemberId, timeout.Token))!);
                if (owner == 0)
                {
                    firstMembers = members.ToArray(); firstCompletion = completion;
                    await File.WriteAllTextAsync(note, latest, timeout.Token);
                }
                else
                {
                    CollectionAssert.AreEqual(firstMembers!, members.ToArray());
                    Assert.AreNotEqual(firstCompletion!.RoleSid, completion.RoleSid);
                    Assert.AreEqual(firstCompletion, await catalog.ReadNamespaceInitializationCompletionAsync(firstCompletion.OperationId, timeout.Token));
                }
                Assert.AreEqual(latest, await File.ReadAllTextAsync(note, timeout.Token));
                Assert.AreEqual(completion, await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token));
                Assert.AreEqual(latest, await File.ReadAllTextAsync(note, timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "ordinary.txt"), "denied", timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(note), timeout.Token));
                await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(paths.SyncRootPath, paths.SyncRootPath + ".moved"), timeout.Token));
                Assert.IsNull(await catalog.ReadMutationAsync(operation, timeout.Token));
                TestContext.WriteLine($"RuntimeInitialization: owner={owner}; productionRuntime=True; durableAdmission=True; busyRetry={owner == 1}; parentFirstMembership=True; finalInventoryAudit=True; immutableMembers=True; sameObjectLatestEdit=True; currentRole=True; coldHydration=False; anchorGuardRetained=True; historicalReplay=True; noRemoteAcceptance=True; defaultHostStartup=False; hostCliIntegrated=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreInitializationFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreInitializationFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        if (!Directory.Exists(syncRoot)) return;
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        foreach (string relative in new[] { "Docs/note.txt", "Docs/cold.bin", "Docs" })
        {
            string path = Path.Combine(syncRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            bool folder = relative == "Docs";
            FileSystemInfo info = folder ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) continue;
            if (info.LinkTarget is not null) throw new InvalidOperationException("Fixture cleanup cannot follow a link.");
            FileSystemSecurity access = folder ? new DirectorySecurity() : new FileSecurity();
            access.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;" + user.Value + ")", AccessControlSections.Access);
            if (folder) ((DirectoryInfo)info).SetAccessControl((DirectorySecurity)access);
            else ((FileInfo)info).SetAccessControl((FileSecurity)access);
        }
    }
}
