using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossOwnersWithoutDispatch() =>
        RunDisabledControlledCreationAcrossOwnersAsync(freshRole: false);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch() =>
        RunDisabledControlledCreationAcrossOwnersAsync(freshRole: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeDisabledRootControlledMoveRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch() =>
        RunDisabledControlledCreationAcrossOwnersAsync(freshRole: true, controlledMove: true);

    private async Task RunDisabledControlledCreationAcrossOwnersAsync(bool freshRole, bool controlledMove = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.disabled-creation"), InstanceId.New(),
            new("existing", "Existing", "Existing", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await using var sharedExecution = freshRole ? null : new MirrorPulseNamespaceExecutionSession();
        string? originalRole = null;
        Guid fileOperation = Guid.NewGuid(), directoryOperation = Guid.NewGuid(), parentId = Guid.Empty;
        Guid fileMoveOperation = Guid.NewGuid(), directoryMoveOperation = Guid.NewGuid();
        MirrorPulseNamespaceMemberRegistration? originalFile = null, originalDirectory = null;
        string? birthHistory = null; Dictionary<Guid, string>? originals = null;
        string? originalMoves = null;
        byte[] latest = "disabled controlled creation retains complete latest local bytes"u8.ToArray();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token); SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var freshExecution = freshRole ? new MirrorPulseNamespaceExecutionSession() : null;
                var execution = freshExecution ?? sharedExecution!;
                if (owner == 0) originalRole = execution.RoleSid.Value;
                else if (freshRole) Assert.AreNotEqual(originalRole, execution.RoleSid.Value);
                else Assert.AreEqual(originalRole, execution.RoleSid.Value);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                DisabledCreationStoreObserver? state = null;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work),
                    uploads: source, stats: source, mutations: source, mayDispatch: _ => false,
                    router: router, catalog: catalog, namespaceExecution: execution, namespaceActor: actor,
                    scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true)
                { StateStoreDecorator = inner => state = new(inner) };
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                var parent = await runtime.InspectNamespaceMemberAsync("Existing", true, timeout.Token);
                if (owner == 0) parentId = parent.MemberId;
                else Assert.AreEqual(parentId, parent.MemberId);
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.IsFalse(work.TryEnter([root.RootId], out var ordinary)); Assert.IsNull(ordinary);
                var created = await runtime.CreateNamespaceMemberAsync(fileOperation, parentId, "note.txt", false, timeout.Token);
                var childDirectory = await runtime.CreateNamespaceMemberAsync(directoryOperation, parentId, "folder", true, timeout.Token);
                string file = Path.Combine(paths.SyncRootPath, "Existing", "note.txt");
                if (owner == 0)
                {
                    originalFile = created; originalDirectory = childDirectory;
                    var firstNative = await runtime.InspectNamespaceMemberAsync("Existing/note.txt", false, timeout.Token);
                    Assert.IsNotNull(firstNative.NativeItemId);
                    await using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(latest, timeout.Token); writer.SetLength(latest.Length); writer.Flush(true);
                    }
                    birthHistory = JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileOperation, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryOperation, timeout.Token) });
                }
                else
                {
                    Assert.AreEqual(originalFile, created); Assert.AreEqual(originalDirectory, childDirectory);
                    Assert.AreEqual(birthHistory, JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileOperation, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryOperation, timeout.Token) }));
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.CreateNamespaceMemberAsync(
                        fileOperation, parentId, "another-name.txt", false, timeout.Token).AsTask());
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.CreateNamespaceMemberAsync(
                        fileOperation, parentId, "note.txt", true, timeout.Token).AsTask());
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceImportAsync(
                        fileOperation, parentId, "note.txt", file, timeout.Token).AsTask());
                }
                if (controlledMove)
                {
                    Assert.IsNotNull(state);
                    Dictionary<Guid, string>? beforeMove = null;
                    if (owner == 0)
                    {
                        await using var read = await state.Store.BeginTransactionAsync(timeout.Token);
                        beforeMove = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                        await read.RollbackAsync(timeout.Token);
                    }
                    var fileIntent = await runtime.PrepareNamespaceMemberMoveAsync(fileMoveOperation, created.MemberId,
                        parentId, "renamed.txt", timeout.Token);
                    var fileCompletion = await runtime.ExecuteNamespaceMemberMoveAsync(fileMoveOperation, timeout.Token);
                    var directoryIntent = await runtime.PrepareNamespaceMemberMoveAsync(directoryMoveOperation, childDirectory.MemberId,
                        parentId, "renamed-folder", timeout.Token);
                    var directoryCompletion = await runtime.ExecuteNamespaceMemberMoveAsync(directoryMoveOperation, timeout.Token);
                    string moves = JsonSerializer.Serialize(new { fileIntent, fileCompletion, directoryIntent, directoryCompletion });
                    if (owner == 0) originalMoves = moves;
                    else Assert.AreEqual(originalMoves, moves);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceMemberMoveAsync(
                        fileMoveOperation, created.MemberId, parentId, "changed-request.txt", timeout.Token).AsTask());
                    Assert.IsFalse(File.Exists(file));
                    Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Existing", "folder")));
                    file = Path.Combine(paths.SyncRootPath, "Existing", "renamed.txt");
                    if (beforeMove is not null)
                    {
                        await using var read = await state.Store.BeginTransactionAsync(timeout.Token);
                        foreach (var original in beforeMove)
                        {
                            var retained = await read.Operations.GetAsync(original.Key, timeout.Token);
                            Assert.IsNotNull(retained); Assert.AreEqual(original.Value, DisabledCreationJournalFact(retained));
                        }
                        await read.RollbackAsync(timeout.Token);
                    }
                }
                var actual = await runtime.InspectNamespaceMemberAsync(controlledMove ? "Existing/renamed.txt" : "Existing/note.txt", false, timeout.Token);
                var actualDirectory = await runtime.InspectNamespaceMemberAsync(controlledMove ? "Existing/renamed-folder" : "Existing/folder", true, timeout.Token);
                Assert.AreEqual(created.MemberId, actual.MemberId); Assert.AreEqual(childDirectory.MemberId, actualDirectory.MemberId);
                Assert.IsNotNull(actual.NativeItemId); Assert.IsNotNull(actualDirectory.NativeItemId);
                foreach (Guid memberId in new[] { parentId, created.MemberId, childDirectory.MemberId })
                {
                    var protection = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, timeout.Token);
                    Assert.IsNotNull(protection);
                    Assert.AreEqual(execution.RoleSid.Value, protection.Context.Protection.RoleSid);
                }
                CollectionAssert.AreEqual(latest, await File.ReadAllBytesAsync(file, timeout.Token));
                Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "Existing", controlledMove ? "renamed-folder" : "folder")));
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.IsFalse(work.TryEnter([root.RootId], out ordinary)); Assert.IsNull(ordinary);
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                Assert.IsNotNull(state);
                await using (var read = await state.Store.BeginTransactionAsync(timeout.Token))
                {
                    var rows = await read.Operations.ListAsync(64, timeout.Token);
                    Assert.IsTrue(rows.Any(row => row.ItemId == actual.NativeItemId));
                    Assert.IsTrue(rows.Any(row => row.ItemId == actualDirectory.NativeItemId));
                    if (owner == 0) originals = rows.ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    else foreach (var original in originals!)
                    {
                        var retained = await read.Operations.GetAsync(original.Key, timeout.Token);
                        Assert.IsNotNull(retained); Assert.AreEqual(original.Value, DisabledCreationJournalFact(retained));
                    }
                    await read.RollbackAsync(timeout.Token);
                }
                Assert.IsEmpty(worker.Calls);
            }
            TestContext.WriteLine($"DisabledCreation: twoLibraryOwners=True; sameNamespaceRole={!freshRole}; actualFilesAndDirectories=True; " +
                "completeLatestBytes=True; originalBirthAndMembership=True; originalJournalPending=True; ordinaryAdmissionClosed=True; " +
                $"configuredUploadPump=True; sourceCalls=0; remoteAcceptance=False; freshRoleRecovery={freshRole}; controlledFileAndDirectoryMove={controlledMove}.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }

    private static string DisabledCreationJournalFact(CloudOperationJournalEntry operation) => JsonSerializer.Serialize(new
    {
        operation.OperationId,
        operation.Sequence,
        operation.ItemId,
        operation.Kind,
        operation.AttemptCount,
        operation.CreatedAt,
        PayloadSha256 = Convert.ToHexString(SHA256.HashData(operation.Payload.Span))
    });

    private sealed class DisabledCreationStoreObserver(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore Store { get; private set; } = null!;
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken token = default)
        {
            Store = await inner.OpenAsync(context, token);
            return Store;
        }
    }
}
