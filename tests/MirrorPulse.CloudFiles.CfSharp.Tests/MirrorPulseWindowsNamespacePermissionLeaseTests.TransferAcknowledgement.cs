using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using MirrorPulse.Tests.Fixtures;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static readonly byte[] TransferAcknowledgementLaterTarget = [42, 41, 40, 39, 38];

    private async Task<(MirrorPulseNamespaceTransferCopy Copy, IMirrorPulseCloudRuntime Runtime)> CompleteNativeTransferWithRecoveryAsync(
        IMirrorPulseCloudRuntime publishing, Func<MirrorPulseNamespaceWorkAdmission, IMirrorPulseCloudRuntime> createOwner, MirrorPulseProductCatalog catalog,
        MirrorPulseStoragePaths paths, MirrorPulseNamespaceTransferCopy prepared, Func<CloudLocalChangeFeed> journal,
        Func<MirrorPulseJournalUploadSource> source,
        MirrorPulseNamespaceWorkAdmission work, TransferNativeWorker worker, TransferNativeAcknowledgementFault fault, CancellationToken token)
    {
        Guid operation = prepared.Plan.OperationId;
        MirrorPulseNamespaceTransferAcknowledgement? firstStart = null;
        int originalReads = -1;
        IMirrorPulseCloudRuntime? replacement = null; bool transferred = false;
        async Task ReopenOwnerAsync()
        {
            // Runtime shutdown owns and disposes its admission. A new store owner
            // must start with fresh closed roots, never the previous Host's object.
            await publishing.DisposeAsync();
            work = new([prepared.Plan.SourceRoute.RootId, prepared.Plan.TargetRoute.RootId]);
            replacement = createOwner(work); publishing = replacement;
            await publishing.StartAsync(token);
            Assert.IsFalse(work.IsAdmitted(prepared.Plan.SourceRoute.RootId));
            Assert.IsFalse(work.IsAdmitted(prepared.Plan.TargetRoute.RootId));
        }
        try
        {
            for (int cut = 0; cut < 3; cut++)
            {
                TestContext.WriteLine($"HostTransferOriginalAckCut: cut={cut}; currentAdmissionOwner=True.");
                try
                {
                    if (cut < 2)
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await publishing.CompleteNamespaceTransferAsync(operation, token));
                    else
                        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await publishing.CompleteNamespaceTransferAsync(operation, token));
                }
                finally
                {
                    if (fault.ReceiptTriggerArmed)
                    {
                        await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER fail_transfer_original_ack;", CancellationToken.None);
                        fault.ReceiptTriggerArmed = false;
                    }
                }
                var interrupted = await catalog.ReadNamespaceTransferAsync(operation, token);
                Assert.IsNotNull(interrupted?.Acknowledgement); Assert.IsNull(interrupted.Acknowledgement.AcknowledgedAt);
                firstStart ??= interrupted.Acknowledgement;
                Assert.AreEqual(firstStart, interrupted.Acknowledgement);
                Assert.AreEqual(MirrorPulseTransferAcknowledgementKind.OriginalJournal, firstStart.Kind);
                Assert.AreEqual(prepared.JournalClaim, interrupted.JournalClaim);
                Assert.IsGreaterThan(0, interrupted.JournalClaim!.Members.Count);
                Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                Assert.IsFalse(work.IsAdmitted(prepared.Plan.SourceRoute.RootId)); Assert.IsFalse(work.IsAdmitted(prepared.Plan.TargetRoute.RootId));
                var pending = await ReadTransferOriginalJournalIdsAsync(journal(), token);
                foreach (var original in interrupted.JournalClaim.Members)
                    Assert.AreEqual(cut == 0, pending.Contains(original.OperationId));
                if (cut == 0) originalReads = Volatile.Read(ref worker.RangeReads);
                else Assert.AreEqual(originalReads, Volatile.Read(ref worker.RangeReads), "An accepted ACK retry must not reread or recopy historical target content.");
                if (cut == 1)
                {
                    string laterTarget = Path.Combine(paths.SyncRootPath, "Archive", "moved.bin");
                    bool beforeWrite = File.GetAttributes(laterTarget).HasFlag(FileAttributes.ReparsePoint);
                    Assert.IsTrue(beforeWrite, "The published target must still have its native placeholder reparse point before the later write.");
                    // Observe the original write without another open, hydration or edit.
                    await File.WriteAllBytesAsync(laterTarget, TransferAcknowledgementLaterTarget, token);
                    bool afterCreateWrite = File.GetAttributes(laterTarget).HasFlag(FileAttributes.ReparsePoint);
                    TestContext.WriteLine($"HostTransferLaterWrite: beforeReparse={beforeWrite}; afterCreateWriteReparse={afterCreateWrite}; originalCreateWriteRetained=True.");
                }
                await ReopenOwnerAsync();
            }
            var completed = await publishing.CompleteNamespaceTransferAsync(operation, token);
            Assert.IsNotNull(completed.Acknowledgement?.AcknowledgedAt);
            Assert.AreEqual(firstStart, completed.Acknowledgement with { AcknowledgedAt = null });
            Assert.AreEqual(prepared.JournalClaim, completed.JournalClaim);
            Assert.AreEqual(2, fault.Interruptions); Assert.AreEqual(1, fault.ReceiptFailuresArmed);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
            Assert.IsTrue(work.IsAdmitted(prepared.Plan.SourceRoute.RootId)); Assert.IsTrue(work.IsAdmitted(prepared.Plan.TargetRoute.RootId));
            Assert.AreEqual(originalReads, Volatile.Read(ref worker.RangeReads));
            CollectionAssert.AreEqual(TransferAcknowledgementLaterTarget,
                await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Archive", "moved.bin"), token));
            var restoredTarget = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(completed.TargetPublication!.MemberId, token);
            Assert.IsNotNull(restoredTarget); Assert.AreEqual(completed.TargetPublication.ItemId, restoredTarget.Identity.ItemId);
            Assert.IsTrue(File.GetAttributes(Path.Combine(paths.SyncRootPath, "Archive", "moved.bin")).HasFlag(FileAttributes.ReparsePoint));
            var restoredActions = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(completed.TargetPublication.MemberId, token);
            Assert.IsGreaterThan(0, restoredActions.Count);
            var restoredAction = restoredActions[^1];
            Assert.IsNotNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(restoredAction.ActionId, token));
            Assert.IsNotNull(await catalog.ReadNamespaceInitializationCompletionAsync(restoredAction.InitializationOperationId, token));
            TestContext.WriteLine($"HostTransferCurrentRecovery: targetPlaceholderRestored=True; laterBytesRetained=True; originalHistoryRetained=True; durableBornAction=True; freshRootInitialization=True; historicalRangeReadsUnchanged=True.");
            Assert.AreEqual(completed, await publishing.CompleteNamespaceTransferAsync(operation, token));
            Guid lateDelete = await WaitForRetiredTransferDeleteAsync(journal(), prepared.Plan.Source.Identity.ItemId!.Value, token);
            await ExecuteTransferPublicationFixtureSqlAsync(paths,
                TransferAcknowledgementFaultSql.BeforeOriginalReceipt(lateDelete), token);
            try { await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await source().ReadPendingAsync(token)); }
            finally { await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER fail_late_transfer_ack;", CancellationToken.None); }
            var lateStart = await catalog.ReadRetiredTransferObservationAcknowledgementAsync(lateDelete, token);
            Assert.IsNotNull(lateStart); Assert.IsNull(lateStart.AcknowledgedAt);
            Assert.IsFalse((await ReadTransferOriginalJournalIdsAsync(journal(), token)).Contains(lateDelete));
            await ReopenOwnerAsync();
            Assert.AreEqual(completed, await publishing.CompleteNamespaceTransferAsync(operation, token));
            Assert.IsFalse(work.IsAdmitted(prepared.Plan.SourceRoute.RootId), "Completed history cannot reopen this new admission owner.");
            Guid recoveryOperation = Guid.NewGuid();
            Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(recoveryOperation, token));
            var sourceRecovery = await publishing.RecoverNamespaceRootAsync(recoveryOperation, prepared.Plan.SourceRoute.RootId, token);
            Assert.AreEqual(recoveryOperation, sourceRecovery.OperationId);
            Assert.IsTrue(work.IsAdmitted(prepared.Plan.SourceRoute.RootId));
            Assert.IsFalse(work.IsAdmitted(prepared.Plan.TargetRoute.RootId), "Recovering the source cannot admit its unrelated target root.");
            var batch = await source().ReadPendingAsync(token);
            var lateCompleted = await catalog.ReadRetiredTransferObservationAcknowledgementAsync(lateDelete, token);
            Assert.IsNotNull(lateCompleted?.AcknowledgedAt); Assert.AreEqual(lateStart, lateCompleted with { AcknowledgedAt = null });
            Assert.IsFalse(batch.ReadyCommands.Any(command => command.OperationId == lateDelete));
            Assert.IsNull(await catalog.ReadWorkerRequestAsync(lateDelete, token));
            Assert.AreEqual(1, worker.Uploads); Assert.AreEqual(1, worker.Deletes);
            CollectionAssert.AreEqual(TransferAcknowledgementLaterTarget,
                await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Archive", "moved.bin"), token));
            transferred = true; return (completed, publishing);
        }
        finally { if (!transferred && replacement is not null) await replacement.DisposeAsync(); }
    }

    private static async Task<Guid> WaitForRetiredTransferDeleteAsync(CloudLocalChangeFeed feed, Guid itemId, CancellationToken token)
    {
        while (true)
        {
            var scan = await feed.BeginScanAsync(token); Assert.IsFalse(scan.RequiresFullRescan); long after = 0;
            while (true)
            {
                var page = await feed.ReadPageAsync(scan, after, 64, token); Assert.IsFalse(page.RequiresFullRescan);
                var original = page.Changes.FirstOrDefault(change => change.ItemId == itemId && change.Kind == CloudLocalChangeKind.Delete);
                if (original is not null) return original.OperationId;
                if (!page.HasMore) break;
                Assert.IsGreaterThan(after, page.LastScannedSequence); after = page.LastScannedSequence;
            }
            await Task.Delay(10, token);
        }
    }

    private sealed class TransferNativeAcknowledgementFault(MirrorPulseStoragePaths paths)
    {
        public int Interruptions { get; private set; }
        public int ReceiptFailuresArmed { get; private set; }
        public bool ReceiptTriggerArmed { get; set; }

        public async ValueTask CheckpointAsync(MirrorPulseTransferAcknowledgementCheckpoint checkpoint, CancellationToken token)
        {
            if (Interruptions == 0 && checkpoint == MirrorPulseTransferAcknowledgementCheckpoint.BeforeSdkAcknowledgement ||
                Interruptions == 1 && checkpoint == MirrorPulseTransferAcknowledgementCheckpoint.AfterSdkAcknowledgement)
            {
                Interruptions++;
                throw new InvalidOperationException("Synthetic interruption at the original public SDK acknowledgement boundary.");
            }
            if (Interruptions == 2 && ReceiptFailuresArmed == 0 && checkpoint == MirrorPulseTransferAcknowledgementCheckpoint.BeforeSdkAcknowledgement)
            {
                await ExecuteTransferPublicationFixtureSqlAsync(paths,
                    "CREATE TRIGGER fail_transfer_original_ack BEFORE UPDATE ON namespace_transfers WHEN json_extract(CAST(NEW.payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NOT NULL BEGIN SELECT RAISE(ABORT,'original-ack-receipt-gap'); END;", token);
                ReceiptFailuresArmed++; ReceiptTriggerArmed = true;
            }
        }
    }
}
