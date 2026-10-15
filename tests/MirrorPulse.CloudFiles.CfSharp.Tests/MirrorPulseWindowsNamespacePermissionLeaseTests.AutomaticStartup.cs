using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
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
    public async Task NativeAutomaticStartupBootstrapsExistingAndBornRootsAndIsolatesBusyRecovery()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New(); var adapter = AdapterId.Parse("example.automatic-startup");
        var roots = AdapterRootRegistrationMapper.MapAll(adapter, instance,
            [new("existing", "Existing", "Existing", false), new("new", "New", "New", false)],
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot).Concat(
            AdapterRootRegistrationMapper.MapAll(adapter, InstanceId.New(), [new("offline", "Offline", "Offline", false)],
                RootRegistrationState.Disabled, identityScope: RootIdentityScope.InstanceRoot)).ToArray();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
        RootRegistration existingRoot = roots.Single(root => root.DirectoryName == "Existing");
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        string note = Path.Combine(paths.SyncRootPath, "Existing", "note.txt"); Guid firstAnchor = Guid.Empty;
        Dictionary<string, Guid> firstMembers = [];
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
                (await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(
                    new(paths.SyncRootPath, [existingRoot]), timeout.Token)).ThrowIfAnyFailed();
                await setup.GetDirectory("Existing").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                await File.WriteAllTextAsync(note, "original bytes", timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("Existing"), setup.GetFile("Existing/note.txt") })
                    SetProtectedFixtureUserOwner(item, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler(); await using var work = new MirrorPulseNamespaceWorkAdmission(roots.Select(root => root.RootId));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: provider, router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true);
                await using var runtime = factory.Create(paths);
                await using var writer = owner == 1 ? new FileStream(note, FileMode.Open, FileAccess.Write, FileShare.ReadWrite) : null;
                await runtime.StartAsync(timeout.Token);
                Assert.HasCount(3, runtime.NamespaceStartupResults);
                foreach (var root in roots.Where(root => owner == 0 || root.RootId != existingRoot.RootId))
                {
                    Assert.IsTrue(work.IsAdmitted(root.RootId), runtime.NamespaceStartupResults.Single(result => result.RootId == root.RootId).RecoveryCode);
                    var actual = await runtime.InspectNamespaceMemberAsync(root.DirectoryName, true, timeout.Token);
                    Assert.AreEqual(root.RootId, actual.RootId);
                }
                var anchor = await runtime.InspectNamespaceMemberAsync(string.Empty, true, timeout.Token);
                if (owner == 0)
                {
                    firstAnchor = anchor.MemberId;
                    firstMembers = (await catalog.ReadNamespaceMemberTreeAsync(firstAnchor, timeout.Token)).ToDictionary(entry => entry.RelativePath, entry => entry.MemberId);
                    Assert.HasCount(5, firstMembers);
                    Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
                        (await catalog.ReadNamespaceMemberAsync(firstMembers["Existing/note.txt"], timeout.Token))!.Origin);
                    foreach (string name in new[] { "New", "Offline" })
                        Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth,
                            (await catalog.ReadNamespaceMemberAsync(firstMembers[name], timeout.Token))!.Origin);
                    await File.WriteAllTextAsync(note, "latest user bytes", timeout.Token);
                }
                else
                {
                    Assert.AreEqual(firstAnchor, anchor.MemberId); Assert.IsFalse(work.IsAdmitted(existingRoot.RootId));
                    var blocked = runtime.NamespaceStartupResults.Single(result => result.RootId == existingRoot.RootId);
                    Assert.IsNotNull(blocked.OperationId); Assert.IsFalse(blocked.Admitted);
                    await writer!.DisposeAsync();
                    var restored = await runtime.RecoverNamespaceRootAsync(blocked.OperationId.Value, existingRoot.RootId, timeout.Token);
                    Assert.AreEqual(existingRoot.RootId, restored.ScopeRoot); Assert.IsTrue(work.IsAdmitted(existingRoot.RootId));
                    foreach (var entry in await catalog.ReadNamespaceMemberTreeAsync(firstAnchor, timeout.Token))
                        Assert.AreEqual(firstMembers[entry.RelativePath], entry.MemberId);
                }
                Assert.AreEqual("latest user bytes", await File.ReadAllTextAsync(note, timeout.Token));
                Assert.IsEmpty(worker.Calls);
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                TestContext.WriteLine($"AutomaticStartup: owner={owner}; existingOriginalCapture=True; newProtectedBirths=True; offlineSourceCalls=0; " +
                    "freshPerRootNativeAudit=True; busyRootIsolation=True; sameOperationRecovery=True; originalMembersAndBytesPreserved=True; remoteAcceptance=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath); RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }

    private static void RestoreAutomaticStartupFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        foreach (string name in new[] { "Existing", "New", "Offline" })
        {
            var folder = new DirectoryInfo(Path.Combine(syncRoot, name)); if (!folder.Exists) continue;
            if (folder.LinkTarget is not null) throw new InvalidOperationException("Automatic startup fixture cleanup cannot follow links.");
            var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;" + user.Value + ")", AccessControlSections.Access); folder.SetAccessControl(acl);
        }
        var note = new FileInfo(Path.Combine(syncRoot, "Existing", "note.txt"));
        if (!note.Exists) return;
        if (note.LinkTarget is not null) throw new InvalidOperationException("Automatic startup fixture cleanup cannot follow links.");
        var access = new FileSecurity(); access.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;" + user.Value + ")", AccessControlSections.Access); note.SetAccessControl(access);
    }
}
