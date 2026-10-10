using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
    public async Task NativeControlledBirthRetainsIdentityBytesAndOfficialJournalAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.controlled-birth"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var births = new List<(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan, MirrorPulseNamespaceBirthStart Start)>();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var observations = new List<MirrorPulseNamespaceBirthObservation>();
        var originalBindings = new List<CloudLocalFileBinding>();
        Guid[] journalIds = [];
        byte[] payload = Encoding.UTF8.GetBytes("controlled local bytes with no remote acceptance");
        string filePath = Path.Combine(paths.SyncRootPath, "Docs", "born.bin");
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(timeout.Token));
                await using var feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                CloudDirectory docs = fileSystem.GetDirectory("Docs");
                if (owner == 0)
                {
                    await role.RunNamespaceOperationAsync(async () => await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
                    await role.RunNamespaceOperationAsync(async () => await docs.SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token));
                    SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!);
                    SetProtectedFixtureUserOwner(docs, caller.User!);
                }
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, timeout.Token);
                if (owner == 0)
                {
                    foreach (CloudDirectory item in new[] { fileSystem.Root, docs })
                    {
                        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, timeout.Token);
                        var observed = await lease.InspectAsync(timeout.Token);
                        var original = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), observed.RootId, observed.LocalObject,
                            observed.RelativePath, true, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                        var protection = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), original.EvidenceId, original.LocalObject,
                            original.RootId, original.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value,
                            original.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, true), DateTimeOffset.UtcNow);
                        originals.Add(new(original, protection));
                    }
                    await catalog.PrepareNamespacePermissionChangesAsync(originals, timeout.Token);
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        foreach (var preparation in originals.AsEnumerable().Reverse())
                        {
                            var applied = await coordinator.ApplyProtectedAsync(preparation.Intent.OperationId,
                                preparation.Baseline.RootId is null ? fileSystem.Root : docs, router, cancellationToken: timeout.Token);
                            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
                        }
                    });
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(
                        Path.Combine(docs.FullPath, "ordinary.txt"), "not admitted", timeout.Token));
                    foreach (bool isDirectory in new[] { false, true })
                    {
                        var parent = originals[1];
                        string name = isDirectory ? "empty" : "born.bin";
                        var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), registration.RootId,
                            parent.Baseline.EvidenceId, parent.Intent.OperationId, parent.Baseline.LocalObject,
                            "Docs/" + name, isDirectory, MirrorPulseNamespaceBirthOrigin.ControlledCreation, role.RoleSid.Value, DateTimeOffset.UtcNow);
                        CloudPlaceholderIdentity identity = router.CreateFileIdentity(registration.InstanceId, "docs", "local:" + birth.OperationId.ToString("N"));
                        var plan = new MirrorPulseNamespaceBirthPlan(1, birth.OperationId, identity.ItemId, identity.RemoteId, null, DateTimeOffset.UtcNow);
                        await catalog.PrepareNamespaceBirthAsync(birth, timeout.Token); await catalog.PrepareNamespaceBirthPlanAsync(plan, timeout.Token);
                        Assert.IsTrue(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, birth.OperationId, timeout.Token));
                        var start = new MirrorPulseNamespaceBirthStart(1, birth.OperationId, DateTimeOffset.UtcNow);
                        Assert.IsTrue((await catalog.RecordNamespaceBirthStartAsync(start, timeout.Token)).NewlyRecorded);
                        births.Add((birth, plan, start));
                        CloudItem child = isDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
                        // Public remote placeholder population does not describe a local create.
                        // Create locally under protection, then use CfSharp's same-object conversion.
                        await role.RunNamespaceOperationAsync(async () =>
                        {
                            if (isDirectory) Directory.CreateDirectory(child.FullPath);
                            else
                            {
                                var roleOnly = new FileSecurity();
                                roleOnly.SetAccessRuleProtection(true, false);
                                roleOnly.AddAccessRule(new FileSystemAccessRule(role.RoleSid, FileSystemRights.FullControl, AccessControlType.Allow));
                                await using FileStream writer = new FileInfo(child.FullPath).Create(FileMode.CreateNew,
                                    FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, roleOnly);
                                await writer.WriteAsync(payload, timeout.Token);
                                writer.Flush(flushToDisk: true);
                            }
                            var ordinary = await child.InspectAsync(timeout.Token);
                            Assert.IsFalse(ordinary.IsPlaceholder);
                            Assert.IsNotNull(ordinary.LocalBinding);
                            originalBindings.Add(ordinary.LocalBinding);
                        });
                        if (!isDirectory)
                        {
                            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(child.FullPath, timeout.Token));
                            AssertBirthAliasDenied(Path.Combine(directory, "ordinary-birth.link"), child.FullPath, expectedError: 5);
                        }
                        await role.RunNamespaceOperationAsync(async () =>
                        {
                            if (isDirectory)
                                await child.ConvertToPlaceholderAsync(identity, CloudPlaceholderConversionOptions.CreateBuilder()
                                    .WithPopulationState(CloudDirectoryPopulationState.Complete).Build(), timeout.Token);
                            else
                            {
                                var binding = originalBindings[^1];
                                var retained = new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId);
                                var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, retained, identity,
                                    async (lease, stop) =>
                                    {
                                        await child.ConvertToPlaceholderAsync(identity, cancellationToken: stop);
                                        AssertBirthAliasDenied(Path.Combine(directory, "conversion-scope.link"), child.FullPath);
                                        await lease.ApplyDaclAsync(MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent.Intent.TargetDacl, false), stop);
                                        Assert.AreEqual(retained, (await lease.InspectAsync(stop)).LocalObject);
                                    }, timeout.Token);
                                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome, receipt.Error?.ToString());
                                Assert.IsTrue(receipt.CallbackStarted && receipt.CallbackCompleted && receipt.Drained);
                            }
                        });
                        // Keep actual native birth unrecorded until after the first CfSharp/catalog owner ends.
                        var snapshot = await child.InspectAsync(timeout.Token);
                        Assert.IsTrue(snapshot.IsPlaceholder);
                        Assert.AreEqual(originalBindings[^1], snapshot.LocalBinding);
                        Assert.AreEqual(plan.ItemId, snapshot.ItemId);
                        Assert.IsNull(snapshot.RemoteRevision);
                        Assert.AreEqual(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                        await using (var bornLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(child, router, timeout.Token))
                            Assert.AreEqual(MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent.Intent.TargetDacl, isDirectory),
                                (await bornLease.InspectAsync(timeout.Token)).Dacl);
                        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, timeout.Token));
                    }
                    AssertBirthAliasDenied(Path.Combine(directory, "before-write.link"), filePath);
                    await using (var writer = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        await writer.WriteAsync(payload, timeout.Token);
                        writer.Flush(flushToDisk: true);
                    }
                    AssertBirthAliasDenied(Path.Combine(directory, "after-write.link"), filePath);
                    CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(filePath, timeout.Token));
                    var pending = await WaitForBirthJournalAsync(feed, births[0].Plan.ItemId, births[1].Plan.ItemId, timeout.Token);
                    journalIds = pending.Select(change => change.OperationId).ToArray();
                    Assert.IsGreaterThan(0, journalIds.Length);
                }
                else
                {
                    foreach (var (birth, plan, start) in births)
                    {
                        Assert.IsFalse((await catalog.RecordNamespaceBirthStartAsync(start, timeout.Token)).NewlyRecorded);
                        CloudItem child = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
                        var observed = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, child, router, timeout.Token);
                        observations.Add(observed);
                        Assert.AreEqual(caller.User!.Value, observed.OwnerSid);
                        Assert.AreEqual(1U, observed.LinkCount);
                        Assert.AreEqual(plan.ItemId, observed.ItemId);
                        Assert.IsFalse(observed.IsInSync);
                        Assert.IsNull(observed.RemoteRevision);
                        Assert.AreEqual(originalBindings[observations.Count - 1].LocalFileId, observed.LocalObject.LocalFileId);
                        Assert.AreEqual(observed, await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, child, router, timeout.Token));
                    }
                    Assert.HasCount(2, observations);
                    CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(filePath, timeout.Token));
                    await File.AppendAllTextAsync(filePath, " latest", timeout.Token); // Existing content remains writable to the normal user.
                    AssertBirthAliasDenied(Path.Combine(directory, "after-normal-edit.link"), filePath);
                    Assert.AreEqual(Encoding.UTF8.GetString(payload) + " latest", await File.ReadAllTextAsync(filePath, timeout.Token));
                    var snapshot = await fileSystem.GetFile("Docs/born.bin").InspectAsync(timeout.Token);
                    Assert.IsTrue(snapshot.IsPlaceholder);
                    Assert.AreEqual(observations[0].LocalObject.LocalFileId, snapshot.LocalBinding!.LocalFileId);
                    Assert.AreEqual(births[0].Plan.ItemId, snapshot.ItemId);
                    var pending = await WaitForBirthJournalAsync(feed, births[0].Plan.ItemId, births[1].Plan.ItemId, timeout.Token);
                    CollectionAssert.IsSubsetOf(journalIds, pending.Select(change => change.OperationId).ToArray());
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(filePath), timeout.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => Directory.Delete(Path.Combine(docs.FullPath, "empty")), timeout.Token));
                    Assert.HasCount(2, await catalog.ReadNamespacePermissionChangesAsync(timeout.Token));
                    foreach (var original in originals)
                        Assert.AreEqual(original.Baseline, await catalog.ReadNamespacePermissionBaselineAsync(original.Baseline.EvidenceId, timeout.Token));
                }
                Assert.AreEqual(0, source.Reads);
                foreach (var preparation in originals)
                {
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                        preparation.Baseline.RootId is null ? fileSystem.Root : docs, router, timeout.Token);
                    Assert.AreEqual(preparation.Intent.TargetDacl, (await lease.InspectAsync(timeout.Token)).Dacl);
                }
                TestContext.WriteLine($"ControlledBirth: owner={owner}; architecture={RuntimeInformation.ProcessArchitecture}; roleOnlyOrdinaryBirth=True; sameObjectConversion=True; fileModeOpen=True; aliasesDenied=True; originalsRetained=True; nativeBirthUnrecorded={owner == 0}; officialJournalIds={journalIds.Length}; sourceReads={source.Reads}; parentAclClosed=True; elevatedCaller={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; hostIntegrated=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private async Task<CloudLocalChange[]> WaitForBirthJournalAsync(CloudLocalChangeFeed feed, Guid file, Guid directory, CancellationToken token)
    {
        string? previousDiagnostic = null;
        while (true)
        {
            var scan = await feed.BeginScanAsync(token);
            Assert.IsFalse(scan.RequiresFullRescan);
            var changes = new List<CloudLocalChange>();
            long after = 0;
            while (true)
            {
                var page = await feed.ReadPageAsync(scan, after, 64, token);
                Assert.IsFalse(page.RequiresFullRescan);
                changes.AddRange(page.Changes.Where(change => change.ItemId == file || change.ItemId == directory));
                if (!page.HasMore) break;
                Assert.IsGreaterThan(after, page.LastScannedSequence);
                after = page.LastScannedSequence;
            }
            string diagnostic = $"count={changes.Count}; fileCreate={changes.Count(change => change.ItemId == file && change.Kind == CloudLocalChangeKind.Create)}; directoryCreate={changes.Count(change => change.ItemId == directory && change.Kind == CloudLocalChangeKind.Create)}";
            if (diagnostic != previousDiagnostic)
            {
                TestContext.WriteLine("ControlledBirthJournal: " + diagnostic);
                previousDiagnostic = diagnostic;
            }
            if (changes.Any(change => change.ItemId == file && change.Kind == CloudLocalChangeKind.Create && !change.IsDirectory) &&
                changes.Any(change => change.ItemId == directory && change.Kind == CloudLocalChangeKind.Create && change.IsDirectory))
                return changes.ToArray();
            await Task.Delay(25, token);
        }
    }

    private void AssertBirthAliasDenied(string alias, string target, int expectedError = 396)
    {
        bool created = CreateBirthHardLink(alias, target, nint.Zero);
        int error = created ? 0 : Marshal.GetLastPInvokeError();
        TestContext.WriteLine($"ControlledBirthAlias: name={Path.GetFileName(alias)}; created={created}; nativeError={error}.");
        Assert.IsFalse(created);
        Assert.AreEqual(expectedError, error); // Access denied before conversion; incompatible Cloud Files hardlinks afterwards.
        Assert.IsFalse(File.Exists(alias));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateBirthHardLink(string alias, string target, nint attributes);
}
