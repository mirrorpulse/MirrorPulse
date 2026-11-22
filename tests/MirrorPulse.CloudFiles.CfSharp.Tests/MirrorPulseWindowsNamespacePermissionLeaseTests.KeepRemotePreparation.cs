using System.Diagnostics;
using System.Security.Cryptography;
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
    private static async Task VerifyKeepRemotePreparationRecoveryAsync(MirrorPulseStoragePaths paths,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, RootRegistration root,
        MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, KeepBothRemote remote, CloudFileSystem fileSystem,
        MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespaceWorkAdmission work,
        MirrorPulseNamespaceExecutionSession firstRole, Guid conflictId, MirrorPulseJournalCoalescingPlan plan, CloudItemSnapshot original,
        byte[] local, byte[] selected, CancellationToken token)
    {
        var backoff = new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var originalHashes = new Dictionary<Guid, string>();
        var auditMetadata = new HashSet<Guid>();
        await using (var read = await state.OpenStore.BeginTransactionAsync(token))
        {
            var rows = await read.Operations.ListByItemIdAsync(plan.ItemId, 32, token);
            foreach (var row in rows)
                originalHashes.Add(row.OperationId, Convert.ToHexString(SHA256.HashData(row.Payload.Span)));
            CollectionAssert.AreEquivalent(plan.Members.Select(member => member.OperationId).ToArray(), originalHashes.Keys.ToArray());
            await read.RollbackAsync(token);
        }
        await RequireOriginalJournalAsync("Before first audit");
        await using var firstPermissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var firstAudit = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, firstRole,
            actor, scheduler, firstPermissions, work).StartAsync(token);
        await using (firstAudit.Guard)
        {
            Assert.IsTrue(firstAudit.Roots.Single().Admitted);
            await RequireOriginalJournalAsync("After first audit");
            var firstOwner = new MirrorPulseUploadConflictActions(catalog, state, feed, backoff, paths, router, remote,
                scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor, namespaceExecution: firstRole,
                namespaceWork: work, stats: remote, remoteRanges: remote, remoteDirectories: remote)
            {
                KeepRemotePrepared = _ => ValueTask.FromException(new IOException("Synthetic interruption after KeepRemote acceptance."))
            };
            await Assert.ThrowsExactlyAsync<IOException>(() => firstOwner.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token).AsTask());
            await RequireOriginalJournalAsync("After explicit preparation");
        }

        Guid command = (await catalog.ReadPendingConflictKeepRemotePreparationsAsync(token: token)).Single();
        var preparation = await catalog.ReadConflictKeepRemoteAsync(command, token);
        Assert.IsNotNull(preparation);
        Assert.AreEqual(conflictId, preparation.Intent.ConflictId);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command, token))!.State);
        Assert.IsNull(preparation.LocalPreservation);
        Assert.IsNull(preparation.RemoteCopy);
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(command, token));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
        var file = fileSystem.GetFile(preparation.Intent.LocalRelativePath);
        CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(file.FullPath, token));
        await RequireOriginalJournalAsync("Before second audit");

        await using var nextWork = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await using var nextRole = new MirrorPulseNamespaceExecutionSession();
        int fileStarts = 0;
        var nextOwner = new MirrorPulseUploadConflictActions(catalog, state, feed, backoff, paths, router, remote,
            scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor, namespaceExecution: nextRole,
            namespaceWork: nextWork, stats: remote, remoteRanges: remote, remoteDirectories: remote)
        {
            KeepRemoteFileStarted = _ =>
            {
                Interlocked.Increment(ref fileStarts);
                return ValueTask.FromException(new IOException("Synthetic interruption after the first file start."));
            }
        };
        Assert.IsFalse(await nextOwner.RecoverKeepRemoteBeforeAuditAsync(root, token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => nextOwner.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token).AsTask());
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(command, token));

        await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var audit = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, nextRole,
            actor, scheduler, permissions, nextWork).StartAsync(token);
        await using var guard = audit.Guard;
        var audited = audit.Roots.Single();
        Assert.IsTrue(audited.Admitted, $"{audited.RecoveryCode} ({audited.NativeErrorCode})");
        await RequireOriginalJournalAsync("After second audit");
        await using (var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, remote, remote, state,
            paths.SyncRootPath, paths.DataRootPath, _ => true, new(feed, state, backoff), mutations: remote,
            ranges: remote, directories: remote, fileSystem: fileSystem, scheduler: scheduler, namespaceActor: actor,
            namespaceExecution: nextRole, namespaceWork: nextWork, conflictActions: nextOwner))
        {
            await pump.StartAsync(token);
            long waitStarted = Stopwatch.GetTimestamp();
            while ((await catalog.ReadUserCommandAsync(command, token))!.State != "resolved" &&
                Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromSeconds(15))
                await Task.Delay(100, token);
        }
        if ((await catalog.ReadUserCommandAsync(command, token))!.State != "resolved")
        {
            var pending = await catalog.ReadConflictKeepRemoteAsync(command, token);
            bool journal = await catalog.ReadConflictKeepRemoteJournalAsync(command, token) is not null;
            bool started = await catalog.ReadConflictKeepRemoteFileStartAsync(command, token) is not null;
            var content = await catalog.ReadConflictKeepRemoteFileContentAsync(command, token);
            var confirmation = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(command, token);
            var restore = await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(command, token);
            bool requiresRescan = (await feed.BeginScanAsync(token)).RequiresFullRescan;
            Assert.Fail($"KeepRemote remained pending. LocalCopy={pending?.LocalPreservation is not null}, RemoteCopy={pending?.RemoteCopy is not null}, Journal={journal}, FileStart={started}, WriteStart={content?.WriteStart is not null}, Written={content?.Completion is not null}, NativeAttempts={confirmation.Count}, RestoreAttempts={restore.Count}, RequiresRescan={requiresRescan}.");
        }

        Assert.AreEqual(1, fileStarts, "Resume the original start without recording another file start.");
        var acknowledgement = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(command, token);
        Assert.IsNotNull(acknowledgement?.Result);
        var unsupported = await catalog.ReadBlockedLocalOperationsAsync(token);
        await using (var read = await state.OpenStore.BeginTransactionAsync(token))
        {
            foreach (Guid metadataId in auditMetadata)
            {
                var recorded = unsupported.Single(operation => operation.OperationId == metadataId);
                Assert.AreEqual(MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, recorded.Reason);
                Assert.IsNull(await read.Operations.GetAsync(metadataId, token));
            }
            await read.RollbackAsync(token);
        }
        CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), preparation.Intent.OriginalOperationIds.ToArray());
        foreach (Guid originalId in preparation.Intent.OriginalOperationIds)
            Assert.AreEqual(command, await catalog.ReadConflictKeepRemoteOwnerAsync(originalId, token));
        Assert.IsEmpty(await catalog.ReadPendingConflictKeepRemotePreparationsAsync(token: token));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
        Assert.IsTrue(nextWork.IsAdmitted(root.RootId));
        var applied = await file.InspectAsync(token);
        Assert.AreEqual(original.LocalBinding, applied.LocalBinding);
        Assert.AreEqual("selected-remote", applied.RemoteRevision);
        Assert.AreEqual(CloudSynchronizationState.InSync, applied.SynchronizationState);
        CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(file.FullPath, token));
        var settled = await catalog.ReadConflictKeepRemoteAsync(command, token);
        var conflict = await catalog.ReadUploadConflictAsync(conflictId, token);
        Assert.IsNotNull(conflict);
        Assert.IsFalse(conflict.IsPending);
        var store = new MirrorPulseConflictCopyStore(paths);
        await using var preserved = await store.OpenLocalDecisionAsync(command, conflict, settled!.LocalPreservation!.Copy!, token);
        CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(preserved.Path, token));
        Assert.AreEqual(0, remote.AcceptedUploads);

        async Task RequireOriginalJournalAsync(string stage)
        {
            await using var read = await state.OpenStore.BeginTransactionAsync(token);
            var rows = await read.Operations.ListByItemIdAsync(plan.ItemId, 32, token);
            Assert.IsLessThan(32, rows.Count, "Read the complete bounded target-item journal.");
            foreach (var member in plan.Members)
            {
                var originalRow = rows.Single(row => row.OperationId == member.OperationId);
                Assert.AreEqual(member.Sequence, originalRow.Sequence);
                Assert.AreEqual(CloudStateOperationKind.ContentUpdate, originalRow.Kind);
                Assert.AreEqual(originalHashes[member.OperationId], Convert.ToHexString(SHA256.HashData(originalRow.Payload.Span)));
            }
            foreach (var later in rows.Where(row => !originalHashes.ContainsKey(row.OperationId)))
            {
                Assert.IsGreaterThan(plan.Members.Max(member => member.Sequence), later.Sequence);
                Assert.AreEqual(CloudStateOperationKind.MetadataUpdate, later.Kind, $"{stage}. A later content edit must remain a conflict.");
                auditMetadata.Add(later.OperationId);
            }
            await read.RollbackAsync(token);
        }
    }
}
