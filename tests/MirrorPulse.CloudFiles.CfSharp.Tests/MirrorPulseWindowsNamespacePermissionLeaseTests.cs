using System.Runtime.Versioning;
using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeOwnedPermissionLeaseReconcilesUnrecordedWriteRotationAndRestorationAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.permission-lease"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        string filePath = Path.Combine(paths.SyncRootPath, "Docs", "unsent.txt");
        MirrorPulseNamespacePermissionBaseline? original = null;
        MirrorPulseNamespacePermissionBaseline? originalDirectoryBaseline = null;
        MirrorPulseNamespacePermissionIntent? first = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var role = new MirrorPulseNamespaceExecutionSession();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                    await File.WriteAllTextAsync(filePath, "latest unsent bytes", timeout.Token);
                }
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
                var foreignRouter = new MirrorPulseRootRouter(Path.Combine(directory, "foreign"), [registration]);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseWindowsNamespacePermissionLease.OpenAsync(
                    fileSystem.GetFile("Docs/unsent.txt"), foreignRouter, timeout.Token));
                await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.GetFile("Docs/unsent.txt"), router, timeout.Token);
                MirrorPulseNamespacePermissionObject observed = await lease.InspectAsync(timeout.Token);
                Assert.AreEqual(registration.RootId, observed.RootId);
                Assert.AreEqual("Docs/unsent.txt", observed.RelativePath);
                Assert.IsFalse(observed.IsDirectory);
                await Assert.ThrowsAsync<IOException>(() => Task.Run(() => File.Move(filePath, filePath + ".moved"), timeout.Token));
                if (owner == 0)
                {
                    original = new(Guid.NewGuid(), observed.RootId, observed.LocalObject, observed.RelativePath,
                        observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                    first = new(Guid.NewGuid(), original.EvidenceId, observed.LocalObject, observed.RootId, observed.RelativePath,
                        MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value, observed.Dacl,
                        MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: false), DateTimeOffset.UtcNow);
                    await catalog.PrepareNamespacePermissionChangeAsync(original, first, timeout.Token);
                    await lease.ApplyDaclAsync(first.TargetDacl, timeout.Token);
                    Assert.AreEqual(first.TargetDacl, (await lease.InspectAsync(timeout.Token)).Dacl);
                    Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(first.OperationId, timeout.Token))!.Phase);
                    // End the first store owner with a real applied ACL but no application receipt.
                }
                else
                {
                    Assert.AreEqual(original!.LocalObject, observed.LocalObject);
                    Assert.AreEqual(first!.TargetDacl, observed.Dacl);
                    var counted = new CountingLease(lease);
                    var recovered = await coordinator.ApplyAsync(first.OperationId, counted, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, recovered.Outcome);
                    Assert.AreEqual(0, counted.Writes);
                    Assert.AreEqual(2, counted.Reads);
                    var rotation = first with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                        RoleSid = role.RoleSid.Value,
                        ExpectedDacl = first.TargetDacl,
                        TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: false),
                        PreparedAt = DateTimeOffset.UtcNow,
                    };
                    await catalog.PrepareNamespacePermissionChangeAsync(original, rotation, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(rotation.OperationId, counted, timeout.Token)).Outcome);
                    await using var directoryLease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.GetDirectory("Docs"), router, timeout.Token);
                    MirrorPulseNamespacePermissionObject originalDirectory = await directoryLease.InspectAsync(timeout.Token);
                    var directoryBaseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), originalDirectory.RootId, originalDirectory.LocalObject,
                        originalDirectory.RelativePath, true, originalDirectory.OwnerSid, originalDirectory.Dacl, originalDirectory.ObservedAt);
                    originalDirectoryBaseline = directoryBaseline;
                    var directoryProtection = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), directoryBaseline.EvidenceId,
                        directoryBaseline.LocalObject, directoryBaseline.RootId, directoryBaseline.RelativePath,
                        MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value, directoryBaseline.OriginalDacl,
                        MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: true), DateTimeOffset.UtcNow);
                    await catalog.PrepareNamespacePermissionChangeAsync(directoryBaseline, directoryProtection, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(directoryProtection.OperationId, directoryLease, timeout.Token)).Outcome);
                    await role.RunNamespaceOperationAsync(async () => await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("born.bin", router.CreateFileIdentity(registration.InstanceId, "docs", "born", "v1"), 16)
                            .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token));
                    string born = Path.Combine(paths.SyncRootPath, "Docs", "born.bin");
                    Assert.IsTrue((await fileSystem.GetFile("Docs/born.bin").InspectAsync(timeout.Token)).IsPlaceholder);
                    FileSystemAccessRule[] inheritedUser = new FileInfo(born).GetAccessControl(AccessControlSections.Access)
                        .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                        .Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(role.OwnerSid)).ToArray();
                    Assert.IsTrue(inheritedUser.Any(rule => rule.IsInherited && rule.AccessControlType == AccessControlType.Allow));
                    Assert.IsFalse(inheritedUser.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                        (rule.FileSystemRights & FileSystemRights.Delete) != 0));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(born), timeout.Token));
                    var directoryRestore = directoryProtection with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
                        ExpectedDacl = directoryProtection.TargetDacl,
                        TargetDacl = directoryBaseline.OriginalDacl,
                        PreparedAt = DateTimeOffset.UtcNow,
                    };
                    await catalog.PrepareNamespacePermissionChangeAsync(directoryBaseline, directoryRestore, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(directoryRestore.OperationId, directoryLease, timeout.Token)).Outcome);
                    var restore = rotation with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
                        ExpectedDacl = rotation.TargetDacl,
                        TargetDacl = original.OriginalDacl,
                        PreparedAt = DateTimeOffset.UtcNow,
                    };
                    await catalog.PrepareNamespacePermissionChangeAsync(original, restore, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(restore.OperationId, counted, timeout.Token)).Outcome);
                    Assert.AreEqual(2, counted.Writes);
                    Assert.AreEqual(original.OriginalDacl, (await lease.InspectAsync(timeout.Token)).Dacl);
                    Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId, timeout.Token));
                    Assert.HasCount(5, await catalog.ReadNamespacePermissionChangesAsync(timeout.Token));
                }
                Assert.AreEqual("latest unsent bytes", await File.ReadAllTextAsync(filePath, timeout.Token));
            }
            TestContext.WriteLine($"OwnedPermissionLease: architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; twoStoreOwners=True; publicBinding=True; retainedHandle=True; unrecordedWriteRecovered=True; repeatedRecoveryWrites=0; rotationAndExactRestore=True; fileAndDirectory=True; cfapiCreationInheritsProtection=True; latestBytesRetained=True; disabledRoot=True; sourceAccess=False; productIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (originalDirectoryBaseline is not null && Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                var cleanupDirectory = new DirectorySecurity();
                cleanupDirectory.SetSecurityDescriptorSddlForm(originalDirectoryBaseline.OriginalDacl, AccessControlSections.Access);
                new DirectoryInfo(Path.GetDirectoryName(filePath)!).SetAccessControl(cleanupDirectory);
            }
            if (original is not null && File.Exists(filePath))
            {
                var cleanup = new System.Security.AccessControl.FileSecurity();
                cleanup.SetSecurityDescriptorSddlForm(original.OriginalDacl, System.Security.AccessControl.AccessControlSections.Access);
                new FileInfo(filePath).SetAccessControl(cleanup);
            }
            DeleteFixture(directory);
        }
    }

    private static void DeleteFixture(string directory)
    {
        string fixturePrefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new InvalidOperationException("The namespace fixture cleanup target is invalid.");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class CountingLease(IMirrorPulseNamespacePermissionLease inner) : IMirrorPulseNamespacePermissionLease
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken token) { Reads++; return inner.InspectAsync(token); }
        public ValueTask ApplyDaclAsync(string dacl, CancellationToken token) { Writes++; return inner.ApplyDaclAsync(dacl, token); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The test retains ownership of the actual lease.
    }
}
