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
    public async Task NativeRootLabelJournalAdmissionRequiresCurrentTreeAndActualSdkReturnBeforeReadmission()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (string cut in new[] { "none", "ack-return", "post-ack" })
                await ProbeRootLabelJournalAdmissionAsync(disabled, cut);
    }

    private async Task ProbeRootLabelJournalAdmissionAsync(bool disabled, string cut)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.root-journal-admission"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] bytes = [2, 3, 5, 7, 11, 13];
        var provider = new ProtectedPermissionNoSourceProvider();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            await using var role = new MirrorPulseNamespaceExecutionSession();
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
            await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
            await using var feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(deadline.Token);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token);
            await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, bytes, deadline.Token);
            foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetFile("Docs/original.bin") })
                SetProtectedFixtureUserOwner(item, caller.User!);
            await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
            await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
            await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, role,
                actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token);
            await using var anchor = initialized.TransferAnchor();
            var rootMember = (await catalog.ReadNamespaceMemberChildAsync(initialized.Completion.AnchorMemberId, "Docs", deadline.Token))!;
            var file = (await catalog.ReadNamespaceMemberChildAsync(rootMember.MemberId, "original.bin", deadline.Token))!;
            var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
            var child = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "child", true, deadline.Token);
            var moves = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
            var nestedMove = await moves.PrepareAsync(Guid.NewGuid(), file.MemberId, child.MemberId, "pending.bin", deadline.Token);
            await moves.ExecuteAsync(nestedMove.OperationId, deadline.Token);
            if (disabled)
            {
                await catalog.SetInstanceEnabledAsync(root.InstanceId, false, deadline.Token);
                root = (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
                router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(deadline.Token));
            }
            var audits = new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, role, actor, permissions);
            await using (var admitted = await work.BeginRecoveryAsync([root.RootId], deadline.Token))
            {
                await audits.AuditRootAsync(initialized.Completion.AnchorMemberId, anchor, root.RootId, deadline.Token);
                if (!disabled) admitted.CompleteAfterFreshAudit();
            }
            MirrorPulseRootRenameHistory history = null!;
            await role.RunNamespaceOperationAsync(async () => history = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                .PrepareAsync(root.RootId, "Renamed", deadline.Token));
            await moves.PrepareRootLabelAsync(history.Intent.OperationId, deadline.Token);
            await moves.ExecuteRootLabelAsync(history.Intent.OperationId, deadline.Token);

            MirrorPulseLocalChangeObservation? original = null;
            while (original is null)
            {
                var scan = await feed.BeginScanAsync(deadline.Token);
                Assert.IsFalse(scan.RequiresFullRescan, "Lost journal history cannot supply an original root notification.");
                long after = 0; bool more;
                do
                {
                    var page = await feed.ReadPageAsync(scan, after, 128, deadline.Token);
                    Assert.IsFalse(page.RequiresFullRescan);
                    var change = page.Changes.SingleOrDefault(change => change.Kind == CloudLocalChangeKind.Move &&
                        change.IsDirectory && change.ItemId == history.Proof!.ItemId && change.RelativePath == "Renamed" && change.PreviousRelativePath == "Docs");
                    if (change is not null) original = new(change.OperationId, change.Sequence, change.Kind, change.ItemId,
                        change.RelativePath, change.PreviousRelativePath, change.IsDirectory, change.ObservedAt);
                    if (page.HasMore) Assert.IsGreaterThan(after, page.LastScannedSequence);
                    after = page.LastScannedSequence; more = page.HasMore;
                } while (more);
                if (original is null) await Task.Delay(25, deadline.Token);
            }

            bool loseReturn = cut == "ack-return"; int actualReturns = 0;
            var replay = new MirrorPulseRootLabelJournalRecovery(catalog, actor, async (id, stop) =>
            {
                await role.RunNormalUserOperationAsync(async () =>
                {
                    using WindowsIdentity current = WindowsIdentity.GetCurrent();
                    Assert.IsFalse(new WindowsPrincipal(current).IsInRole(role.RoleSid));
                    await feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(id, null)], stop);
                });
                if (id == original.OperationId && ++actualReturns == 1 && loseReturn)
                    throw new IOException("Injected loss after the actual public SDK return.");
            });
            var admission = new MirrorPulseRootLabelJournalAdmission(fileSystem, catalog, router, role, actor, permissions, work, replay, () => anchor)
            {
                AfterAcknowledgement = cut == "post-ack"
                    ? _ => ValueTask.FromException(new IOException("Injected loss after the durable actual SDK ACK receipt.")) : null
            };
            Assert.IsFalse(await admission.TryAcceptAsync(original with { ItemId = null }, deadline.Token));
            Assert.IsFalse(await admission.TryAcceptAsync(original with { ItemId = Guid.NewGuid() }, deadline.Token));
            Assert.IsFalse(await admission.TryAcceptAsync(original with { IsDirectory = false }, deadline.Token));
            Assert.IsNull(await catalog.ReadRootLabelJournalAcknowledgementAsync(original.OperationId, deadline.Token));
            Assert.AreEqual(!disabled, work.IsAdmitted(root.RootId));

            // A controlled-transfer reader may own ordinary root work. It must not
            // invoke new admission or wait for its own lease to drain.
            using (var held = disabled ? null : work.Enter([root.RootId]))
            {
                var transferSource = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => !disabled,
                    namespaceWork: work, rootLabelJournal: replay);
                _ = await transferSource.ReadPendingAsync(deadline.Token);
                Assert.IsNull(await catalog.ReadRootLabelJournalAcknowledgementAsync(original.OperationId, deadline.Token));
                Assert.AreEqual(0, actualReturns);
            }
            var ordinary = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => !disabled,
                namespaceWork: work, rootLabelJournal: replay, rootLabelAdmission: admission);
            MirrorPulseRootLabelJournalAcknowledgement first;
            if (cut != "none")
            {
                var deferred = await ordinary.ReadPendingAsync(deadline.Token);
                Assert.IsFalse(deferred.ReadyCommands.Any(command => command.OperationId == original.OperationId));
                var failure = admission.TakeFailures().Single(item => item.OperationId == original.OperationId).Failure;
                Assert.IsInstanceOfType<IOException>(failure);
                StringAssert.Contains(failure.Message, "Injected loss");
                first = (await catalog.ReadRootLabelJournalAcknowledgementAsync(original.OperationId, deadline.Token))!;
                Assert.IsNotNull(first);
                Assert.IsFalse(work.IsAdmitted(root.RootId)); Assert.IsTrue(admission.HasPendingReadmission);
                if (loseReturn)
                {
                    Assert.IsNull(first.AcknowledgedAt);
                    CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).ToArray(), root.RootId);
                    var recovered = await replay.RecoverPendingAsync(token: deadline.Token);
                    Assert.IsNull(recovered.Single(item => item.OperationId == original.OperationId).Failure);
                    Assert.AreEqual(2, actualReturns);
                }
                else
                {
                    Assert.IsNotNull(first.AcknowledgedAt);
                    Assert.IsEmpty(await replay.RecoverPendingAsync(token: deadline.Token));
                    Assert.IsFalse((await catalog.ReadBlockedLocalOperationsAsync(deadline.Token)).Any(item => item.OperationId == original.OperationId));
                    Assert.AreEqual(1, actualReturns);
                }
                var readmitted = await admission.RecoverClosedAsync(deadline.Token);
                Assert.IsNull(readmitted.Single(item => item.RootId == root.RootId).Failure);
                first = first with { AcknowledgedAt = null };
            }
            else
            {
                var batch = await ordinary.ReadPendingAsync(deadline.Token);
                Assert.IsFalse(batch.ReadyCommands.Any(command => command.OperationId == original.OperationId));
                first = (await catalog.ReadRootLabelJournalAcknowledgementAsync(original.OperationId, deadline.Token))! with { AcknowledgedAt = null };
                Assert.AreEqual(1, actualReturns);
            }
            var completed = (await catalog.ReadRootLabelJournalAcknowledgementAsync(original.OperationId, deadline.Token))!;
            Assert.IsNotNull(completed.AcknowledgedAt);
            Assert.AreEqual(first, completed with { AcknowledgedAt = null });
            Assert.AreEqual(role.RoleSid.Value, completed.Witness.RoleSid);
            Assert.AreEqual(original.OperationId, completed.Observation.OperationId);
            Assert.AreEqual("Renamed", completed.Witness.DirectoryName);
            Assert.AreEqual(!disabled, work.IsAdmitted(root.RootId));
            Assert.IsFalse(admission.HasPendingReadmission);
            Assert.IsNull(await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(root.RootId, deadline.Token));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Renamed", "child", "pending.bin"), deadline.Token));
            Assert.AreEqual(0, provider.Reads);
            TestContext.WriteLine($"RootLabelJournalAdmissionActual: disabled={disabled}; cut={cut}; lostReturn={loseReturn}; actualSdkReturns={actualReturns}; originalFirstStartPreserved=True; freshCurrentTreeBeforeStart=True; nativeGuardsRetained=True; normalOwnerAck=True; currentAuditBeforeActiveReadmission=True; disabledRemainsClosed=True; controlledTransferOriginalOnly=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
