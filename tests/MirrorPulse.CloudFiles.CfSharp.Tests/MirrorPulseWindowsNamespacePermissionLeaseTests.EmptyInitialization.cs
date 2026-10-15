using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRuntimeProtectsAnEmptyAdapterInventoryAndRecoversItsActualAnchorAcrossOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, []);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        MirrorPulseNamespaceMemberRegistration? original = null; string? firstRole = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: provider, router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                Guid operation = Guid.NewGuid();
                var completion = owner == 0
                    ? await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token)
                    : await runtime.RecoverNamespaceCurrentTreeAsync(operation, timeout.Token);
                var intent = (await runtime.ReadNamespaceInitializationStatusAsync(operation, timeout.Token)).Intent;
                Assert.AreEqual(3, intent.Version); Assert.IsEmpty(intent.Roots);
                Assert.AreEqual(1, completion.MemberCount); Assert.AreEqual(execution.RoleSid.Value, completion.RoleSid);
                var member = await catalog.ReadNamespaceMemberAsync(completion.AnchorMemberId, timeout.Token);
                Assert.IsNotNull(member); Assert.IsNull(member.RootId); Assert.IsNull(member.ParentMemberId);
                Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.CapturedOriginal, member.Origin);
                Assert.AreEqual(member.LocalObject.SyncRootFileId, member.LocalObject.LocalFileId);
                if (owner == 0) { original = member; firstRole = completion.RoleSid; }
                else { Assert.AreEqual(original, member); Assert.AreNotEqual(firstRole, completion.RoleSid); }
                var page = await provider.FetchChildrenAsync(paths.SyncRootPath, ReadOnlyMemory<byte>.Empty, null, timeout.Token);
                Assert.IsTrue(page.IsComplete); Assert.IsEmpty(page.Children); Assert.IsEmpty(worker.Calls);
                Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync(timeout.Token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                Assert.IsEmpty(Directory.GetFileSystemEntries(paths.SyncRootPath));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "uncontrolled.txt"), "denied", timeout.Token));
                await Assert.ThrowsAsync<IOException>(() => Task.Run(() =>
                    Directory.Move(paths.SyncRootPath, paths.SyncRootPath + ".moved"), timeout.Token));
                TestContext.WriteLine($"EmptyRuntimeInitialization: owner={owner}; version=3; actualAnchor=True; noAdapterRoots=True; " +
                    "freshNativeAudit=True; immutableAnchorBinding=True; currentRole=True; normalNamespaceDenied=True; " +
                    "ancestorGuardRetained=True; sourceCalls=0; defaultHostStartup=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootGuardFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
