using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeProtectedDirectoryPopulationAndColdHydrationKeepSourceOutsideNamespaceRole()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var instance = InstanceId.New();
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.source-context"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using var role = new MirrorPulseNamespaceExecutionSession();
            var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(role)
            {
                DirectoryPage = new([new("cold", "v1", "file", "cold.bin", 4, null, null, false)], default, true),
            };
            var source = new MirrorPulseNormalUserWorkerTransport(role, worker, worker, worker, worker, worker);
            var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(provider).Build();
            await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(timeout.Token));
            await using var feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(timeout.Token);
            await role.RunNamespaceOperationAsync(async () =>
                await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
            CloudDirectory docs = fileSystem.GetDirectory("Docs");
            SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!);
            SetProtectedFixtureUserOwner(docs, caller.User!);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, timeout.Token);
            var preparations = new List<MirrorPulseNamespacePermissionPreparation>();
            foreach (CloudDirectory item in new[] { fileSystem.Root, docs })
            {
                await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, timeout.Token);
                var original = await lease.InspectAsync(timeout.Token);
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), original.RootId, original.LocalObject,
                    original.RelativePath, true, original.OwnerSid, original.Dacl, original.ObservedAt);
                var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject,
                    baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect,
                    role.RoleSid.Value, baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, true),
                    DateTimeOffset.UtcNow);
                preparations.Add(new(baseline, intent));
            }
            await catalog.PrepareNamespacePermissionChangesAsync(preparations, timeout.Token);
            await role.RunNamespaceOperationAsync(async () =>
            {
                // Retain both originals before changing any parent inheritance.
                for (int index = preparations.Count - 1; index >= 0; index--)
                {
                    var preparation = preparations[index];
                    var item = index == 0 ? fileSystem.Root : docs;
                    var result = await coordinator.ApplyProtectedAsync(preparation.Intent.OperationId, item, router,
                        cancellationToken: timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Permission!.Outcome);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, result.Receipt!.Outcome);
                    Assert.IsTrue(result.Receipt.Drained);
                }
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                File.WriteAllTextAsync(Path.Combine(docs.FullPath, "ordinary.txt"), "not admitted", timeout.Token));
            Assert.AreEqual(0, worker.Calls.Count(call => call == "directory"));
            // Windows drives a real FETCH_PLACEHOLDERS callback in this ordinary-user enumeration.
            string[] local = await Task.Run(() => Directory.GetFiles(docs.FullPath), timeout.Token);
            Assert.IsTrue(local.Contains(Path.Combine(docs.FullPath, "cold.bin"), StringComparer.OrdinalIgnoreCase));
            Assert.IsGreaterThan(0, worker.Calls.Count(call => call == "directory"));
            Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
            var cold = await fileSystem.GetFile("Docs/cold.bin").InspectAsync(timeout.Token);
            Assert.IsTrue(cold.IsPlaceholder);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Task.Run(() => File.Delete(Path.Combine(docs.FullPath, "cold.bin")), timeout.Token));
            // Opening a cold file now drives a real FETCH_DATA callback, including its lazy body.
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(Path.Combine(docs.FullPath, "cold.bin"), timeout.Token));
            Assert.IsGreaterThan(0, worker.Calls.Count(call => call == "range-open"));
            Assert.IsGreaterThan(0, worker.Calls.Count(call => call == "range-read"));
            Assert.AreEqual(worker.Calls.Count(call => call == "range-open"), worker.Calls.Count(call => call == "range-dispose"));
            foreach (var preparation in preparations)
            {
                CloudDirectory item = preparation.Baseline.RootId is null ? fileSystem.Root : docs;
                await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, timeout.Token);
                var observed = await lease.InspectAsync(timeout.Token);
                Assert.AreEqual(preparation.Baseline.LocalObject, observed.LocalObject);
                Assert.AreEqual(preparation.Baseline.OwnerSid, observed.OwnerSid);
                Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(preparation.Intent.TargetDacl, observed.Dacl));
            }
            TestContext.WriteLine($"NativeSourceContext: ordinaryEnumeration=True; inheritedDeleteProtection=True; coldHydration=True; lazyRangeDisposedAsNormalUser=True; closedParentDacls=True; rootGuardRetained=True; elevatedCaller={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; hostStrictIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreSourceContextFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        if (!Directory.Exists(syncRoot)) return;
        // The existing guard verifies the exact GUID fixture, marker and sync-root location.
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        string docs = Path.Combine(syncRoot, "Docs");
        if (!Directory.Exists(docs)) return;
        // Cloud Files reparse tags have no directory-link target. Never follow a link.
        if (new DirectoryInfo(docs).LinkTarget is not null)
            throw new InvalidOperationException("Fixture cleanup cannot follow a directory link.");
        var cleanup = new DirectorySecurity();
        cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        cleanup.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(docs).SetAccessControl(cleanup);
    }
}
