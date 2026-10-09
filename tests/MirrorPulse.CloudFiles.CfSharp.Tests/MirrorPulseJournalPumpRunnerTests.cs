using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class MirrorPulseJournalPumpRunnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnconfirmedEditHoldsLaterMoveAndDeleteAcrossCatalogRestartButOtherPathsContinue(bool ambiguous)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-order-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        InstanceId instance = InstanceId.New();
        MirrorPulseWorkerChangeCommand Command(long sequence, string path, MirrorPulseWorkerChangeKind kind, string? previous = null) =>
            new(Guid.NewGuid(), sequence, instance, "docs", kind, path, previous is null ? null : "docs", previous, false, null, DateTimeOffset.UtcNow);
        MirrorPulseWorkerChangeCommand edit = Command(1, "first.txt", MirrorPulseWorkerChangeKind.ContentUpdate);
        MirrorPulseWorkerChangeCommand move = Command(2, "renamed.txt", MirrorPulseWorkerChangeKind.Move, "first.txt");
        MirrorPulseWorkerChangeCommand delete = Command(3, "renamed.txt", MirrorPulseWorkerChangeKind.Delete);
        MirrorPulseWorkerChangeCommand independent = Command(4, "independent.txt", MirrorPulseWorkerChangeKind.Create);
        var intent = new MirrorPulseMutationIntent(edit.OperationId, instance, "docs", edit.Kind, edit.RelativePath,
            null, false, "before", null, null, MirrorPulseMutationOrigin.Journal);
        var dispatched = new List<Guid>();
        int writes = 0;
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "first.txt"), "before");
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var executor = new MirrorPulseMutationExecutor(catalog);
                var runner = new MirrorPulseJournalPumpRunner();
                ValueTask<MirrorPulseJournalUploadBatch> Read(CancellationToken _) => ValueTask.FromResult(
                    new MirrorPulseJournalUploadBatch([delete, independent, move, edit], 0, false));
                async ValueTask<bool> Dispatch(MirrorPulseWorkerChangeCommand command, CancellationToken token)
                {
                    dispatched.Add(command.OperationId);
                    if (command == edit)
                        await executor.ExecuteAsync(intent, async executionToken =>
                        {
                            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "first.txt"), "accepted bytes", executionToken);
                            writes++;
                            if (ambiguous) throw new IOException("The reply was lost after writing.");
                            return "accepted";
                        }, (_, _) => throw new IOException("The acknowledgement was interrupted."), token);
                    else await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, command.RelativePath), "independent bytes", token);
                    return true;
                }
                Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
                CollectionAssert.AreEqual(new[] { edit.OperationId, independent.OperationId }, dispatched);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "first.txt")));
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "renamed.txt")));
            }
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseMutationRecord record = (await catalog.ReadMutationAsync(edit.OperationId))!;
                Assert.AreEqual(ambiguous ? MirrorPulseMutationState.Ambiguous : MirrorPulseMutationState.RemoteAccepted, record.State);
                await new MirrorPulseMutationExecutor(catalog).ReconcileAsync(record,
                    async (_, readToken) => new(await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "first.txt"), readToken) == "accepted bytes"
                        ? MirrorPulseMutationProofKind.Verified : MirrorPulseMutationProofKind.Unknown, "accepted"),
                    (_, _) => ValueTask.CompletedTask, CancellationToken.None);
                Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await catalog.ReadMutationAsync(edit.OperationId))!.State);
                Assert.AreEqual(1, writes);
                var runner = new MirrorPulseJournalPumpRunner();
                var remaining = new List<Guid>();
                await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch([delete, move], 0, false)),
                    (command, _) =>
                    {
                        remaining.Add(command.OperationId);
                        if (command.Kind == MirrorPulseWorkerChangeKind.Move)
                        {
                            File.Move(Path.Combine(paths.SyncRootPath, "first.txt"), Path.Combine(paths.SyncRootPath, "renamed.txt"));
                            Assert.AreEqual("accepted bytes", File.ReadAllText(Path.Combine(paths.SyncRootPath, "renamed.txt")));
                        }
                        else File.Delete(Path.Combine(paths.SyncRootPath, "renamed.txt"));
                        return ValueTask.FromResult(true);
                    }, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None);
                CollectionAssert.AreEqual(new[] { move.OperationId, delete.OperationId }, remaining);
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "first.txt")));
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "renamed.txt")));
            }
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public async Task DeferredParentHoldsChildrenAndParentDeleteWithoutHoldingSiblingDirectories()
    {
        var runner = new MirrorPulseJournalPumpRunner();
        InstanceId instance = InstanceId.New();
        MirrorPulseWorkerChangeCommand parent = new(Guid.NewGuid(), 1, instance, "root", MirrorPulseWorkerChangeKind.Create,
            "folder", null, null, true, null, DateTimeOffset.UtcNow);
        var child = parent with { OperationId = Guid.NewGuid(), Sequence = 2, RelativePath = "folder/child.txt", IsDirectory = false };
        var delete = parent with { OperationId = Guid.NewGuid(), Sequence = 3, Kind = MirrorPulseWorkerChangeKind.Delete };
        var sibling = child with { OperationId = Guid.NewGuid(), Sequence = 4, RelativePath = "folder-other/child.txt" };
        var calls = new List<Guid>();
        await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch([delete, sibling, child, parent], 0, false)),
            (command, _) => { calls.Add(command.OperationId); return ValueTask.FromResult(command != parent); },
            (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { parent.OperationId, sibling.OperationId }, calls);
    }

    [TestMethod]
    [DataRow("routing")]
    [DataRow("worker")]
    [DataRow("catalog")]
    [DataRow("acknowledgement")]
    public async Task FailedCommandDoesNotStopNextCommandOrDisappearFromHealth(string boundary)
    {
        var runner = new MirrorPulseJournalPumpRunner();
        MirrorPulseWorkerChangeCommand failed = Command();
        MirrorPulseWorkerChangeCommand valid = Command();
        var acknowledged = new HashSet<Guid>();
        string? code = null;
        bool inject = true;
        ValueTask<bool> Dispatch(MirrorPulseWorkerChangeCommand command, CancellationToken _)
        {
            if (inject && command.OperationId == failed.OperationId)
                throw boundary == "acknowledgement"
                    ? new MirrorPulseJournalAcknowledgementException("fixture ack rejected")
                    : new IOException("fixture boundary unavailable");
            acknowledged.Add(command.OperationId);
            return ValueTask.FromResult(true);
        }
        ValueTask Report(MirrorPulseWorkerChangeCommand? command, string error, Exception _, CancellationToken __)
        {
            Assert.AreEqual(failed.OperationId, command!.OperationId);
            code = error;
            if (boundary == "catalog") throw new IOException("fixture fault reporting unavailable");
            return ValueTask.CompletedTask;
        }
        ValueTask<MirrorPulseJournalUploadBatch> Read(CancellationToken _) => ValueTask.FromResult(
            new MirrorPulseJournalUploadBatch([failed, valid], 0, false));
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, Report, CancellationToken.None));
        Assert.Contains(valid.OperationId, acknowledged);
        Assert.DoesNotContain(failed.OperationId, acknowledged);
        Assert.IsFalse(runner.Health.Healthy);
        Assert.AreEqual(1, runner.Health.PendingFaults);
        Assert.AreEqual(boundary == "acknowledgement" ? "JournalAcknowledgementFailed" : "JournalCommandFailed", code);
        inject = false;
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, Report, CancellationToken.None));
        Assert.Contains(failed.OperationId, acknowledged);
        Assert.IsTrue(runner.Health.Healthy);
    }

    [TestMethod]
    public async Task ReadFailureIsVisibleAndNextCycleRecoversEvenWhenReportingFails()
    {
        var runner = new MirrorPulseJournalPumpRunner();
        await runner.RunCycleAsync(_ => throw new IOException("fixture source unavailable"),
            (_, _) => ValueTask.FromResult(true), (_, _, _, _) => throw new IOException("fixture catalog unavailable"), CancellationToken.None);
        Assert.AreEqual("JournalReadFailed", runner.Health.LastErrorCode);
        Assert.IsFalse(runner.Health.Healthy);
        await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch([], 0, false)),
            (_, _) => ValueTask.FromResult(true), (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.IsTrue(runner.Health.Healthy);
    }

    private static MirrorPulseWorkerChangeCommand Command() => new(Guid.NewGuid(), 1, InstanceId.New(), "root",
        MirrorPulseWorkerChangeKind.Create, "file.txt", null, null, false, null, DateTimeOffset.UtcNow);
}
