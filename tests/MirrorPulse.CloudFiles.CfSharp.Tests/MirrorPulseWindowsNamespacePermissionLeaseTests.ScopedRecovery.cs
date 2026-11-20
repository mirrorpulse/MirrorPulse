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
    public async Task NativeScopedRecoveryKeepsABusyRootClosedWhileAnotherRootAcceptsControlledWork()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New();
        var roots = AdapterRootRegistrationMapper.MapAll(AdapterId.Parse("example.scoped-recovery"), instance,
            [new("first", "First", "First", false), new("second", "Second", "Second", false)], RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot).ToArray();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
        RootRegistration first = roots.Single(root => root.DirectoryName == "First"), second = roots.Single(root => root.DirectoryName == "Second");
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var catalogOpenCapture = new MirrorPulseCatalogOpenCapture((phase, trace, elapsed) =>
            TestContext.WriteLine($"ScopedRecoveryCatalogOpenProgress: trace={trace:D}; phase={phase}; elapsedMs={elapsed}."));
        string firstPath = Path.Combine(paths.SyncRootPath, "First", "note.txt"), secondPath = Path.Combine(paths.SyncRootPath, "Second", "note.txt");
        MirrorPulseNamespaceMemberRegistration[] originals = []; string initialRole = string.Empty;
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
                (await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token)).ThrowIfAnyFailed();
                foreach (var root in roots)
                    await setup.GetDirectory(root.DirectoryName).SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                await File.WriteAllTextAsync(firstPath, "first retained bytes", timeout.Token);
                await File.WriteAllTextAsync(secondPath, "second retained bytes", timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("First"), setup.GetDirectory("Second"),
                    setup.GetFile("First/note.txt"), setup.GetFile("Second/note.txt") }) SetProtectedFixtureUserOwner(item, user.User!);
            }
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission(roots.Select(root => root.RootId));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                var initialized = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), timeout.Token);
                initialRole = initialized.RoleSid;
                originals = await ReadActualMembersAsync(catalog, initialized.AnchorMemberId, timeout.Token);
                Assert.HasCount(5, originals);
            }
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                Assert.AreNotEqual(initialRole, execution.RoleSid.Value);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission(roots.Select(root => root.RootId));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                Guid firstOperation = Guid.NewGuid(), secondOperation = Guid.NewGuid();
                await using (var writer = new FileStream(firstPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.RecoverNamespaceRootAsync(firstOperation, first.RootId, timeout.Token));
                    Assert.IsFalse(work.IsAdmitted(first.RootId)); Assert.IsFalse(work.IsAdmitted(second.RootId));
                    Assert.IsNull((await runtime.ReadNamespaceInitializationStatusAsync(firstOperation, timeout.Token)).Completion);
                    await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(paths.SyncRootPath, paths.SyncRootPath + ".moved"), timeout.Token));
                    var recovered = await runtime.RecoverNamespaceRootAsync(secondOperation, second.RootId, timeout.Token);
                    Assert.AreEqual(second.RootId, recovered.ScopeRoot); Assert.AreEqual(3, recovered.MemberCount);
                    Assert.AreEqual(4, (await runtime.ReadNamespaceInitializationStatusAsync(secondOperation, timeout.Token)).Intent.Version);
                    Assert.IsTrue(work.IsAdmitted(second.RootId)); Assert.IsFalse(work.IsAdmitted(first.RootId));
                    CollectionAssert.AreEqual(new[] { first.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await runtime.RecoverNamespaceCurrentTreeAsync(secondOperation, timeout.Token));
                    var secondRoot = originals.Single(member => member.RelativePath == "Second");
                    var created = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), secondRoot.MemberId, "controlled.txt", false, timeout.Token);
                    Assert.AreEqual(second.RootId, created.RootId); Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Second", "controlled.txt")));
                    Assert.AreEqual("second retained bytes", await File.ReadAllTextAsync(secondPath, timeout.Token));
                    Assert.AreEqual(recovered, await runtime.RecoverNamespaceRootAsync(secondOperation, second.RootId, timeout.Token));
                    Assert.IsTrue(work.IsAdmitted(second.RootId)); Assert.IsFalse(work.IsAdmitted(first.RootId));
                }
                var firstRecovered = await runtime.RecoverNamespaceRootAsync(firstOperation, first.RootId, timeout.Token);
                Assert.AreEqual(first.RootId, firstRecovered.ScopeRoot);
                Assert.IsTrue(work.IsAdmitted(first.RootId)); Assert.IsTrue(work.IsAdmitted(second.RootId));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                foreach (var member in originals) Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, timeout.Token));
                Assert.AreEqual("first retained bytes", await File.ReadAllTextAsync(firstPath, timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(secondPath), timeout.Token));
                TestContext.WriteLine("ScopedRuntimeRecovery: sameInstanceTwoRoots=True; version4=True; newRole=True; realWriterBusy=True; " +
                    "failedRootFenceRetained=True; anchorGuardAfterFailure=True; otherRootFreshNativeAudit=True; actualControlledCreation=True; " +
                    "historicalReplayDoesNotReopenOtherRoot=True; pendingOperationResumed=True; firstBindingsAndBytesPreserved=True; " +
                    "noRemoteAcceptance=True; defaultHostStartup=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootGuardFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            foreach (string name in new[] { "First", "Second" })
            {
                string path = Path.Combine(paths.SyncRootPath, name);
                if (!Directory.Exists(path)) continue;
                var access = new System.Security.AccessControl.DirectorySecurity();
                access.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;" + user.User!.Value + ")", System.Security.AccessControl.AccessControlSections.Access);
                new DirectoryInfo(path).SetAccessControl(access);
            }
            DeleteFixture(directory);
        }
    }

    private static async Task<MirrorPulseNamespaceMemberRegistration[]> ReadActualMembersAsync(MirrorPulseProductCatalog catalog,
        Guid anchor, CancellationToken token)
    {
        var result = new List<MirrorPulseNamespaceMemberRegistration>();
        foreach (var item in await catalog.ReadNamespaceMemberTreeAsync(anchor, token))
            result.Add((await catalog.ReadNamespaceMemberAsync(item.MemberId, token))!);
        return result.ToArray();
    }
}
