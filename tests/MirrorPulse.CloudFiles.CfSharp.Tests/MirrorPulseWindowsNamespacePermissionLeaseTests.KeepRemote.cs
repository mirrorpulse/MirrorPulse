using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("seal")]
    [DataRow("writer")]
    [DataRow("unowned-seal")]
    public async Task NativeKeepRemoteFirstSealRequiresTheOriginalFileAndJournalAndReplaysWithoutWriting(string scenario)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.keep-remote"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using var caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var token = deadline.Token;
        await using var role = new MirrorPulseNamespaceExecutionSession();
        await using var actor = new MirrorPulseNamespaceActor();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        var provider = new ProtectedPermissionNoSourceProvider();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 14]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
            await role.RunNamespaceOperationAsync(() => fileSystem.StartAsync(token).AsTask());
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            var file = fileSystem.GetFile("Docs/original.bin");
            Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
            await File.WriteAllBytesAsync(file.FullPath, [1, 2, 3], token);
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, token);
            await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, token);
            await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, token);
            await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
            await feed.StartAsync(token);
            var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
            Guid itemId = (await file.InspectAsync(token)).ItemId!.Value;
            await File.AppendAllTextAsync(file.FullPath, " first local edit", token);
            await ReadEditedWindowAsync(1);
            await File.AppendAllTextAsync(file.FullPath, " latest local edit", token);
            var window = await ReadEditedWindowAsync(2);
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, itemId, window.WindowUpperSequence, new HashSet<Guid>());
            Assert.IsNotNull(plan);
            Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile, plan.Effect);
            Assert.HasCount(2, plan.Members);
            byte[] local = await File.ReadAllBytesAsync(file.FullPath, token);
            var snapshot = await file.InspectAsync(token);
            Assert.IsNotNull(snapshot.RemoteId);
            var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role)
                .CaptureUploadBindingAsync(file, snapshot, token);
            var content = new MirrorPulseCoalescingContent(local.Length, Convert.ToHexString(SHA256.HashData(local)), binding);
            await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, "prior-remote", content, token);
            var remote = new KeepBothRemote(Path.Combine(paths.DataRootPath, "keep-remote-selected.bin"), snapshot.RemoteId);
            remote.Seed();
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() =>
                new MirrorPulseJournalCoalescingExecutor(catalog, remote, remote, remote, new(remote)).ExecuteAsync(plan.PlanId,
                    (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(local, writable: false)), token).AsTask());
            Guid conflictId = Guid.Empty;
            foreach (var member in plan.Members)
            {
                var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), root.InstanceId, member.OperationId.ToString("D"), "Docs/original.bin",
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "prior-remote", "selected-remote", DateTimeOffset.UtcNow);
                await catalog.SaveUploadConflictAsync(conflict, token);
                conflictId = conflict.ConflictId;
            }
            Guid commandId = Guid.NewGuid();
            await catalog.SaveUserCommandAsync(new(commandId, "keepremote", conflictId.ToString("D"), "pending"), token);
            var source = new MirrorPulseConflictRemoteSource(1, root.InstanceId.ToString(), root.UniquenessKey, "original.bin",
                snapshot.RemoteId, "selected-remote", KeepBothRemote.SelectedBytes.Length);
            await catalog.PrepareConflictKeepRemoteAsync(commandId, conflictId, itemId, "Docs/original.bin", source, content, token);
            var copyStore = new MirrorPulseConflictCopyStore(paths);
            await using var copies = await new MirrorPulseConflictKeepRemotePreserver(catalog, copyStore, new(copyStore, remote, remote, remote))
                .RetainAsync(commandId, (_, _) => ValueTask.FromResult<Stream>(File.OpenRead(file.FullPath)), token);
            int sourceReads = remote.SourceReads;
            Assert.IsGreaterThan(0, sourceReads);
            remote.DenySourceReads = true;
            var retainedJournal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
            await retainedJournal.RetainAsync(copies, token);
            var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(members.Values.Single(member => !member.IsDirectory).MemberId, token);
            Assert.IsNotNull(original);
            var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Location.ParentMemberId!.Value, token);
            Assert.IsNotNull(parent);
            using var store = await state.OpenStoreFileLeaseAsync(token);
            await catalog.RetainConflictKeepRemoteFileStartAsync(commandId, store.Binding.VolumeSerialNumber, store.Binding.FileId,
                original, parent, DateTimeOffset.UtcNow, token);

            var seal = new MirrorPulseConflictKeepRemoteResidentSeal(fileSystem, catalog, state, feed, router, role, actor);
            await using var admission = await work.EnterPendingOperationAsync(root.RootId, token);
            if (scenario == "writer")
            {
                using var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                await Assert.ThrowsExactlyAsync<IOException>(() => ApplySealAsync().AsTask());
                Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
                return;
            }
            if (scenario == "unowned-seal")
            {
                await role.RunNamespaceOperationAsync(async () =>
                {
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(file, router, token);
                    await lease.ApplyDaclAsync(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role), token);
                });
                try
                {
                    await ApplySealAsync();
                    Assert.Fail("A descriptor without an original permission action must be refused.");
                }
                catch (Exception failure) when (failure is InvalidDataException or IOException)
                {
                    Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
                }
                return;
            }

            CloudProtectedLocalOperationResult first;
            try
            {
                first = await ApplySealAsync();
            }
            catch
            {
                await using var inspection = await state.OpenStore.BeginTransactionAsync(CancellationToken.None);
                var rows = await inspection.Operations.ListByItemIdAsync(itemId, 64, CancellationToken.None);
                foreach (var row in rows)
                    TestContext.WriteLine($"KeepRemoteSealJournal original={plan.Members.Any(member => member.OperationId == row.OperationId)} kind={row.Kind} sequence={row.Sequence} payloadLength={row.Payload.Length}.");
                await inspection.RollbackAsync(CancellationToken.None);
                throw;
            }
            Assert.IsTrue(first.CallbackCompleted && first.Drained && first.AccessDescriptorApplied && first.AccessDescriptorReadBack);
            var action = await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token);
            Assert.IsNotNull(action);
            Assert.AreEqual(original.Context.Member.LocalObject, action.LocalObject);
            Assert.AreEqual(original.ExpectedDacl, action.RestoreDacl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, token));
            var replay = await ApplySealAsync();
            Assert.IsTrue(replay.CallbackCompleted && replay.Drained);
            Assert.IsFalse(replay.AccessDescriptorApplied);
            Assert.AreEqual(action, await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
            await role.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(file.FullPath, token)));
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
            Assert.AreEqual(sourceReads, remote.SourceReads);
            Assert.AreEqual(0, provider.Reads);
            await retainedJournal.RevalidateRetainedOriginalsAsync(copies, token);
            await using (var pending = await state.OpenStore.BeginTransactionAsync(token))
            {
                foreach (var member in plan.Members)
                    Assert.IsNotNull(await pending.Operations.GetAsync(member.OperationId, token));
                await pending.RollbackAsync(token);
            }

            ValueTask<CloudProtectedLocalOperationResult> ApplySealAsync() => scheduler.RunAsync(root.InstanceId,
                stop => actor.RunAsync(inner => new ValueTask<CloudProtectedLocalOperationResult>(seal.ApplyFirstAsync(root.RootId, copies, inner)), stop), token);

            async Task<MirrorPulseJournalUploadBatch> ReadEditedWindowAsync(int expected)
            {
                while (true)
                {
                    var current = await journal.ReadPendingAsync(token);
                    Assert.IsFalse(current.RequiresFullRescan);
                    if (current.ReadyCommands.Count(command => command.ItemId == itemId) >= expected)
                        return current;
                    await Task.Delay(25, token);
                }
            }
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
