using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeResidentInSyncReplacementRetainsOriginalBytesJournalAndDirtyContentWithoutSourceCalls(bool acceptedRemoteIdentity)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.resident-replacement"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        byte[] oldBytes = "resident original already accepted before local replacement"u8.ToArray();
        byte[] newBytes = "explicit new local bytes are not remote acceptance"u8.ToArray();
        TestContext.WriteLine("ResidentFixture: " + Path.GetFileName(directory));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using var role = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
            var source = new ProtectedPermissionNoSourceProvider(); var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
            await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(timeout.Token));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
            var file = fileSystem.GetFile("Docs/original.bin"); await File.WriteAllBytesAsync(file.FullPath, oldBytes, timeout.Token);
            if (acceptedRemoteIdentity)
                _ = await file.ConvertToPlaceholderAsync(router.CreateFileIdentity(root.InstanceId, "docs", "remote:original", "accepted-v1"), cancellationToken: timeout.Token);
            var originals = new List<MirrorPulseNamespacePermissionPreparation>(); var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, user.User!, originals, timeout.Token);
            await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, timeout.Token);
            await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, timeout.Token);
            await using var feed = fileSystem.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
            var member = members.Values.Single(item => !item.IsDirectory);
            File.SetLastWriteTimeUtc(file.FullPath, new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc));
            var journal = new Dictionary<Guid, string>();
            for (int observation = 0; observation < 100; observation++)
            {
                await using (var read = await state.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    journal = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(item => item.OperationId, DisabledCreationJournalFact);
                    await read.RollbackAsync(timeout.Token);
                }
                if (journal.Count > 0) break;
                await Task.Delay(50, timeout.Token);
            }
            Assert.IsGreaterThan(0, journal.Count);
            await role.RunNamespaceOperationAsync(async () => await file.SetInSyncAsync(true, cancellationToken: timeout.Token));
            var before = await file.InspectAsync(timeout.Token);
            Assert.IsTrue(before.IsPlaceholder); Assert.AreEqual(CloudContentAvailability.FullyAvailable, before.ContentAvailability);
            Assert.AreEqual(CloudSynchronizationState.InSync, before.SynchronizationState);
            Assert.AreEqual(acceptedRemoteIdentity ? "accepted-v1" : null, before.RemoteRevision);
            string identity = Convert.ToBase64String(before.PlaceholderIdentity.Span);
            Assert.IsTrue(CloudPlaceholderIdentity.TryDecode(before.PlaceholderIdentity.Span, out var originalIdentity)); Assert.IsNotNull(originalIdentity);
            int unownedCallbacks = 0;
            await actor.RunAsync(async token =>
            {
                await role.RunNamespaceOperationAsync(async () =>
                {
                    foreach (var unrelated in new[] { new CloudPlaceholderIdentity(Guid.NewGuid(), originalIdentity.RemoteId, originalIdentity.RemoteRevision),
                        new(originalIdentity.ItemId, originalIdentity.RemoteId + "-unrelated", originalIdentity.RemoteRevision),
                        new(originalIdentity.ItemId, originalIdentity.RemoteId, "unrelated-revision") })
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceReplacementPermissionScope.RunAsync(file, router,
                            member.LocalObject, unrelated, (_, _) => { unownedCallbacks++; return ValueTask.CompletedTask; }, token));
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceReplacementPermissionScope.RunAsync(file, router,
                        member.LocalObject with { LocalFileId = Guid.NewGuid() }, originalIdentity,
                        (_, _) => { unownedCallbacks++; return ValueTask.CompletedTask; }, token));
                });
                return true;
            }, timeout.Token);
            Assert.AreEqual(0, unownedCallbacks);
            bool scopedIdentityMatched = false;
            await actor.RunAsync(async token =>
            {
                await role.RunNamespaceOperationAsync(async () =>
                {
                    var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(file, router, member.LocalObject,
                        async (lease, stop) =>
                        {
                            var known = await lease.InspectLocalIdentityAsync(stop);
                            scopedIdentityMatched = known.IsPlaceholder && known.PlaceholderIdentity is not null &&
                                known.PlaceholderIdentity.Encode().AsSpan().SequenceEqual(before.PlaceholderIdentity.Span);
                            var observed = await lease.InspectAsync(stop); await lease.ApplyDaclAsync(observed.Dacl, stop);
                        }, token);
                    TestContext.WriteLine($"ExistingAccessReceipt: outcome={receipt.Outcome}; stage={receipt.Stage}; callback={receipt.CallbackCompleted}; prepared={receipt.NativeIdentityPrepared}; converted={receipt.NativeConverted}; projection={receipt.DurableProjectionCommitted}; applied={receipt.AccessDescriptorApplied}; readback={receipt.AccessDescriptorReadBack}; drained={receipt.Drained}; identityMatched={scopedIdentityMatched}; error={receipt.Error?.HResult:X8}.");
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome); Assert.IsTrue(scopedIdentityMatched);
                });
                return true;
            }, timeout.Token);
            Guid operation = Guid.NewGuid(); string input = Path.Combine(directory, "input.bin");
            await File.WriteAllBytesAsync(input, newBytes, timeout.Token);
            var inputs = new MirrorPulseLocalImportStagingStore(paths); var backups = new MirrorPulseLocalReplacementBackupStore(paths);
            var preparation = new MirrorPulseNamespaceReplacementPreparationCoordinator(fileSystem, state, catalog, router, role, actor, scheduler, inputs, _ => false);
            _ = await preparation.PrepareAsync(operation, member.MemberId, input, timeout.Token); File.Delete(input);
            var executor = new MirrorPulseNamespaceReplacementExecutor(fileSystem, catalog, router, role, actor, scheduler, inputs, backups);
            var completion = await executor.ExecuteAsync(operation, timeout.Token);
            CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(file.FullPath, timeout.Token));
            var after = await file.InspectAsync(timeout.Token);
            Assert.AreEqual(identity, Convert.ToBase64String(after.PlaceholderIdentity.Span));
            Assert.AreEqual(before.RemoteRevision, after.RemoteRevision);
            Assert.AreEqual(before.LocalBinding, after.LocalBinding); Assert.AreEqual(CloudSynchronizationState.NotInSync, after.SynchronizationState);
            var retained = await catalog.ReadNamespaceReplacementExecutionAsync(operation, timeout.Token);
            Assert.IsNotNull(retained?.Backup); Assert.IsNotNull(retained.ContentCompletion); Assert.IsFalse(retained.ContentCompletion.IsInSync);
            Assert.IsNotNull(retained.Restoration); Assert.IsNotNull(retained.RestorationResult); Assert.IsTrue(retained.RestorationResult.ProductVerified);
            Assert.AreEqual(acceptedRemoteIdentity ? 2 : 1, retained.Restoration.Version);
            Assert.AreEqual(retained.Restoration.Version, retained.RestorationResult.Version);
            Assert.AreEqual(!acceptedRemoteIdentity, retained.RestorationResult.NativeIdentityPrepared);
            Assert.AreEqual(!acceptedRemoteIdentity, retained.RestorationResult.DurableProjectionCommitted);
            if (acceptedRemoteIdentity)
            {
                Assert.IsNotNull(retained.Restoration.ExistingPlaceholder);
                Assert.AreEqual(identity, retained.Restoration.ExistingPlaceholder.PlaceholderIdentity);
                Assert.AreEqual(member.LocalObject, retained.Restoration.ExistingPlaceholder.LocalObject);
                Assert.IsFalse(retained.RestorationResult.NativeConverted);
            }
            else Assert.IsNull(retained.Restoration.ExistingPlaceholder);
            await using (var backup = await backups.OpenLeaseAsync(operation, retained.Backup.Content, timeout.Token))
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(oldBytes)), Convert.ToHexString(await SHA256.HashDataAsync(backup.Data, timeout.Token)));
            await using (var read = await state.OpenStore.BeginTransactionAsync(timeout.Token))
            {
                foreach (var original in journal)
                {
                    var current = await read.Operations.GetAsync(original.Key, timeout.Token);
                    Assert.IsNotNull(current); Assert.AreEqual(original.Value, DisabledCreationJournalFact(current));
                }
                await read.RollbackAsync(timeout.Token);
            }
            Assert.AreEqual(completion, await executor.ExecuteAsync(operation, timeout.Token));
            Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, timeout.Token));
            Assert.AreEqual(0, source.Reads); Assert.IsNull(await catalog.ReadMutationAsync(operation, timeout.Token));
            TestContext.WriteLine("ResidentReplacement: initialNativeInSync=True; completeOldBackup=True; completeNewBytes=True; sameOriginalIdentity=True; changedContentDirty=True; originalJournalRetained=True; sourceReads=0; remoteAcceptance=False.");
        }
        catch (Exception error)
        {
            TestContext.WriteLine($"ResidentReplacementPrimary: type={error.GetType().Name}; hresult={error.HResult:X8}; message={error.Message}");
            throw;
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            RestoreImportContentFixtureForCleanup(paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
