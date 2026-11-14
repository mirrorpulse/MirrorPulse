using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
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
    public Task NativeSignedLocalDirectoryTransferToWebDavRejectsUnsupportedReverseBeforeMutation()
    {
        string? webDavPackage = Environment.GetEnvironmentVariable("MP_SIGNED_WEBDAV_PACKAGE");
        if (string.IsNullOrWhiteSpace(webDavPackage)) Assert.Inconclusive("Requires the frozen signed WebDAV package.");
        return VerifySignedDirectoryTransferAsync(webDavPackage);
    }

    private async Task VerifySignedDirectoryTransferAsync(string? webDavPackage)
    {
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
            const string interruption = "Fixture interruption after the first original native retirement.";
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider, transport, transport, transport, router, catalog,
                worker.IsConnected, scheduler: scheduler, namespaceExecution: execution, namespaceActor: actor, namespaceWork: work,
                initializeNamespaceOnStart: true, transfers: transfers)
            {
                TransferSourceRetirementCheckpoint = (checkpoint, _) =>
                {
                    if (interrupt && checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion)
                    {
                        interrupt = false;
                        throw new IOException(interruption);
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
            Guid operationId = Guid.NewGuid();
            Assert.IsTrue(await runtime.PrepareDirectoryNamespaceTransferAsync(operationId, source.MemberId, target.MemberId, "copied", token));
            var pending = (await catalog.ReadPendingNamespaceTransferStatusesAsync(sourceRoot.RootId, token)).Single();
            Assert.AreEqual(pending, (await catalog.ReadPendingNamespaceTransferStatusesAsync(targetRoot.RootId, token)).Single());
            Assert.IsTrue(pending.IsDirectory && pending.RootsFenced);

            var stopped = await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
            Assert.AreEqual(interruption, stopped.Message);
            var deletion = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, token);
            Assert.IsNotNull(deletion?.Receipt);
            Assert.IsFalse(Directory.Exists(Path.Combine(sourceBackend, "tree")));
            byte[] targetBytes = webDav is null ? await File.ReadAllBytesAsync(Path.Combine(targetBackend, transferred), token) : webDav.Files[transferred];
            CollectionAssert.AreEqual(bytes, targetBytes);
            Assert.IsTrue((await catalog.ReadNamespaceTransferStatusAsync(operationId, token))!.RootsFenced);
            Assert.IsTrue(await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
            var replayed = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, token);
            Assert.IsNotNull(replayed?.Receipt);
            Assert.AreEqual(deletion.Receipt.ReceiptId, replayed.Receipt.ReceiptId);
            Assert.AreEqual(deletion.Receipt.RequestFingerprint, replayed.Receipt.RequestFingerprint);
            Assert.AreEqual(deletion.AcceptedAt, replayed.AcceptedAt);
            CollectionAssert.AreEqual(deletion.Receipt.DeletedOriginals.ToArray(), replayed.Receipt.DeletedOriginals.ToArray());
            var completed = await catalog.ReadNamespaceTransferStatusAsync(operationId, token);
            Assert.IsNotNull(completed);
            Assert.IsTrue(completed.Completed && completed.IsDirectory);
            Assert.IsFalse(completed.RootsFenced);
            Assert.IsTrue(work.IsAdmitted(sourceRoot.RootId) && work.IsAdmitted(targetRoot.RootId));
            Assert.IsEmpty(await catalog.ReadPendingNamespaceTransferStatusesAsync(targetRoot.RootId, token));
            Assert.HasCount(3, await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, token));
            var anchor = await runtime.InspectNamespaceMemberAsync(string.Empty, true, token);
            var auditedSource = await catalog.ReadNamespaceCurrentRootSnapshotAsync(anchor.MemberId, sourceRoot.RootId, token);
            Assert.HasCount(3, auditedSource.RetiredTransferMembers!);
            Assert.IsTrue(auditedSource.RetiredTransferMembers!.All(retirement => retirement.TransferId == operationId));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberLocationAsync(source.MemberId, token));
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "tree")));
            var published = await runtime.InspectNamespaceMemberAsync("Archive/copied", true, token);
            Assert.AreEqual(completed.TargetMemberId, published.MemberId);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Archive", transferred), token));
            Assert.IsTrue(await runtime.ResumeDirectoryNamespaceTransferAsync(operationId, token));
            Assert.AreEqual(completed, await catalog.ReadNamespaceTransferStatusAsync(operationId, token));
            await using var packageSource = File.OpenRead(package);
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(packageSource, token));
            if (webDav is null)
            {
                TestContext.WriteLine($"SignedDirectoryTransfer: packageSha256={digest}; distinctSingleRootInstances=True; sourceMembers=3; atomicSourceReceipt=True; interruptedNativeRetirement=True; reopenedRoots=True; targetBytesVerified=True.");
            }
            else
            {
                Assert.IsFalse(worker.SupportsAtomicDirectoryDeletion(targetRoot.InstanceId));
                var reverseParent = await runtime.InspectNamespaceMemberAsync("Docs", true, token);
                Guid reverseId = Guid.NewGuid();
                int before = webDav.MutationRequests;
                var denied = await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
                    await runtime.PrepareDirectoryNamespaceTransferAsync(reverseId, published.MemberId, reverseParent.MemberId, "reverse", token));
                StringAssert.Contains(denied.Message, "cannot safely retire a remote directory transfer");
                Assert.IsNull(await catalog.ReadNamespaceTransferStatusAsync(reverseId, token));
                Assert.AreEqual(before, webDav.MutationRequests);
                Assert.IsFalse(Directory.Exists(Path.Combine(sourceBackend, "reverse")));
                CollectionAssert.AreEqual(bytes, webDav.Files[transferred]);
                Assert.IsTrue(work.IsAdmitted(sourceRoot.RootId) && work.IsAdmitted(targetRoot.RootId));
                await using var webDavPackageSource = File.OpenRead(webDavPackage!);
                string webDavDigest = Convert.ToHexString(await SHA256.HashDataAsync(webDavPackageSource, token));
                TestContext.WriteLine($"SignedCrossPackageDirectoryTransfer: localSha256={digest}; webDavSha256={webDavDigest}; targetBytesVerified=True; reverseRejectedBeforeAdmission=True; reverseSourcePreserved=True.");
            }
            succeeded = true;
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
