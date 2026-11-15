using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using MirrorPulse.Core.Tests;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesAcrossTwoSingleRootInstances() => VerifySignedDirectoryTransferAsync(null);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesAfterNativeDeleteAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesBeforeNativeDeleteAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesAfterWorkerDeleteAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.BeforeFirstDirectoryNativeRetirement);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesBeforeWorkerReceiptAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.AfterDirectoryWorkerDeletionBeforeReceipt);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesBeforeWorkerDispatchAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletion);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesBeforeWorkerStartAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletionStart);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferResumesPartialTargetPublicationAcrossHostRoles() =>
        VerifySignedDirectoryTransferAsync(null, interruptDuringPublication: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public Task NativeSignedLocalDirectoryTransferToWebDavRejectsUnsupportedReverseBeforeMutation()
    {
        string? webDavPackage = Environment.GetEnvironmentVariable("MP_SIGNED_WEBDAV_PACKAGE");
        if (string.IsNullOrWhiteSpace(webDavPackage)) Assert.Inconclusive("Requires the frozen signed WebDAV package.");
        return VerifySignedDirectoryTransferAsync(webDavPackage);
    }

    private async Task VerifySignedDirectoryTransferAsync(string? webDavPackage,
        MirrorPulseTransferSourceRetirementCheckpoint? restartCut = null, bool interruptDuringPublication = false)
    {
        bool restartDuringTransfer = restartCut is not null || interruptDuringPublication;
        bool interruptBeforeNativeDelete = restartCut == MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion;
        bool interruptAfterWorkerDelete = restartCut == MirrorPulseTransferSourceRetirementCheckpoint.BeforeFirstDirectoryNativeRetirement;
        bool interruptBeforeWorkerReceipt = restartCut == MirrorPulseTransferSourceRetirementCheckpoint.AfterDirectoryWorkerDeletionBeforeReceipt;
        bool interruptBeforeWorkerDispatch = restartCut == MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletion;
        bool interruptBeforeWorkerStart = restartCut == MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletionStart;
        bool sourceStillRemote = interruptBeforeWorkerDispatch || interruptBeforeWorkerStart || interruptDuringPublication;
        bool noWorkerReceiptBeforeRestart = sourceStillRemote || interruptBeforeWorkerReceipt;
        bool noNativeStartsBeforeRestart = interruptAfterWorkerDelete || noWorkerReceiptBeforeRestart;
        string? package = Environment.GetEnvironmentVariable("MP_SIGNED_LOCAL_PACKAGE");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" || string.IsNullOrWhiteSpace(package))
            Assert.Inconclusive("Requires the disposable native Windows environment and the frozen signed Local package.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        CancellationToken token = deadline.Token;
        using var caller = WindowsIdentity.GetCurrent();
        var owner = caller.User ?? throw new InvalidOperationException("The native fixture has no Windows owner.");
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string sourceBackend = Path.Combine(directory, "source");
        string targetBackend = Path.Combine(directory, "target");
        const string original = "tree/deep/payload.bin";
        const string transferred = "copied/deep/payload.bin";
        byte[] bytes = [17, 31, 43, 59, 71, 83];
        await using var webDav = webDavPackage is null ? null : new SignedWebDavV2HttpFixture("directory-transfer", null);
        webDav?.Files.Clear();
        Directory.CreateDirectory(Path.Combine(sourceBackend, "tree", "deep"));
        Directory.CreateDirectory(targetBackend);
        await File.WriteAllBytesAsync(Path.Combine(sourceBackend, original), bytes, token);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        bool registered = false;
        bool succeeded = false;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            registered = true;
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                Path.Combine(paths.DataRootPath, "installed"), rid, cancellationToken: token);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
            var targetInstallation = installed;
            if (webDavPackage is not null)
            {
                targetInstallation = await catalog.InstallSignedAdapterAsync(webDavPackage, webDavPackage + ".signature.json",
                    Path.Combine(paths.DataRootPath, "installed"), rid, cancellationToken: token);
                Assert.IsTrue(targetInstallation.IsSigned);
                Assert.AreEqual("com.mirrorpulse.adapter.webdav", targetInstallation.AdapterId.Value);
            }
            string files = Path.Combine(paths.DataRootPath, "files");
            await catalog.CreateInstanceAsync(installed.InstallId, "Docs",
                new Dictionary<string, string> { ["sourceDirectory"] = sourceBackend }, [], files,
                Path.Combine(paths.DataRootPath, "transfer-cache"),
                rootLabels: new Dictionary<string, string> { [installed.Manifest.RootDefinitions.Single().Key] = "Docs" }, cancellationToken: token);
            var targetConfiguration = webDav is null
                ? new Dictionary<string, string> { ["sourceDirectory"] = targetBackend }
                : new Dictionary<string, string> { ["endpoint"] = webDav.Endpoint.AbsoluteUri };
            await catalog.CreateInstanceAsync(targetInstallation.InstallId, "Archive", targetConfiguration, [], files,
                Path.Combine(paths.DataRootPath, "transfer-cache"),
                rootLabels: new Dictionary<string, string> { [targetInstallation.Manifest.RootDefinitions.Single().Key] = "Archive" },
                cancellationToken: token);
            var topology = await catalog.ReadAdapterTopologyAsync(token);
            var sourceRoot = topology.Roots.Single(root => root.Label == "Docs");
            var targetRoot = topology.Roots.Single(root => root.Label == "Archive");
            Assert.AreNotEqual(sourceRoot.InstanceId, targetRoot.InstanceId);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            await using var worker = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await worker.StartAsync(topology);
            while (!worker.IsConnected(sourceRoot.InstanceId) || !worker.IsConnected(targetRoot.InstanceId))
                await Task.Delay(25, token);
            Assert.IsTrue(worker.SupportsAtomicDirectoryDeletion(sourceRoot.InstanceId));
            Assert.AreEqual(2, worker.GetConnectedProtocolVersion(targetRoot.InstanceId));

            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([sourceRoot.RootId, targetRoot.RootId]);
            var transport = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var provider = new MirrorPulseDemandProvider(router, transport, new MirrorPulseAdapterDirectoryPageSource(transport), work, catalog);
            var transfers = new MirrorPulseTransferWorkerServices(transport, transport, transport, transport,
                worker.GetConnectedProtocolVersion, transport);
            bool interrupt = true;
            bool interruptAcknowledgement = webDav is null && !restartDuringTransfer;
            CloudLocalChangeFeed? journal = null;
            const string interruption = "Fixture interruption around the first original native retirement.";
            var interruptedCheckpoint = restartCut ?? MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion;
            const string acknowledgementInterruption = "Fixture interruption after the original SDK acknowledgement.";
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider, null, transport, transport, router, catalog,
                worker.IsConnected, scheduler: scheduler, namespaceExecution: execution, namespaceActor: actor, namespaceWork: work,
                initializeNamespaceOnStart: true, transfers: transfers)
            {
                TransferJournalReady = feed => journal = feed,
                TransferPublicationCheckpoint = (checkpoint, _) =>
                {
                    if (interruptDuringPublication && checkpoint == MirrorPulseTransferPublicationCheckpoint.AfterRegistration)
                        throw new IOException(interruption);
                    return ValueTask.CompletedTask;
                },
                TransferSourceRetirementCheckpoint = (checkpoint, _) =>
                {
                    if (interrupt && checkpoint == interruptedCheckpoint)
                    {
                        interrupt = false;
                        throw new IOException(interruption);
                    }
                    return ValueTask.CompletedTask;
                },
                TransferAcknowledgementCheckpoint = (checkpoint, _) =>
                {
                    if (interruptAcknowledgement && checkpoint == MirrorPulseTransferAcknowledgementCheckpoint.AfterSdkAcknowledgement)
                    {
                        interruptAcknowledgement = false;
                        throw new IOException(acknowledgementInterruption);
                    }
                    return ValueTask.CompletedTask;
                }
            };
            await using var runtime = factory.Create(paths);
            await runtime.StartAsync(token);
            Assert.HasCount(2, runtime.NamespaceStartupResults);
            Assert.IsTrue(runtime.NamespaceStartupResults.All(root => root.Admitted && root.RecoveryCode is null));
            var rootIdentity = router.CreateRootPage().Children.Single(child => child.Name == "Docs").Identity;
            await provider.FetchChildrenAsync("Docs", rootIdentity.Encode(), null, token);
            foreach (string path in new[] { "tree", "tree/deep" })
            {
                string? revision = await worker.StatAsync(new(sourceRoot.InstanceId, path, sourceRoot.UniquenessKey), token);
                Assert.IsNotNull(revision);
                var identity = router.CreateFileIdentity(sourceRoot.InstanceId, sourceRoot.UniquenessKey, path, revision);
                await provider.FetchChildrenAsync("Docs/" + path, identity.Encode(), null, token);
            }
            var source = await runtime.InspectNamespaceMemberAsync("Docs/tree", true, token);
            var target = await runtime.InspectNamespaceMemberAsync("Archive", true, token);
            IReadOnlyList<Guid> originalNotifications = [];
            if (webDav is null)
            {
                // Keep a real original content notification pending on a remote-backed file.
                // The command, not an independent upload pump, owns its settlement in this fixture.
                string localFile = Path.Combine(paths.SyncRootPath, "Docs", original);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(localFile, token));
                await File.WriteAllBytesAsync(localFile, bytes, token);
                var leaf = await runtime.InspectNamespaceMemberAsync("Docs/" + original, false, token);
                var originalIdentity = await catalog.ReadNamespaceMemberIdentityAsync(leaf.MemberId, token);
                Assert.IsNotNull(originalIdentity.ItemId);
                Assert.IsNotNull(journal);
                originalNotifications = await WaitForTransferOriginalContentJournalAsync(journal, originalIdentity.ItemId.Value, token);
            }
            Guid operationId = Guid.NewGuid();
            Assert.IsTrue(await runtime.PrepareDirectoryNamespaceTransferAsync(operationId, source.MemberId, target.MemberId, "copied", token));
            var plan = await catalog.ReadNamespaceDirectoryTransferAsync(operationId, token);
            Assert.IsNotNull(plan);
            Assert.IsTrue(originalNotifications.All(id => plan.Originals.Members.Any(change => change.OperationId == id)));
            var pending = (await catalog.ReadPendingNamespaceTransferStatusesAsync(sourceRoot.RootId, token)).Single();
            Assert.AreEqual(pending, (await catalog.ReadPendingNamespaceTransferStatusesAsync(targetRoot.RootId, token)).Single());
            Assert.IsTrue(pending.IsDirectory && pending.RootsFenced);

            var stopped = await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
            Assert.AreEqual(interruption, stopped.Message);
            var deletion = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, token);
            var originalTargets = new List<MirrorPulseDirectoryTransferTargetBirth>();
            if (interruptBeforeWorkerStart || interruptDuringPublication)
            {
                Assert.IsNull(deletion);
                int expectedBirths = interruptDuringPublication ? 1 : plan.Members.Count;
                for (int ordinal = 0; ordinal < expectedBirths; ordinal++)
                {
                    var birth = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, token);
                    Assert.IsNotNull(birth);
                    Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(birth.Birth.OperationId, token));
                    originalTargets.Add(birth);
                }
                if (interruptDuringPublication)
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, 1, token));
            }
            else
            {
                Assert.IsNotNull(deletion?.Request);
                Assert.AreEqual(!noWorkerReceiptBeforeRestart, deletion.Receipt is not null);
                Assert.AreEqual(!noWorkerReceiptBeforeRestart, deletion.AcceptedAt is not null);
            }
            Assert.AreEqual(sourceStillRemote, Directory.Exists(Path.Combine(sourceBackend, "tree")));
            if (sourceStillRemote)
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(sourceBackend, original), token));
            var originalWorkerReceipt = deletion?.Receipt;
            if (interruptBeforeWorkerReceipt)
            {
                // The Worker has already committed. Observe its exact durable
                // receipt independently of the interrupted Host catalog.
                originalWorkerReceipt = await ((IMirrorPulseDirectoryTransferDeletionTransport)worker)
                    .DeleteDirectoryTransferSourceAsync(deletion!.Request!, token);
            }
            byte[] targetBytes = webDav is null ? await File.ReadAllBytesAsync(Path.Combine(targetBackend, transferred), token) : webDav.Files[transferred];
            CollectionAssert.AreEqual(bytes, targetBytes);
            Assert.IsTrue((await catalog.ReadNamespaceTransferStatusAsync(operationId, token))!.RootsFenced);
            if (webDav is null)
            {
                DateTimeOffset? originalAcknowledgementStart = null;
                MirrorPulseDirectoryTransferRetirement? firstRetirement = null;
                if (restartDuringTransfer)
                {
                    var started = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, token);
                    Assert.HasCount(noNativeStartsBeforeRestart ? 0 : 1, started);
                    firstRetirement = started.SingleOrDefault();
                    Assert.IsNull(firstRetirement?.RetiredAt);
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, token));
                }
                else
                {
                    var ackCut = await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
                    Assert.AreEqual(acknowledgementInterruption, ackCut.Message);
                    var originalAck = await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, token);
                    Assert.IsNotNull(originalAck);
                    Assert.IsNull(originalAck.AcknowledgedAt);
                    originalAcknowledgementStart = originalAck.StartedAt;
                    var remaining = await ReadTransferOriginalJournalIdsAsync(journal!, token);
                    Assert.IsTrue(originalNotifications.All(id => !remaining.Contains(id)));
                }
                await runtime.DisposeAsync();
                await using var newExecution = new MirrorPulseNamespaceExecutionSession();
                Assert.AreNotEqual(execution.RoleSid, newExecution.RoleSid);
                await using var newActor = new MirrorPulseNamespaceActor();
                await using var newWork = new MirrorPulseNamespaceWorkAdmission([sourceRoot.RootId, targetRoot.RootId]);
                var newTransport = new MirrorPulseNormalUserWorkerTransport(newExecution, worker, worker, worker, worker, worker);
                var newProvider = new MirrorPulseDemandProvider(router, newTransport, new MirrorPulseAdapterDirectoryPageSource(newTransport), newWork, catalog);
                var newTransfers = new MirrorPulseTransferWorkerServices(newTransport, newTransport, newTransport, newTransport,
                    worker.GetConnectedProtocolVersion, newTransport);
                CloudLocalChangeFeed? reopenedJournal = null;
                Guid? lateDelete = null;
                var newFactory = new CfSharpMirrorPulseCloudRuntimeFactory(newProvider, newTransport, newTransport, newTransport, router, catalog,
                    worker.IsConnected, scheduler: scheduler, namespaceExecution: newExecution, namespaceActor: newActor, namespaceWork: newWork,
                    initializeNamespaceOnStart: true, transfers: newTransfers)
                {
                    TransferJournalReady = feed => reopenedJournal = feed,
                    TransferSourceRetirementCheckpoint = async (checkpoint, _) =>
                    {
                        if (interruptBeforeNativeDelete && firstRetirement is not null && lateDelete is null &&
                            checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion)
                            lateDelete = await WaitForRetiredTransferDeleteAsync(reopenedJournal!, firstRetirement.Projection.ItemId, token);
                    }
                };
                await using var newRuntime = newFactory.Create(paths);
                await newRuntime.StartAsync(token);
                Assert.HasCount(2, newRuntime.NamespaceStartupResults);
                if (restartDuringTransfer)
                {
                    Assert.IsTrue(newRuntime.NamespaceStartupResults.All(root => !root.Admitted && root.OperationId == operationId));
                    var started = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, token);
                    Assert.HasCount(noNativeStartsBeforeRestart ? 0 : 1, started);
                    if (firstRetirement is not null)
                    {
                        var settled = started.Single();
                        Assert.AreEqual(!interruptBeforeNativeDelete, settled.RetiredAt is not null);
                        Assert.AreEqual(firstRetirement.StartedAt, settled.StartedAt);
                        Assert.AreEqual(firstRetirement.Projection, settled.Projection);
                        Assert.AreEqual(firstRetirement.EffectId, settled.EffectId);
                        Assert.AreEqual(firstRetirement.MemberId, settled.MemberId);
                    }
                    Assert.AreEqual(interruptBeforeNativeDelete || noNativeStartsBeforeRestart,
                        File.Exists(Path.Combine(paths.SyncRootPath, "Docs", original)));
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, token));
                    Assert.IsTrue((await catalog.ReadNamespaceTransferStatusAsync(operationId, token))!.RootsFenced);
                    Assert.IsFalse(newWork.IsAdmitted(sourceRoot.RootId) || newWork.IsAdmitted(targetRoot.RootId));
                }
                else Assert.IsTrue(newRuntime.NamespaceStartupResults.All(root => root.Admitted && root.RecoveryCode is null));
                if (interruptBeforeNativeDelete)
                {
                    Assert.IsNotNull(reopenedJournal);
                    while ((await reopenedJournal.BeginScanAsync(token)).RequiresFullRescan)
                        await Task.Delay(25, token);
                }
                Assert.IsTrue(await newRuntime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
                var recoveredAck = await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, token);
                Assert.IsNotNull(recoveredAck?.AcknowledgedAt);
                if (originalAcknowledgementStart is { } ackStart) Assert.AreEqual(ackStart, recoveredAck.StartedAt);
                if (restartDuringTransfer)
                {
                    var audits = (await catalog.ReadNamespaceInitializationsAsync(cancellationToken: token))
                        .Where(status => status.Intent.DirectoryTransferOperationId == operationId).ToArray();
                    Assert.HasCount(2, audits);
                    CollectionAssert.AreEquivalent(new[] { sourceRoot.RootId, targetRoot.RootId },
                        audits.Select(status => status.Completion!.ScopeRoot!.Value).ToArray());
                    Assert.IsTrue(audits.All(status => status.Completion?.RoleSid == newExecution.RoleSid.Value));
                    if (interruptDuringPublication)
                    {
                        var targetAudit = audits.Single(status => status.Completion!.ScopeRoot == targetRoot.RootId).Completion!;
                        Assert.IsNull(originalTargets.Single().RecoveryInitializationId);
                        for (int ordinal = 1; ordinal < plan.Members.Count; ordinal++)
                        {
                            var birth = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, token);
                            Assert.IsNotNull(birth);
                            Assert.AreEqual(targetAudit.OperationId, birth.RecoveryInitializationId);
                            Assert.AreEqual(targetAudit.RoleSid, birth.Birth.RoleSid);
                            Assert.IsTrue(birth.Birth.PreparedAt >= targetAudit.CompletedAt);
                        }
                        var accepted = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, token);
                        Assert.IsNotNull(accepted);
                        Assert.IsTrue(accepted.StartedAt >= targetAudit.CompletedAt);
                    }
                    var retirements = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, token);
                    Assert.HasCount(3, retirements);
                    Assert.IsTrue(retirements.Where(row => interruptBeforeNativeDelete || row.Ordinal != firstRetirement?.Ordinal)
                        .All(row => row.ParentProtection?.Context.Protection.RoleSid == newExecution.RoleSid.Value));
                    if (interruptAfterWorkerDelete)
                    {
                        Assert.IsNotNull(retirements[0].Projection.OfficialLocalFileId);
                        Assert.IsNull(retirements[0].RecoveredOfficialLocalFileId);
                    }
                    if (firstRetirement is not null)
                    {
                        var finishedFirst = retirements.Single(row => row.Ordinal == firstRetirement.Ordinal);
                        Assert.AreEqual(firstRetirement.Projection, finishedFirst.Projection);
                        Assert.AreEqual(firstRetirement.StartedAt, finishedFirst.StartedAt);
                        if (interruptBeforeNativeDelete)
                        {
                            Assert.IsNull(firstRetirement.Projection.OfficialLocalFileId);
                            Assert.IsNotNull(finishedFirst.RecoveredOfficialLocalFileId);
                            Assert.IsNotNull(lateDelete);
                            DateTimeOffset acknowledgementDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
                            MirrorPulseDirectoryTransferLateAcknowledgement? late;
                            do
                            {
                                late = await catalog.ReadDirectoryTransferLateAcknowledgementAsync(lateDelete.Value, token);
                                if (late?.AcknowledgedAt is null) await Task.Delay(25, token);
                            } while (late?.AcknowledgedAt is null && DateTimeOffset.UtcNow < acknowledgementDeadline);
                            Assert.IsNotNull(late?.AcknowledgedAt, "The late original Delete must settle with the recovered SDK local file ID.");
                            Assert.AreEqual(operationId, late.TransferId);
                            Assert.DoesNotContain(lateDelete.Value, await ReadTransferOriginalJournalIdsAsync(reopenedJournal!, token));
                        }
                    }
                }
                Assert.IsTrue((await catalog.ReadNamespaceTransferStatusAsync(operationId, token))!.JournalAcknowledged);
                await AssertCompletedTransferAsync(newRuntime, newWork);
            }
            else
            {
                Assert.IsTrue(await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
                await AssertCompletedTransferAsync(runtime, work);
            }
            succeeded = true;

            async Task AssertCompletedTransferAsync(IMirrorPulseCloudRuntime current, MirrorPulseNamespaceWorkAdmission currentWork)
            {
                var replayed = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, token);
                Assert.IsNotNull(replayed?.Receipt);
                foreach (var birth in originalTargets)
                {
                    Assert.AreEqual(birth, await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, birth.Ordinal, token));
                    var identity = await catalog.ReadNamespaceMemberIdentityAsync(birth.Birth.OperationId, token);
                    Assert.AreEqual(birth.Plan.ItemId, identity.ItemId);
                    Assert.AreEqual(birth.Plan.RemoteId, identity.RemoteId);
                }
                if (deletion is not null)
                {
                    Assert.AreEqual(deletion.StartedAt, replayed.StartedAt);
                    Assert.AreEqual(deletion.Request!.RequestFingerprint, replayed.Receipt.RequestFingerprint);
                }
                if (originalWorkerReceipt is not null)
                {
                    Assert.AreEqual(originalWorkerReceipt.ReceiptId, replayed.Receipt.ReceiptId);
                    if (deletion!.AcceptedAt is not null) Assert.AreEqual(deletion.AcceptedAt, replayed.AcceptedAt);
                    CollectionAssert.AreEqual(originalWorkerReceipt.DeletedOriginals.ToArray(), replayed.Receipt.DeletedOriginals.ToArray());
                }
                Assert.HasCount(3, replayed.Receipt.DeletedOriginals);
                Assert.IsFalse(Directory.Exists(Path.Combine(sourceBackend, "tree")));
                var completed = await catalog.ReadNamespaceTransferStatusAsync(operationId, token);
                Assert.IsNotNull(completed);
                Assert.IsTrue(completed.Completed && completed.IsDirectory);
                Assert.IsFalse(completed.RootsFenced);
                Assert.IsTrue(currentWork.IsAdmitted(sourceRoot.RootId) && currentWork.IsAdmitted(targetRoot.RootId));
                Assert.IsEmpty(await catalog.ReadPendingNamespaceTransferStatusesAsync(targetRoot.RootId, token));
                Assert.HasCount(3, await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, token));
                var anchor = await current.InspectNamespaceMemberAsync(string.Empty, true, token);
                var auditedSource = await catalog.ReadNamespaceCurrentRootSnapshotAsync(anchor.MemberId, sourceRoot.RootId, token);
                Assert.HasCount(3, auditedSource.RetiredTransferMembers!);
                Assert.IsTrue(auditedSource.RetiredTransferMembers!.All(retirement => retirement.TransferId == operationId));
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberLocationAsync(source.MemberId, token));
                Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "tree")));
                var published = await current.InspectNamespaceMemberAsync("Archive/copied", true, token);
                Assert.AreEqual(completed.TargetMemberId, published.MemberId);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Archive", transferred), token));
                Assert.IsTrue(await current.ResumeDirectoryNamespaceTransferAsync(operationId, token));
                Assert.AreEqual(completed, await catalog.ReadNamespaceTransferStatusAsync(operationId, token));
                await using var packageSource = File.OpenRead(package);
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(packageSource, token));
                if (interruptDuringPublication)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; firstBirthRetained=True; twoBirthsUnderAuditedRole=True; sourceIntactBeforeRecovery=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (interruptBeforeWorkerStart)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; publishedBirthsRetained=3; sourceIntactBeforeWorkerStart=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (interruptBeforeWorkerDispatch)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; sourceIntactBeforeWorkerDispatch=True; originalWorkerRequestRetained=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (interruptBeforeWorkerReceipt)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; workerResultBeforeHostReceipt=True; exactWorkerReceiptReplayed=True; nativeStartsBeforeRestart=0; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (interruptAfterWorkerDelete)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; originalWorkerDeleteAccepted=True; nativeStartsBeforeRestart=0; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (interruptBeforeNativeDelete)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; liveOriginalSurvivedRestart=True; originalStartRetained=True; sdkLocalFileIdRecovered=True; lateDeleteAcknowledged=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (restartCut is not null)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; originalNativeRetirementSettled=True; originalStartRetained=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else if (webDav is null)
                {
                    TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; distinctSingleRootInstances=True; sourceMembers=3; atomicSourceReceipt=True; interruptedNativeRetirement=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.");
                }
                else
                {
                    Assert.IsFalse(worker.SupportsAtomicDirectoryDeletion(targetRoot.InstanceId));
                    var reverseParent = await current.InspectNamespaceMemberAsync("Docs", true, token);
                    Guid reverseId = Guid.NewGuid();
                    int before = webDav.MutationRequests;
                    var denied = await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
                        await current.PrepareDirectoryNamespaceTransferAsync(reverseId, published.MemberId, reverseParent.MemberId, "reverse", token));
                    StringAssert.Contains(denied.Message, "cannot safely retire a remote directory transfer");
                    Assert.IsNull(await catalog.ReadNamespaceTransferStatusAsync(reverseId, token));
                    Assert.AreEqual(before, webDav.MutationRequests);
                    Assert.IsFalse(Directory.Exists(Path.Combine(sourceBackend, "reverse")));
                    CollectionAssert.AreEqual(bytes, webDav.Files[transferred]);
                    Assert.IsTrue(currentWork.IsAdmitted(sourceRoot.RootId) && currentWork.IsAdmitted(targetRoot.RootId));
                    await using var webDavPackageSource = File.OpenRead(webDavPackage!);
                    string webDavDigest = Convert.ToHexString(await SHA256.HashDataAsync(webDavPackageSource, token));
                    TestContext.WriteLine($"SignedCrossPackageDirectoryTransfer: localSha256={digest}; webDavSha256={webDavDigest}; targetBytesVerified=True; reverseRejectedBeforeAdmission=True; reverseSourcePreserved=True.");
                }
            }
        }
        finally
        {
            try
            {
                if (registered) await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, owner,
                    ["Docs", "Docs/tree", "Docs/tree/deep", "Archive", "Archive/copied", "Archive/copied/deep"],
                    ["Docs/" + original, "Archive/" + transferred]);
            }
            catch (Exception error) when (!succeeded && error is IOException or UnauthorizedAccessException)
            {
                TestContext.WriteLine("The disposed fixture also reported a cleanup failure.");
            }
            finally { if (registered) registry.Unregister(paths.SyncRootPath); }
            try { DeleteFixture(directory); }
            catch (Exception error) when (!succeeded && error is IOException or UnauthorizedAccessException)
            {
                TestContext.WriteLine("The disposed fixture also reported a final cleanup failure.");
            }
        }
    }
}
