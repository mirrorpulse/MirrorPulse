using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using Microsoft.Data.Sqlite;
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
    public async Task NativeProtectedRootLabelExecutionRecoversOriginalSubtreeAndCaseOnlyHistoryAcrossOwners()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (string cut in new[] { "none", "start-write", "after-native", "after-observation", "after-projection", "atomic-write", "client-return" })
                await ProbeProtectedRootLabelExecutionAsync(disabled, cut);
    }

    private async Task ProbeProtectedRootLabelExecutionAsync(bool disabled, string cut)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.root-label-execution"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var setupSource = new ProtectedPermissionNoSourceProvider();
        var ranges = new RootLabelExecutionRejectedRanges();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseRootRenameHistory original = null!;
        MirrorPulseNamespaceMemberMoveIntent intent = null!;
        MirrorPulseNamespaceDirectoryMoveSnapshot sealedTree = null!;
        MirrorPulseNamespaceMemberMoveStart? firstStart = null;
        MirrorPulseNamespaceMemberMoveObservation? firstNative = null;
        MirrorPulseNamespaceMemberMoveProjection? firstProjection = null;
        MirrorPulseRootRenameObservation? firstLabelNative = null;
        MirrorPulseNamespaceMemberRegistration rootMember = null!;
        byte[] bytes = [1, 2, 3, 5, 8, 13];
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                // The actual root move and recovery use the production policy. Setup only
                // uses the established no-source fixture for its earlier child movement.
                ICloudFileContentProvider provider = owner == 0 ? setupSource : new MirrorPulseDemandProvider(router, ranges, catalog: catalog);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                await using var journal = owner == 0 ? null : new RootLabelJournalObservation(TestContext, fileSystem,
                    intent.ItemId, root.State, cut, owner);
                if (journal is not null) await journal.StartAsync(deadline.Token);
                if (owner == 0)
                {
                    await using (var feed = fileSystem.CreateLocalChangeFeed())
                    {
                        await feed.StartAsync(deadline.Token);
                        await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token);
                    }
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, bytes, deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    rootMember = members.Values.Single(member => member.IsDirectory && member.RootId is not null);
                    var originalFile = members.Values.Single(member => !member.IsDirectory);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    var left = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "left", true, deadline.Token);
                    var nested = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "nested", true, deadline.Token);
                    var child = await coordinator.PrepareAsync(Guid.NewGuid(), originalFile.MemberId, nested.MemberId, "pending.bin", deadline.Token);
                    await coordinator.ExecuteAsync(child.OperationId, deadline.Token);
                    if (disabled)
                    {
                        await catalog.SetInstanceEnabledAsync(root.InstanceId, false, deadline.Token);
                        root = (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
                        router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(deadline.Token));
                    }
                    await role.RunNamespaceOperationAsync(async () => original = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                        .PrepareAsync(root.RootId, "Renamed", deadline.Token));
                    intent = await coordinator.PrepareRootLabelAsync(original.Intent.OperationId, deadline.Token);
                    sealedTree = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(intent.OperationId, deadline.Token))!;
                    Assert.HasCount(4, sealedTree.Entries);
                    Assert.AreEqual(child.OperationId, sealedTree.Entries.Single(entry => entry.MemberId == originalFile.MemberId).PreviousMoveOperationId);
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                    continue;
                }
                Assert.AreEqual(intent, await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId, deadline.Token));
                if (owner == 1)
                {
                    string? trigger = cut switch
                    {
                        "start-write" => $"BEFORE INSERT ON namespace_member_move_starts WHEN NEW.operation_id='{intent.OperationId:D}'",
                        "atomic-write" => $"BEFORE UPDATE ON managed_root_rename_history WHEN NEW.operation_id='{intent.OperationId:D}' AND json_extract(NEW.payload,'$.Phase')=3",
                        _ => null
                    };
                    if (trigger is not null)
                        await ExecuteTransferPublicationFixtureSqlAsync(paths, $"CREATE TRIGGER fail_root_label_execution {trigger} BEGIN SELECT RAISE(ABORT,'root-label-original-gap'); END;", deadline.Token);
                    var interrupted = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor,
                        (checkpoint, _) =>
                        {
                            if (cut == "after-native" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove ||
                                cut == "after-observation" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation ||
                                cut == "after-projection" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection)
                                throw new IOException("Injected original protected root Label interruption.");
                            return ValueTask.CompletedTask;
                        });
                    try
                    {
                        if (trigger is not null)
                        {
                            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => interrupted.ExecuteRootLabelAsync(intent.OperationId, deadline.Token).AsTask());
                            StringAssert.Contains(failure.Message, "root-label-original-gap");
                        }
                        else if (cut.StartsWith("after-", StringComparison.Ordinal))
                        {
                            var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ExecuteRootLabelAsync(intent.OperationId, deadline.Token).AsTask());
                            StringAssert.Contains(failure.Message, "Injected original protected root Label interruption.");
                        }
                        else if (cut == "client-return")
                        {
                            var failure = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                            {
                                await interrupted.ExecuteRootLabelAsync(intent.OperationId, deadline.Token);
                                throw new IOException("Synthetic client return loss after complete protected root Label execution.");
                            });
                            StringAssert.Contains(failure.Message, "Synthetic client return loss after complete protected root Label execution.");
                        }
                        else await interrupted.ExecuteRootLabelAsync(intent.OperationId, deadline.Token);
                    }
                    finally
                    {
                        if (trigger is not null) await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER fail_root_label_execution;", deadline.Token);
                    }
                    firstStart = await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token);
                    firstNative = await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token);
                    firstProjection = await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token);
                    firstLabelNative = (await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token))!.Observation;
                    bool completed = cut is "none" or "client-return";
                    Assert.AreEqual(completed, await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token) is not null);
                    if (!completed)
                    {
                        Assert.AreEqual(root, (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single());
                        Assert.AreEqual(intent, await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                        foreach (var entry in sealedTree.Entries)
                        {
                            Assert.AreEqual(entry.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, deadline.Token)).RelativePath);
                            Assert.IsNull(await catalog.ReadNamespaceMemberPositionAsync(entry.MemberId, intent.OperationId, deadline.Token));
                        }
                    }
                    if (cut == "start-write")
                    {
                        Assert.IsNull(firstStart); Assert.IsNull(firstNative); Assert.IsNull(firstProjection); Assert.IsNull(firstLabelNative);
                        Assert.IsTrue((await fileSystem.GetDirectory("Docs").InspectAsync(deadline.Token)).Exists);
                        Assert.IsFalse((await fileSystem.GetDirectory("Renamed").InspectAsync(deadline.Token)).Exists);
                    }
                    await journal!.CaptureAsync(cut == "start-write" ? "Docs" : "Renamed", deadline.Token);
                    continue;
                }
                var completion = await coordinator.ExecuteRootLabelAsync(intent.OperationId, deadline.Token);
                var receipt = (await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token))!;
                Assert.AreEqual(completion, receipt.Root); Assert.HasCount(4, receipt.Entries);
                Assert.AreEqual(2, completion.Version);
                Assert.IsTrue(receipt.PublicResult.NativeMoveObserved && receipt.PublicResult.DurableProjectionCommitted);
                Assert.IsFalse(receipt.PublicResult.RequiresFullRescan);
                Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                var label = (await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token))!;
                Assert.AreEqual(original.Proof, label.Proof); Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, label.Intent.Phase);
                if (firstStart is not null) Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                if (firstNative is not null) Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token));
                if (firstProjection is not null) Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token));
                if (firstLabelNative is not null) Assert.AreEqual(firstLabelNative, label.Observation);
                foreach (var entry in sealedTree.Entries)
                {
                    string expected = "Renamed" + entry.RelativePath["Docs".Length..];
                    Assert.AreEqual(expected, (await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, deadline.Token)).RelativePath);
                    CloudItem actual = entry.IsDirectory ? fileSystem.GetDirectory(expected) : fileSystem.GetFile(expected);
                    Assert.AreEqual(entry.LocalObject, NativeMemberBinding(await actual.InspectAsync(deadline.Token)));
                    Assert.AreEqual(intent.OperationId, (await catalog.ReadNamespaceMemberPositionAsync(entry.MemberId, intent.OperationId, deadline.Token))!.OperationId);
                }
                MirrorPulseRootRenameHistory next = null!;
                await role.RunNamespaceOperationAsync(async () => next = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                    .PrepareAsync(root.RootId, "RENAMED", deadline.Token));
                await coordinator.PrepareRootLabelAsync(next.Intent.OperationId, deadline.Token);
                await coordinator.ExecuteRootLabelAsync(next.Intent.OperationId, deadline.Token);
                var later = await catalog.ReadManagedRootRenameAsync(root.RootId, deadline.Token);
                Assert.AreEqual(completion, await coordinator.ExecuteRootLabelAsync(intent.OperationId, deadline.Token));
                Assert.AreEqual(label, await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token));
                Assert.AreEqual(later, await catalog.ReadManagedRootRenameAsync(root.RootId, deadline.Token));
                Assert.AreEqual("RENAMED", router.FindRegistration(root.RootId)!.DirectoryName);
                Assert.AreEqual(root.State, router.FindRegistration(root.RootId)!.State);
                string payload = fileSystem.GetFile("RENAMED/left/nested/pending.bin").FullPath;
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
                await journal!.CaptureAsync("RENAMED", deadline.Token);
                Assert.AreEqual(0, setupSource.Reads); Assert.AreEqual(0, ranges.Calls);
            }
            TestContext.WriteLine($"ProtectedRootLabelExecution: state={root.State}; cut={cut}; originalProof=True; productionRootPolicy=True; entireProtectedTree=True; originalFactsImmutable=True; atomicAllPositionsAndLabel=True; caseOnlyNextLabel=True; historicalReplayPreservesCurrent=True; ordinaryDeleteDenied=True; sourceReads=0; owners=3; newRoleRecovery=False; hostIntegrated=False.");
        }
        catch (Exception failure) { TestContext.WriteLine("ProtectedRootLabelExecutionFailure: " + failure); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreRootLabelExecutionFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        // Authenticate the exact marked GUID fixture and sync-root path first.
        RestoreSourceContextFixtureForCleanup(directory, syncRoot, user);
        foreach (string rootName in new[] { "Docs", "Renamed" })
        {
            foreach (string suffix in new[] { "", "left", "left/nested" })
            {
                var info = new DirectoryInfo(Path.Combine(syncRoot, rootName, suffix.Replace('/', Path.DirectorySeparatorChar)));
                if (!info.Exists) continue;
                if (info.LinkTarget is not null) throw new InvalidOperationException("Root Label cleanup cannot follow a directory link.");
                var descriptor = new DirectorySecurity();
                descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                descriptor.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(descriptor);
            }
            foreach (string suffix in new[] { "original.bin", "left/nested/pending.bin" })
            {
                var info = new FileInfo(Path.Combine(syncRoot, rootName, suffix.Replace('/', Path.DirectorySeparatorChar)));
                if (!info.Exists) continue;
                if (info.LinkTarget is not null) throw new InvalidOperationException("Root Label cleanup cannot follow a file link.");
                var descriptor = new FileSecurity();
                descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                descriptor.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                info.SetAccessControl(descriptor);
            }
        }
    }

    private sealed class RootLabelExecutionRejectedRanges : IMirrorPulseWorkerRangeTransport
    {
        internal int Calls { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<Stream>(new AssertFailedException("Protected root Label execution must not read a source."));
        }
    }
}
