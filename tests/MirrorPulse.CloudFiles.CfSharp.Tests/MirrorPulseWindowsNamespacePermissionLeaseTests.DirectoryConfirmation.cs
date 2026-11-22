using System.Globalization;
using System.Security.Cryptography;
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
    [DataRow("none")]
    [DataRow("AfterAcceptanceSaved")]
    [DataRow("AfterNativeApplied")]
    [DataRow("BeforeConfirmationRecorded")]
    public async Task NativeDirectoryConfirmationRetainsOriginalBindingAndTypedIdentityAcrossOwners(string cut)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.directory-confirmation"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseNamespaceMemberRegistration created = null!;
        MirrorPulseMutationIntent intent = null!;
        MirrorPulseWorkerDirectoryEntry remote = null!;
        Guid firstItem = Guid.Empty; long originalSequence = 0; string originalPayload = string.Empty;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 8, 9]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1, 2, 3], deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    await feed.StartAsync(deadline.Token);
                    var docs = members.Values.Single(value => value.IsDirectory && value.RootId is not null);
                    created = await new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor)
                        .CreateAsync(Guid.NewGuid(), docs.MemberId, "folder", true, deadline.Token);
                    var before = await fileSystem.GetDirectory(created.RelativePath).InspectAsync(deadline.Token);
                    firstItem = before.ItemId!.Value;
                    CloudLocalChange? original = null;
                    while (original is null)
                    {
                        original = (await feed.ReadBatchAsync(deadline.Token)).Changes.FirstOrDefault(change =>
                            change.ItemId == firstItem && change.Kind == CloudLocalChangeKind.Create && change.IsDirectory);
                        if (original is null) await Task.Delay(25, deadline.Token);
                    }
                    intent = new(original.OperationId, root.InstanceId, root.UniquenessKey, MirrorPulseWorkerChangeKind.Create,
                        "folder", null, true, null, null, null, MirrorPulseMutationOrigin.Journal);
                    await using (var read = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        var row = await read.Operations.GetAsync(intent.OperationId, deadline.Token); Assert.IsNotNull(row);
                        originalSequence = row.Sequence; originalPayload = Convert.ToHexString(SHA256.HashData(row.Payload.Span));
                        await read.RollbackAsync(deadline.Token);
                    }
                    // Actual directory backend effect. This component test asserts no Worker RPC or Host journal ACK.
                    var backend = Directory.CreateDirectory(Path.Combine(directory, "backend", "folder"));
                    string revision = backend.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    remote = new("folder", revision, "Directory", "folder", null, backend.CreationTimeUtc, backend.LastWriteTimeUtc, false);
                    await catalog.PrepareMutationAsync(intent, deadline.Token);
                    await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing, cancellationToken: deadline.Token);
                    await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, revision, deadline.Token);
                }
                if (owner != 0) await feed.StartAsync(deadline.Token);
                var accepted = await catalog.ReadMutationAsync(intent.OperationId, deadline.Token); Assert.IsNotNull(accepted);
                bool injected = false;
                var confirmation = new MirrorPulseNamespaceDirectoryConfirmation(catalog, router, state, actor, role)
                {
                    Checkpoint = (phase, _) =>
                    {
                        if (owner == 0 && cut == phase.ToString()) { injected = true; throw new IOException("Owned directory confirmation interruption."); }
                        return ValueTask.CompletedTask;
                    }
                };
                if (owner == 0 && cut != "none")
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => confirmation.ConfirmAsync(fileSystem, accepted, remote,
                        remote.RemoteRevision, firstItem, deadline.Token).AsTask());
                    Assert.IsTrue(injected);
                    Assert.IsNull(await catalog.ReadDirectoryConfirmationAsync(intent.OperationId, deadline.Token));
                }
                else
                {
                    await confirmation.ConfirmAsync(fileSystem, accepted, remote, remote.RemoteRevision, firstItem, deadline.Token);
                    var receipt = await catalog.ReadDirectoryConfirmationAsync(intent.OperationId, deadline.Token); Assert.IsNotNull(receipt);
                    var identity = await catalog.ReadNamespaceMemberIdentityAsync(created.MemberId, deadline.Token);
                    var actual = await fileSystem.GetDirectory(created.RelativePath).InspectAsync(deadline.Token);
                    Assert.AreEqual(intent.OperationId, identity.AcceptanceOperationId);
                    Assert.AreEqual(firstItem, identity.OriginalItemId); Assert.AreNotEqual(firstItem, identity.ItemId);
                    Assert.AreEqual(identity.ItemId, actual.ItemId); Assert.AreEqual(identity.RemoteId, actual.RemoteId);
                    Assert.AreEqual(created.LocalObject.LocalFileId, actual.LocalBinding!.LocalFileId);
                    Assert.AreEqual(CloudSynchronizationState.InSync, actual.SynchronizationState);
                    Assert.AreEqual(created.MemberId, (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, role, actor)
                        .InspectAsync(created.RelativePath, true, deadline.Token)).MemberId);
                    await confirmation.ConfirmAsync(fileSystem, accepted, remote, remote.RemoteRevision, firstItem, deadline.Token);
                    Assert.AreEqual(receipt, await catalog.ReadDirectoryConfirmationAsync(intent.OperationId, deadline.Token));
                }
                Assert.AreEqual(created, await catalog.ReadNamespaceMemberAsync(created.MemberId, deadline.Token));
                Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(intent.OperationId, deadline.Token))!.State);
                Assert.IsNull(await catalog.ReadContentAcceptanceProofAsync(intent.OperationId, deadline.Token));
                await using var verify = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                var retained = await verify.Operations.GetAsync(intent.OperationId, deadline.Token); Assert.IsNotNull(retained);
                Assert.AreEqual(originalSequence, retained.Sequence); Assert.AreEqual(originalPayload, Convert.ToHexString(SHA256.HashData(retained.Payload.Span)));
                await verify.RollbackAsync(deadline.Token);
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("DirectoryConfirmation: actualBackendDirectory=True; actualNativeReadback=True; originalBinding=True; twoOwners=True; contentProof=False; originalSdkAck=False; WorkerRpc=False; fullHost=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
