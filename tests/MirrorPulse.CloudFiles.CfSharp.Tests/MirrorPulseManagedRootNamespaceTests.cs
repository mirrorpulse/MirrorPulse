using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseManagedRootNamespaceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task DisposableTreeDeleteAclPreservesOrdinaryEditsButBlocksFileDeletionAndReplacement()
    {
        // No Cloud Files root is registered. Only this marked temporary ordinary-file fixture gets an ACL.
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        string managed = Path.Combine(root, "sync", "Docs");
        string nested = Path.Combine(managed, "Nested");
        Directory.CreateDirectory(nested);
        var directory = new DirectoryInfo(managed);
        DirectorySecurity original = directory.GetAccessControl();
        string? previous = Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string child = Path.Combine(nested, "unsent.txt");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty);
            await File.WriteAllTextAsync(child, "unsent original");
            DirectorySecurity protectedAcl = directory.GetAccessControl();
            protectedAcl.AddAccessRule(new FileSystemAccessRule(identity.User!,
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(protectedAcl);
            await File.WriteAllTextAsync(child, "unsent latest edit");
            Environment.SetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST", "1");
            NamespaceMutationProbeResult result = await RunNamespaceProcessAsync(root, "delete-tree", timeout.Token);
            Assert.IsFalse(result.Completed);
            Assert.IsTrue(Directory.Exists(nested));
            Assert.AreEqual("unsent latest edit", await File.ReadAllTextAsync(child));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(child)));
            string replacement = Path.Combine(nested, "replacement.tmp");
            await File.WriteAllTextAsync(replacement, "replacement bytes");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(replacement, child, overwrite: true)));
            Assert.AreEqual("unsent latest edit", await File.ReadAllTextAsync(child));
            TestContext.WriteLine("TreeAclProbe: recursive ordinary-file deletion denied; latest in-place edit retained; individual child deletion and atomic-save replacement also denied. This is a compatibility boundary, not the selected product policy.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST", previous);
            directory.SetAccessControl(original);
            // Inherited deny ACEs can remain on already-created descendants.
            // Restore the known disposable parent explicitly before recursive cleanup.
            var cleanup = new DirectorySecurity();
            cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            cleanup.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(nested).SetAccessControl(cleanup);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("delete-empty")]
    [DataRow("delete-tree")]
    [DataRow("rename")]
    public async Task NamespaceConsumerRejectsUnmarkedAndOutOfScopeDirectories(string mode)
    {
        string[] roots = [Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "MirrorPulse-namespace-guard", Guid.NewGuid().ToString("N"))];
        string? previous = Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST");
        try
        {
            Environment.SetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST", "1");
            foreach (string root in roots)
            {
                string directory = Path.Combine(root, "sync", "Docs");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "keep");
                if (root == roots[1]) await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty);
                Assert.AreEqual(2, await NamespaceMutationProbe.RunAsync(root, mode));
                Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(directory, "keep.txt")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST", previous);
            foreach (string root in roots) if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HistoricalRenameWithoutPublicProofCannotAdoptCurrentObject(bool nativeObserved)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-proof-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.missing-proof"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await SaveNamespaceTopologyAsync(catalog, root, directory, CancellationToken.None);
            MirrorPulseRootRenameIntent intent = await catalog.PrepareManagedRootRenameAsync(root.RootId, "Renamed");
            CloudPlaceholderIdentity identity = MirrorPulsePlaceholderIdentity.Create(root.InstanceId, $"mirrorpulse-root:{root.RootId}").ToCfSharp();
            var original = new MirrorPulseRootRenameProof(identity.ItemId,
                new(1, Guid.NewGuid(), Guid.NewGuid()), Convert.ToBase64String(identity.Encode()), DateTimeOffset.UtcNow);
            await catalog.SaveManagedRootRenameProofAsync(intent.OperationId, original);
            if (nativeObserved)
                intent = await catalog.ObserveManagedRootRenameAsync(intent.OperationId,
                    new(original.LocalObject, original.PlaceholderIdentity, intent.TargetName, DateTimeOffset.UtcNow));
            // No registration, StartAsync, item inspection, or original library record exists.
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                .WithStateStore(new MirrorPulseCfSharpStateSession(paths)).Build();
            var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog,
                new(paths.SyncRootPath, [root]));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => coordinator.RecoverAsync(intent.OperationId).AsTask());
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => coordinator.PrepareAsync(root.RootId, "Renamed").AsTask());
            MirrorPulseRootRenameHistory retained = (await catalog.ReadManagedRootRenameHistoryAsync()).Single();
            Assert.AreEqual(original, retained.Proof);
            Assert.AreEqual(intent.Phase, retained.Intent.Phase);
            Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task SaveNamespaceTopologyAsync(MirrorPulseProductCatalog catalog, RootRegistration root,
        string directory, CancellationToken token)
    {
        var manifest = new AdapterManifest(1, root.AdapterId, "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
            [new(root.UniquenessKey, root.Label, root.DirectoryName, false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var configured = new AdapterInstance(root.AdapterId, installation.InstallId, root.InstanceId, "Example",
            new Dictionary<string, string> { ["sourceDirectory"] = Path.Combine(directory, "source") }, [],
            Path.Combine(directory, "cache", "files"), Path.Combine(directory, "cache", "transfers"),
            root.State == RootRegistrationState.Active, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        await catalog.SaveAdapterTopologyAsync(new([installation], [configured], [root]), token);
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRootDeleteProtectionRetainsEntryAfterChildFirstRecursiveDelete()
        => await ProbeRootDeletionAsync(protectChildren: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeChildDeleteDenialExposesUnconvertedFileProtectionBoundary()
        => await ProbeRootDeletionAsync(protectChildren: true);

    private async Task ProbeRootDeletionAsync(bool protectChildren)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.delete-probe"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            var state = new MirrorPulseCfSharpStateSession(paths);
            var provider = new NamespaceProbeProvider(router, protectChildren);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(provider).Build();
            await fileSystem.StartAsync(timeout.Token);
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(timeout.Token);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
            string managed = Path.Combine(paths.SyncRootPath, "Docs");
            CloudItemSnapshot before = await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token);
            Assert.IsTrue(before.Exists && before.IsPlaceholder);
            NamespaceMutationProbeResult emptyFailure = await RunNamespaceProcessAsync(root, "delete-empty", timeout.Token);
            Assert.IsFalse(emptyFailure.Completed, "The external process deleted the protected managed entry.");
            Assert.IsGreaterThan(0, provider.DeleteApprovals, "The provider did not receive the external delete request.");
            Assert.IsTrue(Directory.Exists(managed));
            string child = Path.Combine(managed, "unsent.txt");
            await File.WriteAllTextAsync(child, "unsent resident data", timeout.Token);
            CloudItemSnapshot childBefore = await fileSystem.GetFile(Path.Combine("Docs", "unsent.txt")).InspectAsync(timeout.Token);
            Assert.IsTrue(childBefore.Exists);
            Assert.IsFalse(childBefore.IsPlaceholder, "This probe must cover an ordinary file that has not been converted after upload.");
            NamespaceMutationProbeResult recursiveFailure = await RunNamespaceProcessAsync(root, "delete-tree", timeout.Token);
            Assert.IsFalse(recursiveFailure.Completed, "The external process recursively deleted the protected managed entry.");
            CloudItemSnapshot after = await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token);
            Assert.IsTrue(after.Exists && after.IsPlaceholder);
            CollectionAssert.AreEqual(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
            if (protectChildren)
            {
                Assert.IsFalse(File.Exists(child), "The reproduced ordinary-file callback boundary changed; reassess the product protection mechanism.");
                Assert.AreEqual(0, provider.ChildDeleteApprovals, "The ordinary-file delete unexpectedly reached provider approval; reassess this diagnostic.");
                TestContext.WriteLine("ChildDeleteBoundary: rejecting every provider delete request still does not intercept an unconverted ordinary child's deletion. This diagnostic does not satisfy whole-tree product protection.");
            }
            TestContext.WriteLine($"RootDeleteProbe: emptyRootProtected=True; recursiveRootProtected=True; residentChildRetained={File.Exists(child)}; sourceOperations=0; deleteApprovals={provider.DeleteApprovals}; emptyHResult=0x{emptyFailure.HResult:X8}; recursiveHResult=0x{recursiveFailure.HResult:X8}. A recursive caller can remove children before requesting approval for the protected entry directory.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<NamespaceMutationProbeResult> RunNamespaceProcessAsync(string root, string mode, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, "--namespace-operation", root, mode })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new AssertFailedException("The namespace consumer did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            Assert.AreEqual(0, process.ExitCode, await stderr);
            return JsonSerializer.Deserialize<NamespaceMutationProbeResult>(await stdout)
                ?? throw new AssertFailedException("The namespace consumer did not return its native result.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeExternalRootRenameRecoversOriginalPublicProofAcrossRestart()
    {
        // Keep one required gate result while exercising both availability states.
        await ProbeExternalRootRenameAsync(RootRegistrationState.Active);
        await ProbeExternalRootRenameAsync(RootRegistrationState.Disabled);
    }

    private async Task ProbeExternalRootRenameAsync(RootRegistrationState availability)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rename-probe"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), availability,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            CloudPlaceholderIdentity child = router.CreateFileIdentity(instance, "docs", "child", "v1");
            Guid operation = Guid.Empty;
            MirrorPulseRootRenameProof? originalProof = null;
            for (int run = 0; run < 2; run++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (run == 0)
                    await SaveNamespaceTopologyAsync(catalog, registration, root, timeout.Token);
                var state = new MirrorPulseCfSharpStateSession(paths);
                var provider = new NamespaceProbeProvider();
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(provider).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router);
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("child.txt", child, 4).WithInSyncState(true)
                            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                    MirrorPulseRootRenameHistory prepared = await coordinator.PrepareAsync(registration.RootId, "Renamed", timeout.Token);
                    operation = prepared.Intent.OperationId;
                    originalProof = prepared.Proof;
                    Assert.IsNotNull(originalProof?.DirectoryMoveEvidence);
                    Assert.AreEqual(originalProof, (await coordinator.PrepareAsync(registration.RootId, "Renamed", timeout.Token)).Proof);
                    MirrorPulseManagedRootRenameRecovery beforeMove = await coordinator.RecoverAsync(operation, timeout.Token);
                    Assert.AreEqual(CloudDirectoryMoveReconciliationOutcome.NotMoved, beforeMove.LibraryResult!.Outcome);
                    Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, beforeMove.Intent.Phase);
                    Assert.IsTrue((await RunNamespaceProcessAsync(root, "rename", timeout.Token)).Completed);
                    Assert.IsGreaterThan(0, provider.RenameApprovals, "The provider did not receive the external rename request.");
                    Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single().Label);
                    continue; // Close both owners before recovery, including the original library preparation.
                }
                MirrorPulseManagedRootRenameRecovery recovery = await coordinator.RecoverAsync(operation, timeout.Token);
                Assert.IsNotNull(recovery.LibraryResult);
                Assert.IsTrue(recovery.LibraryResult.NativeMoveObserved);
                Assert.IsTrue(recovery.LibraryResult.DurableProjectionCommitted);
                Assert.IsFalse(recovery.LibraryResult.RequiresFullRescan);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, recovery.Intent.Phase,
                    $"Public recovery outcome={recovery.LibraryResult.Outcome}; stage={recovery.LibraryResult.Stage}; nativeHResult={recovery.LibraryResult.NativeHResult:X8}.");
                RootRegistration renamed = (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single();
                Assert.AreEqual(registration.RootId, renamed.RootId);
                Assert.AreEqual(registration.InstanceId, renamed.InstanceId);
                Assert.AreEqual(registration.UniquenessKey, renamed.UniquenessKey);
                Assert.AreEqual(availability, renamed.State);
                Assert.AreEqual("Renamed", renamed.Label);
                Assert.AreEqual("Renamed", renamed.DirectoryName);
                MirrorPulseRootRenameHistory completed = (await catalog.ReadManagedRootRenameHistoryAsync(timeout.Token)).Single();
                Assert.AreEqual(originalProof, completed.Proof);
                Assert.AreEqual(originalProof!.LocalObject, completed.Observation!.LocalObject);
                Assert.AreEqual(originalProof.PlaceholderIdentity, completed.Observation.PlaceholderIdentity);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, (await coordinator.RecoverAsync(operation, timeout.Token)).Intent.Phase);
                await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                CloudItemState? movedRoot = await transaction.Items.GetByRelativePathAsync("Renamed", timeout.Token);
                CloudItemState? movedChild = await transaction.Items.GetByRelativePathAsync(Path.Combine("Renamed", "child.txt"), timeout.Token);
                Assert.AreEqual(originalProof.ItemId, movedRoot!.ItemId);
                Assert.AreEqual(child.ItemId, movedChild!.ItemId);
                Assert.IsNull(await transaction.Items.GetByRelativePathAsync(Path.Combine("Docs", "child.txt"), timeout.Token));
                await transaction.RollbackAsync(timeout.Token);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Renamed", "child.txt")));
                Assert.AreEqual("docs", router.ResolveCurrentPath(Path.Combine("Renamed", "child.txt")).RootKey);
                TestContext.WriteLine($"PublicRootRenameRecovery: availability={availability}; originalProofRetained=True; stableRoot=True; stableChild=True; runtimeRestart=True; sourceCalls=0.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePendingRootRenameFencesJournalAndRescanAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        AdapterId adapter = AdapterId.Parse("example.root-rename");
        RootRegistration registration = AdapterRootRegistrationMapper.Map(adapter, instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var remote = new RejectingTransport();
        Guid operation = Guid.Empty;
        Guid rename = Guid.Empty;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int run = 0; run < 2; run++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (run == 0)
                {
                    var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
                        new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                        new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                        [new("docs", "Docs", "Docs", false)]);
                    var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
                        new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                    var configured = new AdapterInstance(adapter, installation.InstallId, instance, "Example",
                        new Dictionary<string, string> { ["sourceDirectory"] = Path.Combine(root, "source") }, [],
                        Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"), true,
                        AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
                    await catalog.SaveAdapterTopologyAsync(new([installation], [configured], [registration]), timeout.Token);
                }
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "report.txt"), "retained edit", timeout.Token);
                    MirrorPulseWorkerChangeCommand? command = null;
                    while (command is null)
                    {
                        command = (await source.ReadPendingAsync(timeout.Token)).ReadyCommands.FirstOrDefault(command => command.RelativePath == "report.txt");
                        if (command is null) await Task.Delay(20, timeout.Token);
                    }
                    operation = command.OperationId;
                    rename = (await catalog.PrepareManagedRootRenameAsync(registration.RootId, "My Files", timeout.Token)).OperationId;
                }
                Assert.IsEmpty((await source.ReadPendingAsync(timeout.Token)).ReadyCommands);
                Assert.AreEqual(rename, (await catalog.ReadManagedRootRenamesAsync(timeout.Token)).Single().OperationId);
                var rescan = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog, remote, remote, _ => true);
                Assert.AreEqual(0, await rescan.ReconcileAsync(timeout.Token));
                CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadDeferredRescanRootsAsync(timeout.Token)).ToArray());
                await using (ICloudStateTransaction retained = await state.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    Assert.IsNotNull(await retained.Operations.GetAsync(operation, timeout.Token));
                    await retained.RollbackAsync(timeout.Token);
                }
                Assert.AreEqual("retained edit", await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "report.txt"), timeout.Token));
                Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single().Label);
                if (run == 1)
                {
                    await catalog.TransitionManagedRootRenameAsync(rename, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled, timeout.Token);
                    Assert.IsTrue((await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.OperationId == operation));
                }
                Assert.AreEqual(0, remote.Calls);
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RejectingTransport : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport
    {
        public int Calls { get; private set; }
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("A root with a pending namespace transition must not upload.");
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("A root with a pending namespace transition must not contact its source.");
        }
    }

    // This permissive provider belongs only to the disposable library boundary
    // probe. Product root rename remains guarded by its durable namespace policy.
    private sealed class NamespaceProbeProvider(MirrorPulseRootRouter? rootRouter = null, bool protectChildren = false) : ICloudDemandProvider
    {
        private int _deleteApprovals;
        private int _renameApprovals;
        private int _childDeleteApprovals;
        public int ChildDeleteApprovals => Volatile.Read(ref _childDeleteApprovals);
        public int DeleteApprovals => Volatile.Read(ref _deleteApprovals);
        public int RenameApprovals => Volatile.Read(ref _renameApprovals);
        public ValueTask<CloudProviderPolicyDecision> ApproveDeleteAsync(CloudProviderDeleteRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deleteApprovals);
            if (rootRouter is not null && MirrorPulseRootNamespacePolicy.ApproveDelete(rootRouter, request.NormalizedPath) == CloudProviderPolicyDecision.Allow)
                Interlocked.Increment(ref _childDeleteApprovals);
            if (protectChildren) return ValueTask.FromResult(CloudProviderPolicyDecision.Deny);
            return ValueTask.FromResult(rootRouter is null
                ? CloudProviderPolicyDecision.Deny : MirrorPulseRootNamespacePolicy.ApproveDelete(rootRouter, request.NormalizedPath));
        }
        public ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _renameApprovals);
            return ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
        }
        public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(CloudProviderFetchPlaceholdersRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CloudProviderDirectoryPage([]));
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new AssertFailedException("The namespace probe must not hydrate content."));
    }
}
