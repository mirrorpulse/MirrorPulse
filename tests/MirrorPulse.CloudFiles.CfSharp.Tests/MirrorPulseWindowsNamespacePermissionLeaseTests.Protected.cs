using System.Runtime.InteropServices;
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
    private async Task VerifyProtectedCoordinatorAcrossRestartAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.protected-permission"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var provider = new ProtectedPermissionNoSourceProvider();
        MirrorPulseNamespacePermissionBaseline[]? originals = null;
        MirrorPulseNamespacePermissionIntent[]? protections = null;
        Guid manifestId = Guid.NewGuid();
        MirrorPulseNamespacePermissionLocalIdentity? retainedFileIdentity = null;
        MirrorPulseNamespacePermissionLocalIdentity? retainedColdIdentity = null;
        CloudLocalFileBinding? firstBinding = null;
        string latest = "original unsent bytes before protected permission application";
        string filePath = Path.Combine(paths.SyncRootPath, "Docs", "unsent.txt");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".mp-protected-permission-fixture"), "synthetic", timeout.Token);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int owner = 0; owner < 2; owner++)
            {
                await using var role = new MirrorPulseNamespaceExecutionSession();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(provider).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                    await File.WriteAllTextAsync(filePath, latest, timeout.Token);
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("cold.bin", router.CreateFileIdentity(registration.InstanceId, "docs", "cold", "accepted-v1"), 16)
                            .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                }
                CloudItem[] items = [fileSystem.GetFile("Docs/unsent.txt"), fileSystem.GetFile("Docs/cold.bin"), fileSystem.GetDirectory("Docs")];
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
                if (owner == 0)
                {
                    originals = new MirrorPulseNamespacePermissionBaseline[items.Length];
                    protections = new MirrorPulseNamespacePermissionIntent[items.Length];
                    for (int index = 0; index < items.Length; index++)
                    {
                        await using var capture = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(items[index], router, timeout.Token);
                        var observed = await capture.InspectAsync(timeout.Token);
                        originals[index] = new(Guid.NewGuid(), observed.RootId, observed.LocalObject, observed.RelativePath,
                            observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                        protections[index] = new(Guid.NewGuid(), originals[index].EvidenceId, observed.LocalObject, observed.RootId,
                            observed.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value, observed.Dacl,
                            MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, observed.IsDirectory), DateTimeOffset.UtcNow);
                        Assert.AreNotEqual(observed.Dacl, protections[index].TargetDacl);
                    }
                    var anchor = new MirrorPulseNamespacePermissionPreparation(originals[2], protections[2]);
                    var definition = new MirrorPulseNamespacePermissionTreeDefinition(manifestId, anchor,
                        items.Length, originals.Min(original => original.CapturedAt));
                    await catalog.CreateNamespacePermissionTreeAsync(definition, timeout.Token);
                    await catalog.AppendNamespacePermissionTreeMembersAsync(manifestId,
                        [new(0, null, anchor), new(1, anchor.Baseline.EvidenceId, new(originals[0], protections[0])),
                            new(2, anchor.Baseline.EvidenceId, new(originals[1], protections[1]))], timeout.Token);
                    await catalog.SealNamespacePermissionTreeAsync(manifestId, DateTimeOffset.UtcNow, timeout.Token);
                    CloudItemSnapshot before = await items[0].InspectAsync(timeout.Token);
                    firstBinding = before.LocalBinding;
                    Assert.IsNotNull(firstBinding);
                    CloudPlaceholderIdentity mapped = router.CreateFileIdentity(registration.InstanceId, "docs", "host-selected-local");
                    var candidate = new CloudPlaceholderIdentity(mapped.ItemId, mapped.RemoteId);
                    await using (var treeLease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                        fileSystem.GetDirectory("Docs"), router, timeout.Token))
                    {
                        var tree = await coordinator.ReconcileTreeAsync(manifestId, treeLease, role,
                            baseline => baseline.RelativePath == "Docs/unsent.txt" ? candidate : null, timeout.Token);
                        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, tree.Outcome);
                        Assert.AreEqual(3, tree.CompletedMembers);
                        TestContext.WriteLine($"ProtectedOwnedTree: architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; originalSeal=True; retainedMetadata=True; bottomUpApplication=True; independentLiveAudit=True; completed={tree.CompletedMembers}; hostEnabled=False.");
                    }
                    string addedPath = Path.Combine(paths.SyncRootPath, "Docs", "unexpected.txt");
                    await role.RunNamespaceOperationAsync(() => File.WriteAllTextAsync(addedPath, "synthetic extra member", timeout.Token));
                    await using (var changedLease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                        fileSystem.GetDirectory("Docs"), router, timeout.Token))
                    {
                        var changed = await coordinator.ReconcileTreeAsync(manifestId, changedLease, role,
                            cancellationToken: timeout.Token);
                        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, changed.Outcome);
                        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged, changed.RecoveryReason);
                        Assert.AreEqual(0, changed.CompletedMembers);
                    }
                    await role.RunNamespaceOperationAsync(() => { File.Delete(addedPath); return Task.CompletedTask; });
                    for (int index = 0; index < items.Length; index++)
                    {
                        var applied = await ApplyOwnedAsync(protections[index], items[index], index == 0 ? candidate : null);
                        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, applied.Permission!.Outcome);
                        Assert.IsFalse(applied.Receipt!.AccessDescriptorApplied);
                        Assert.IsTrue(applied.Receipt.AccessDescriptorReadBack);
                        if (index == 0)
                        {
                            Assert.IsTrue(applied.Receipt.NativeIdentityPrepared);
                            Assert.IsTrue(applied.Receipt.DurableProjectionCommitted);
                            retainedFileIdentity = (await catalog.ReadNamespacePermissionLocalIdentityAsync(originals[index].EvidenceId, timeout.Token))!;
                            Assert.AreEqual(before.ItemId ?? candidate.ItemId, retainedFileIdentity.ItemId);
                            Assert.AreEqual(before.RemoteId ?? candidate.RemoteId, retainedFileIdentity.RemoteId);
                        }
                        else Assert.IsFalse(applied.Receipt.NativeConverted);
                    }
                    retainedColdIdentity = (await catalog.ReadNamespacePermissionLocalIdentityAsync(originals[1].EvidenceId, timeout.Token))!;
                    Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(originals[2].EvidenceId, timeout.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(filePath), timeout.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(filePath, filePath + ".renamed"), timeout.Token));
                    latest = "latest in-place unsent bytes after protection, before restart";
                    await File.WriteAllTextAsync(filePath, latest, timeout.Token);
                    CloudItemSnapshot edited = await items[0].InspectAsync(timeout.Token);
                    Assert.AreEqual(firstBinding, edited.LocalBinding);
                    var reprepared = await ApplyOwnedAsync(protections[0], items[0], new(Guid.NewGuid(), "ignored-later-candidate"));
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, reprepared.Permission!.Outcome);
                    Assert.AreEqual(!edited.IsPlaceholder, reprepared.Receipt!.NativeConverted);
                    Assert.IsFalse(reprepared.Receipt.AccessDescriptorApplied);
                    Assert.AreEqual(retainedFileIdentity, await catalog.ReadNamespacePermissionLocalIdentityAsync(originals[0].EvidenceId, timeout.Token));
                    TestContext.WriteLine($"ProtectedOwnedEdit: ordinaryAfterEdit={!edited.IsPlaceholder}; reprepared={reprepared.Receipt.NativeConverted}; originalBinding=True; noAclRewrite=True; latestBytesRetained=True.");
                }
                else
                {
                    Assert.AreEqual(retainedFileIdentity, await catalog.ReadNamespacePermissionLocalIdentityAsync(originals![0].EvidenceId, timeout.Token));
                    Assert.AreEqual(retainedColdIdentity, await catalog.ReadNamespacePermissionLocalIdentityAsync(originals[1].EvidenceId, timeout.Token));
                    for (int index = 0; index < items.Length; index++)
                    {
                        var replayed = await ApplyOwnedAsync(protections![index], items[index], null);
                        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, replayed.Permission!.Outcome);
                        Assert.IsFalse(replayed.Receipt!.NativeConverted);
                        Assert.IsFalse(replayed.Receipt.AccessDescriptorApplied);
                        if (index == 0)
                        {
                            Assert.IsTrue(replayed.Receipt.NativeIdentityPrepared);
                            Assert.IsTrue(replayed.Receipt.DurableProjectionCommitted);
                        }
                        var previous = (await catalog.ReadNamespacePermissionChangeAsync(protections[index].OperationId, timeout.Token))!;
                        var rotation = protections[index] with
                        {
                            OperationId = Guid.NewGuid(),
                            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                            RoleSid = role.RoleSid.Value,
                            ExpectedDacl = previous.Verification!.Dacl,
                            TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, originals[index].IsDirectory),
                            PreparedAt = DateTimeOffset.UtcNow,
                        };
                        Assert.AreNotEqual(rotation.ExpectedDacl, rotation.TargetDacl);
                        await catalog.PrepareNamespacePermissionChangeAsync(originals[index], rotation, timeout.Token);
                        var rotated = await ApplyOwnedAsync(rotation, items[index], null);
                        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, rotated.Permission!.Outcome);
                        Assert.IsTrue(rotated.Receipt!.AccessDescriptorApplied);
                        Assert.IsFalse(rotated.Receipt.NativeConverted);
                    }
                }
                CloudItemSnapshot after = await items[0].InspectAsync(timeout.Token);
                Assert.AreEqual(firstBinding, after.LocalBinding);
                Assert.IsTrue(after.IsPlaceholder);
                Assert.AreEqual(retainedFileIdentity!.ItemId, after.ItemId);
                Assert.AreEqual(retainedFileIdentity.RemoteId, after.RemoteId);
                Assert.IsNull(after.RemoteRevision);
                Assert.AreEqual(CloudSynchronizationState.NotInSync, after.SynchronizationState);
                Assert.AreEqual(latest, await File.ReadAllTextAsync(filePath, timeout.Token));
                CloudItemSnapshot cold = await items[1].InspectAsync(timeout.Token);
                Assert.IsTrue(cold.IsPlaceholder);
                Assert.AreEqual(0L, cold.OnDiskDataSize);
                Assert.AreEqual(retainedColdIdentity!.ItemId, cold.ItemId);
                Assert.AreEqual("accepted-v1", cold.RemoteRevision);
                Assert.AreEqual(CloudSynchronizationState.InSync, cold.SynchronizationState);
                Assert.AreEqual(0, provider.Reads);
                for (int index = 0; index < originals!.Length; index++)
                    Assert.AreEqual(originals[index], await catalog.ReadNamespacePermissionBaselineAsync(originals[index].EvidenceId, timeout.Token));

                async Task<MirrorPulseProtectedNamespacePermissionResult> ApplyOwnedAsync(
                    MirrorPulseNamespacePermissionIntent intent, CloudItem item, CloudPlaceholderIdentity? candidate)
                {
                    MirrorPulseProtectedNamespacePermissionResult? result = null;
                    await role.RunNamespaceOperationAsync(async () => result = await coordinator.ApplyProtectedAsync(
                        intent.OperationId, item, router, candidate, timeout.Token));
                    Assert.IsNotNull(result);
                    Assert.IsNotNull(result.Receipt);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, result.Receipt.Outcome, result.Receipt.Error?.ToString());
                    Assert.AreEqual(0, provider.Reads);
                    var change = (await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId, timeout.Token))!;
                    Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, change.Phase);
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(intent.TargetDacl, change.Verification!.Dacl));
                    return result;
                }
            }
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            TestContext.WriteLine($"ProtectedOwnedCoordinator: architecture={RuntimeInformation.ProcessArchitecture}; twoOfficialSqliteOwners=True; productCatalogPhases=True; originalBindingAndDaclRetained=True; actualDesiredDaclApplied=True; ordinaryDeleteRenameDenied=True; inPlaceEditReprepared=True; knownIdentityRetained=True; coldAccessOnly=True; coldSourceReads=0; acceptedRevisionRetained=True; roleRotation=True; falseRemoteAcceptance=False; hostTreeEnforcement=False; installedIdentity=False; elevatedCaller={new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator)}; OS={Environment.OSVersion.Version}.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (originals is not null)
                foreach (var original in originals.Where(value => value is not null))
                {
                    string path = Path.Combine(paths.SyncRootPath, original.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    FileSystemSecurity access = original.IsDirectory ? new DirectorySecurity() : new FileSecurity();
                    access.SetSecurityDescriptorSddlForm(original.OriginalDacl, AccessControlSections.Access);
                    if (original.IsDirectory && Directory.Exists(path)) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)access);
                    else if (!original.IsDirectory && File.Exists(path)) new FileInfo(path).SetAccessControl((FileSecurity)access);
                }
            DeleteProtectedPermissionFixture(directory);
        }
    }

    private static void DeleteProtectedPermissionFixture(string directory)
    {
        if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, ".mp-protected-permission-fixture")))
            throw new InvalidOperationException("The protected permission fixture marker is missing.");
        DeleteFixture(directory);
    }

    private sealed class ProtectedPermissionNoSourceProvider : ICloudFileContentProvider
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidOperationException("Protected metadata and complete local bytes must not read a source.");
        }
    }
}
