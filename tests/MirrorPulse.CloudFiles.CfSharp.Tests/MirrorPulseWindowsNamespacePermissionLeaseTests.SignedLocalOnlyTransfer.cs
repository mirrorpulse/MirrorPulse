using System.Runtime.InteropServices;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedDirectoryTransfer")]
    public async Task NativeSignedLocalTransfersNestedLocalOnlyFileAcrossTwoSingleRootInstances()
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
        const string localParent = "Docs/folder";
        const string localSource = "Docs/folder/original.bin";
        const string localTarget = "Archive/moved.bin";
        byte[] bytes = [11, 19, 47, 59, 83, 97];
        Directory.CreateDirectory(sourceBackend);
        Directory.CreateDirectory(targetBackend);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        bool registered = false;
        bool succeeded = false;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            registered = true;
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            await ExerciseAsync();
            succeeded = true;
        }
        finally
        {
            try
            {
                if (registered)
                    await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, owner,
                        ["Docs", localParent, "Archive"], [localSource, localTarget]);
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

        async Task ExerciseAsync()
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                Path.Combine(paths.DataRootPath, "installed"), rid, cancellationToken: token);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
            string rootKey = installed.Manifest.RootDefinitions.Single().Key;
            foreach (var (label, backend) in new[] { ("Docs", sourceBackend), ("Archive", targetBackend) })
                await catalog.CreateInstanceAsync(installed.InstallId, label,
                    new Dictionary<string, string> { ["sourceDirectory"] = backend }, [], Path.Combine(paths.DataRootPath, "files"),
                    Path.Combine(paths.DataRootPath, "transfer-cache"),
                    rootLabels: new Dictionary<string, string> { [rootKey] = label }, cancellationToken: token);
            var topology = await catalog.ReadAdapterTopologyAsync(token);
            var sourceRoot = topology.Roots.Single(root => root.Label == "Docs");
            var targetRoot = topology.Roots.Single(root => root.Label == "Archive");
            Assert.AreNotEqual(sourceRoot.InstanceId, targetRoot.InstanceId);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            await using var worker = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await worker.StartAsync(topology);
            while (!worker.IsConnected(sourceRoot.InstanceId) || !worker.IsConnected(targetRoot.InstanceId))
                await Task.Delay(25, token);
            Assert.AreEqual(2, worker.GetConnectedProtocolVersion(sourceRoot.InstanceId));
            Assert.AreEqual(2, worker.GetConnectedProtocolVersion(targetRoot.InstanceId));

            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([sourceRoot.RootId, targetRoot.RootId]);
            var transport = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var provider = new MirrorPulseDemandProvider(router, transport, new MirrorPulseAdapterDirectoryPageSource(transport), work, catalog);
            var transfers = new MirrorPulseTransferWorkerServices(transport, transport, transport, transport,
                worker.GetConnectedProtocolVersion, transport);
            CloudLocalChangeFeed? journal = null;
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider, null, transport, transport, router, catalog,
                worker.IsConnected, scheduler: scheduler, namespaceExecution: execution, namespaceActor: actor, namespaceWork: work,
                initializeNamespaceOnStart: true, transfers: transfers)
            {
                TransferJournalReady = feed => journal = feed
            };
            await using var runtime = factory.Create(paths);
            await runtime.StartAsync(token);
            Assert.HasCount(2, runtime.NamespaceStartupResults);
            Assert.IsTrue(runtime.NamespaceStartupResults.All(root => root.Admitted && root.RecoveryCode is null));
            var docs = await runtime.InspectNamespaceMemberAsync("Docs", true, token);
            var archive = await runtime.InspectNamespaceMemberAsync("Archive", true, token);
            var folder = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), docs.MemberId, "folder", true, token);
            var original = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), folder.MemberId, "original.bin", false, token);
            await File.WriteAllBytesAsync(Path.Combine(paths.SyncRootPath, localSource), bytes, token);
            var originalIdentity = await catalog.ReadNamespaceMemberIdentityAsync(original.MemberId, token);
            Assert.IsNotNull(originalIdentity.ItemId);
            Assert.IsNotNull(journal);
            var originalNotifications = await WaitForTransferOriginalContentJournalAsync(journal, originalIdentity.ItemId.Value, token);
            Assert.IsNotEmpty(originalNotifications);
            Assert.IsNull(await worker.StatAsync(new(sourceRoot.InstanceId, "folder", sourceRoot.UniquenessKey), token));

            Guid operation = Guid.NewGuid();
            var prepared = await runtime.PrepareNamespaceTransferAsync(operation, original.MemberId, archive.MemberId, "moved.bin", token);
            Assert.IsNull(prepared.Plan.SourceRemoteRevision);
            await runtime.CopyNamespaceTransferAsync(operation, token);
            await runtime.VerifyNamespaceTransferTargetAsync(operation, token);
            await runtime.PrepareNamespaceTransferTargetBirthAsync(operation, token);
            await runtime.PublishNamespaceTransferTargetAsync(operation, token);
            await runtime.RetireNamespaceTransferSourceAsync(operation, token);
            var completed = await runtime.CompleteNamespaceTransferAsync(operation, token);
            Assert.IsNotNull(completed.SourceAbsence);
            Assert.IsNull(completed.SourceDeletion);
            Assert.IsNotNull(completed.Acknowledgement?.AcknowledgedAt);
            Assert.IsTrue(originalNotifications.All(id => completed.JournalClaim!.Members.Any(change => change.OperationId == id)));
            var pending = await ReadTransferOriginalJournalIdsAsync(journal, token);
            Assert.IsFalse(originalNotifications.Any(pending.Contains));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
            Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, localSource)));
            Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, localParent)));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(sourceBackend).ToArray());
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(targetBackend, "moved.bin"), token));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, localTarget), token));
            TestContext.WriteLine($"SignedLocalOnlyFileTransfer: packageSha256={installed.PackageSha256}; distinctSingleRootInstances=True; originalNotificationsCaptured=True; originalLocalParentRetained=True; remoteSourceEmpty=True; targetBytesVerified=True; sourceAbsenceRetained=True; originalJournalAcknowledged=True.");
        }
    }
}
