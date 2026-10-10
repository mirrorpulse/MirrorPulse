using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeSyncRootGuardBlocksAncestorMovesWithoutBlockingControlledChildOperations()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, []);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await fileSystem.StartAsync(timeout.Token);
            SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await using var role = new MirrorPulseNamespaceExecutionSession();
            // Retain only the root and its ancestors throughout the operation. Holding every
            // descendant name would also block the Host's own controlled rename and delete.
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, timeout.Token);
            var observed = await guard.InspectAsync(timeout.Token);
            Assert.IsNull(observed.RootId);
            Assert.AreEqual(string.Empty, observed.RelativePath);
            Assert.AreEqual(caller.User!.Value, observed.OwnerSid);
            var original = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), null, observed.LocalObject,
                string.Empty, true, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
            var protection = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), original.EvidenceId, original.LocalObject,
                null, string.Empty, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value, original.OriginalDacl,
                MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: true), DateTimeOffset.UtcNow);
            await catalog.PrepareNamespacePermissionChangeAsync(original, protection, timeout.Token);
            await role.RunNamespaceOperationAsync(async () =>
            {
                var applied = await coordinator.ApplyProtectedAsync(protection.OperationId, fileSystem.Root, router,
                    cancellationToken: timeout.Token);
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, applied.Receipt!.Outcome);
                Assert.IsTrue(applied.Receipt.Drained);
            });
            foreach (string target in new[] { paths.SyncRootPath, directory })
            {
                await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(target, target + ".moved"), timeout.Token));
                Assert.IsTrue(Directory.Exists(target));
                Assert.IsFalse(Directory.Exists(target + ".moved"));
            }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "ordinary.txt"), "not admitted", timeout.Token));
            string child = Path.Combine(paths.SyncRootPath, "controlled");
            string renamed = Path.Combine(paths.SyncRootPath, "renamed");
            await role.RunNamespaceOperationAsync(async () =>
            {
                Directory.CreateDirectory(child);
                await File.WriteAllTextAsync(Path.Combine(child, "latest.txt"), "retained bytes", timeout.Token);
                Directory.Move(child, renamed);
                Assert.AreEqual("retained bytes", await File.ReadAllTextAsync(Path.Combine(renamed, "latest.txt"), timeout.Token));
                File.Delete(Path.Combine(renamed, "latest.txt"));
                Directory.Delete(renamed);
            });
            Assert.IsFalse(Directory.Exists(child));
            Assert.IsFalse(Directory.Exists(renamed));
            var current = await guard.InspectAsync(timeout.Token);
            Assert.AreEqual(original.LocalObject, current.LocalObject);
            Assert.AreEqual(original.OwnerSid, current.OwnerSid);
            var restoration = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), original.EvidenceId, original.LocalObject,
                null, string.Empty, MirrorPulseNamespacePermissionChangeKind.Restore, role.RoleSid.Value, current.Dacl,
                original.OriginalDacl, DateTimeOffset.UtcNow);
            await catalog.PrepareNamespacePermissionChangeAsync(original, restoration, timeout.Token);
            await role.RunNamespaceOperationAsync(async () =>
            {
                var restored = await coordinator.ApplyProtectedAsync(restoration.OperationId, fileSystem.Root, router,
                    cancellationToken: timeout.Token);
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, restored.Permission!.Outcome);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, restored.Receipt!.Outcome);
            });
            Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(original.OriginalDacl,
                (await guard.InspectAsync(timeout.Token)).Dacl));
            TestContext.WriteLine($"SyncRootGuard: rootAndAncestorsRetained=True; ancestorAclWrites=0; ordinaryCreationDenied=True; controlledChildCreateRenameDelete=True; originalRootBindingAndOwner=True; originalDaclRestored=True; elevatedCaller={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; hostIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(paths.SyncRootPath)) RestoreRootGuardFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreRootGuardFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            !File.Exists(Path.Combine(directory, ".mp-permission-fixture")) ||
            !string.Equals(Path.GetFullPath(syncRoot), Path.GetFullPath(Path.Combine(directory, "sync")), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The disposable root permission cleanup target is invalid.");
        var cleanup = new DirectorySecurity();
        cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        cleanup.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(syncRoot).SetAccessControl(cleanup);
    }
}
