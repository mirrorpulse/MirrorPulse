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
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteFileUpdateRecoversItsSdkResultWithoutHydrating() => RunOriginalUpdateAsync(false, false, false, false, 0);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteDirectoryUpdateRecoversItsSdkResultBeforeCursor() => RunOriginalUpdateAsync(true, false, false, false, 0);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteFileUpdatePreservesDirtyBytesAndConflictAcrossOwners() => RunOriginalUpdateAsync(false, true, false, false, 0);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteFileUpdateInvalidatesHydratedBytesBeforeCursor() => RunOriginalUpdateAsync(false, false, true, false, 0);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteUpdateReconcilesItsSdkOutcomeBeforeNewRoleAudit() => RunOriginalUpdateAsync(false, false, false, true, 0);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteUpdateStartWithoutSdkProgressRemainsFenced() => RunOriginalUpdateAsync(false, false, false, false, 1);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteUpdateCommitGapRetainsUnknownNativeEffect() => RunOriginalUpdateAsync(false, false, false, false, 2);

    private async Task RunOriginalUpdateAsync(bool directoryItem, bool dirty, bool hydrate, bool rotateRole, int uncertainGap)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.original-update"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]); var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new OriginalUpdateContent();
        var identity = router.CreateFileIdentity(root.InstanceId, "docs", "original-update", "v1");
        var metadata = directoryItem ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        var kind = directoryItem ? CloudItemKind.Directory : CloudItemKind.File;
        var changeKind = directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert;
        var birth = new CloudRemoteChangeBatch(root.InstanceId + "/birth", ReadOnlyMemory<byte>.Empty,
            [new("birth", changeKind, identity.RemoteId, "v1", kind, "Docs\\target", identity.ItemId,
                length: directoryItem ? null : OriginalUpdateContent.Length, metadata: metadata, cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var update = new CloudRemoteChangeBatch(root.InstanceId + "/update", new byte[] { 1 },
            [new("update", changeKind, identity.RemoteId, "v2", kind, "Docs\\target", identity.ItemId,
                previousRemoteRevision: "v1", length: directoryItem ? null : OriginalUpdateContent.Length, metadata: metadata,
                cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var progressFault = new MirrorPulseRemoteProgressCommitFault(update);
        MirrorPulseNamespaceMemberRegistration firstMember = null!; MirrorPulseNamespaceRemoteUpdatePlan original = null!;
        int dirtySetupReads = 0;
        MirrorPulseNamespaceExecutionSession? sharedRole = rotateRole ? null : new(); string? firstRole = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, caller.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var ownedRole = rotateRole ? new MirrorPulseNamespaceExecutionSession() : null;
                var execution = ownedRole ?? sharedRole!;
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(owner == 0 && uncertainGap == 2 ? progressFault.Wrap(state) : state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                    updateCheckpoint: owner == 0 ? (point, _) => point == (uncertainGap == 1 ? MirrorPulseRemoteUpdateCheckpoint.AfterStart : MirrorPulseRemoteUpdateCheckpoint.AfterSdkApply)
                        ? ValueTask.FromException(new IOException("Synthetic interruption at the original SDK update boundary.")) : ValueTask.CompletedTask : null);
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                CloudItem item = directoryItem ? fileSystem.GetDirectory("Docs/target") : fileSystem.GetFile("Docs/target");
                if (owner == 0)
                {
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, birth, cancellationToken: deadline.Token)).Status);
                    var originalBirth = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, birth.BatchId, 0, deadline.Token))!;
                    firstMember = (await catalog.ReadNamespaceMemberAsync(originalBirth.OperationId, deadline.Token))!;
                    firstRole = execution.RoleSid.Value;
                    if (hydrate || dirty)
                    {
                        byte[] before = await File.ReadAllBytesAsync(item.FullPath, deadline.Token);
                        CollectionAssert.AreEqual(OriginalUpdateContent.Bytes("v1"), before);
                        Assert.IsGreaterThan(0, provider.Reads);
                    }
                    if (dirty)
                    {
                        // Edit the resident original through an existing-object handle.
                        // Create/truncate can revert a placeholder before the actual test.
                        await execution.RunNormalUserOperationAsync(async () =>
                        {
                            await using var writer = new FileStream(item.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read,
                                bufferSize: 4096, FileOptions.Asynchronous);
                            Assert.AreEqual((long)OriginalUpdateContent.Length, writer.Length);
                            await writer.WriteAsync(OriginalUpdateContent.Bytes("local"), deadline.Token);
                            await writer.FlushAsync(deadline.Token);
                        });
                        var edited = await item.InspectAsync(deadline.Token);
                        Assert.IsTrue(edited.IsPlaceholder); Assert.IsNotNull(edited.LocalBinding);
                        Assert.AreEqual(firstMember.LocalObject.VolumeSerialNumber, edited.LocalBinding.VolumeSerialNumber);
                        Assert.AreEqual(firstMember.LocalObject.SyncRootFileId, edited.LocalBinding.SyncRootFileId);
                        Assert.AreEqual(firstMember.LocalObject.LocalFileId, edited.LocalBinding.LocalFileId);
                        var editedIdentity = CloudPlaceholderIdentity.Decode(edited.PlaceholderIdentity.Span);
                        Assert.AreEqual(identity.ItemId, editedIdentity.ItemId); Assert.AreEqual(identity.RemoteId, editedIdentity.RemoteId);
                        Assert.AreEqual("v1", editedIdentity.RemoteRevision);
                        Assert.AreEqual(CloudSynchronizationState.NotInSync, edited.SynchronizationState);
                        CollectionAssert.AreEqual(OriginalUpdateContent.Bytes("local"), await File.ReadAllBytesAsync(item.FullPath, deadline.Token));
                        dirtySetupReads = provider.Reads;
                    }
                    await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(root.InstanceId, update, cancellationToken: deadline.Token).AsTask());
                    var pending = (await catalog.ReadNamespaceRemoteUpdateAsync(root.InstanceId, update.BatchId, 0, deadline.Token))!;
                    original = pending.Plan; Assert.AreEqual(firstMember.LocalObject, original.Child.Context.Member.LocalObject);
                    Assert.IsNotNull(pending.StartedAt); Assert.IsNull(pending.Outcome);
                    Assert.IsTrue(await new MirrorPulseRemoteNamespaceFence(router, catalog).CanReplayAsync(root, update, deadline.Token));
                    CollectionAssert.AreEqual(new byte[] { 1 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    if (uncertainGap == 2) Assert.AreEqual(1, progressFault.Failures);
                }
                else if (uncertainGap != 0)
                {
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(root.InstanceId, update, cancellationToken: deadline.Token).AsTask());
                    var pending = (await catalog.ReadNamespaceRemoteUpdateAsync(root.InstanceId, update.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(original, pending.Plan); Assert.IsNull(pending.Outcome);
                    Assert.IsFalse(await new MirrorPulseRemoteNamespaceFence(router, catalog).CanPollAsync(root, deadline.Token));
                    CollectionAssert.AreEqual(new byte[] { 1 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    string? revision = CloudPlaceholderIdentity.Decode((await item.InspectAsync(deadline.Token)).PlaceholderIdentity.Span).RemoteRevision;
                    Assert.AreEqual(uncertainGap == 1 ? "v1" : "v2", revision);
                }
                else
                {
                    if (rotateRole)
                    {
                        Assert.AreNotEqual(firstRole, execution.RoleSid.Value);
                        var pending = (await catalog.ReadPendingNamespaceRemoteUpdatesAsync(root.RootId, deadline.Token)).Single();
                        await new MirrorPulseRemoteUpdateCoordinator(fileSystem, state, catalog, router, execution, actor)
                            .SettleRecordedAsync(pending, null, deadline.Token);
                        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                        var startup = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions, work)
                            .StartAsync(deadline.Token);
                        await using var startupGuard = startup.Guard;
                        Assert.IsTrue(startup.Roots.Single().Admitted);
                    }
                    var result = await coordinator.ApplyAsync(root.InstanceId, update, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status); Assert.IsFalse(result.RequiresRetry);
                    var terminal = (await catalog.ReadNamespaceRemoteUpdateAsync(root.InstanceId, update.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(original, terminal.Plan); Assert.IsNotNull(terminal.Outcome);
                    Assert.AreEqual(dirty ? MirrorPulseNamespaceRemoteUpdateDisposition.Conflict : MirrorPulseNamespaceRemoteUpdateDisposition.Applied, terminal.Outcome.Disposition);
                    Assert.AreEqual(MirrorPulseNamespaceRemoteUpdateEvidenceSource.RetainedSdkProgress, terminal.Outcome.Source);
                    Assert.AreEqual(firstMember, await catalog.ReadNamespaceMemberAsync(firstMember.MemberId, deadline.Token));
                    Assert.AreEqual(firstMember.LocalObject.LocalFileId, (await item.InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                    Assert.AreEqual(dirty ? "v1" : "v2", CloudPlaceholderIdentity.Decode((await item.InspectAsync(deadline.Token)).PlaceholderIdentity.Span).RemoteRevision);
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    CollectionAssert.AreEqual(update.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    if (dirty)
                    {
                        CollectionAssert.AreEqual(OriginalUpdateContent.Bytes("local"), await File.ReadAllBytesAsync(item.FullPath, deadline.Token));
                        Assert.IsTrue((await fileSystem.ListRemoteConflictsAsync(deadline.Token)).Any(record => record.ConflictId == terminal.Outcome.ConflictId));
                    }
                    else if (hydrate)
                        CollectionAssert.AreEqual(OriginalUpdateContent.Bytes("v2"), await File.ReadAllBytesAsync(item.FullPath, deadline.Token));
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, update, cancellationToken: deadline.Token)).Status);
                    Assert.AreEqual(terminal, await catalog.ReadNamespaceRemoteUpdateAsync(root.InstanceId, update.BatchId, 0, deadline.Token));
                }
                if (dirty) Assert.AreEqual(dirtySetupReads, provider.Reads, "Remote conflict execution and recovery must not read or hydrate source content.");
                else if (!hydrate) Assert.AreEqual(0, provider.Reads);
            }
            if (dirty) TestContext.WriteLine($"OriginalRemoteDirtyUpdate: fixtureHydrationReads={dirtySetupReads}; updateSourceReads={provider.Reads - dirtySetupReads}; " +
                "samePlaceholderAndBinding=True; originalRevisionRetained=True; naturallyNotInSync=True; localBytesPreservedAcrossOwners=True.");
            TestContext.WriteLine($"OriginalRemoteUpdate: directory={directoryItem}; dirtyBytes={dirty}; hydratedRevisionReadback={hydrate}; " +
                $"newRoleReconciliation={rotateRole}; uncertainGap={uncertainGap}; originalBinding=True; sdkOwnsUpdate=True; " +
                $"originalEntryAndPolicy=True; outcomeBeforeCursor=True; wholeLifecyclePassed=False; sourceReads={provider.Reads}; " +
                $"v2Sha256={Convert.ToHexString(SHA256.HashData(OriginalUpdateContent.Bytes("v2")))}.");
        }
        finally
        {
            if (sharedRole is not null) await sharedRole.DisposeAsync();
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", "Docs/target"] : ["Docs"], directoryItem ? [] : ["Docs/target"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }

    private sealed class OriginalUpdateContent : ICloudFileContentProvider
    {
        internal const int Length = 65_537;
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal static byte[] Bytes(string revision) => Enumerable.Range(0, Length)
            .Select(index => (byte)((index * 17 + (revision == "v1" ? 3 : revision == "v2" ? 91 : 177)) % 251)).ToArray();
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _reads);
            string revision = CloudPlaceholderIdentity.Decode(request.FileIdentity).RemoteRevision!;
            if (revision is not ("v1" or "v2")) throw new InvalidDataException("The original update fixture requires the actual requested public revision.");
            return ValueTask.FromResult<Stream>(new MemoryStream(Bytes(revision).AsSpan(checked((int)request.Offset), checked((int)request.Length)).ToArray(), writable: false));
        }
    }
}
