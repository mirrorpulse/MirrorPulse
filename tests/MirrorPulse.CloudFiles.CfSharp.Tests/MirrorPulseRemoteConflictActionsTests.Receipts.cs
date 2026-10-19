using System.Text;
using CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteConflictActionsTests
{
    [TestMethod]
    [DataRow(MirrorPulseConflictAction.KeepLocal, null, false)]
    [DataRow(MirrorPulseConflictAction.KeepLocal, null, true)]
    [DataRow(MirrorPulseConflictAction.KeepRemote, null, false)]
    [DataRow(MirrorPulseConflictAction.KeepRemote, null, true)]
    [DataRow(MirrorPulseConflictAction.KeepBoth, MirrorPulseConflictPreservedSide.Local, false)]
    [DataRow(MirrorPulseConflictAction.KeepBoth, MirrorPulseConflictPreservedSide.Remote, false)]
    public async Task AnObservedCommandReceiptReplaysAfterCatalogReopenWithoutAnotherSdkDecision(
        MirrorPulseConflictAction action, MirrorPulseConflictPreservedSide? side, bool already)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        MirrorPulseRemoteConflictActionOutcome first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            await catalog.SaveRemoteConflictProjectionAsync(conflict);
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) => ValueTask.FromResult(already
                ? CloudRemoteApplyEntryStatus.AlreadyApplied : CloudRemoteApplyEntryStatus.Applied),
                new(fixture.Paths), catalog, center, (_, _) => ValueTask.FromResult(already
                    ? CloudRemoteConflictDismissalStatus.AlreadyDismissed : CloudRemoteConflictDismissalStatus.Dismissed));
            first = await actions.ApplyAsync(command, conflict, action, side, ReceiptFixture.OpenRemote);
            Assert.IsTrue(first.Resolved); Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            int sdkCalls = 0;
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
            { sdkCalls++; return ValueTask.FromException<CloudRemoteApplyEntryStatus>(new KeyNotFoundException("The SDK conflict is already gone.")); },
                new(fixture.Paths), catalog, center, (_, _) =>
                { sdkCalls++; return ValueTask.FromException<CloudRemoteConflictDismissalStatus>(new IOException("Do not dismiss twice.")); });
            var replay = await actions.ApplyAsync(command, conflict, action, side,
                _ => ValueTask.FromException<Stream>(new AssertFailedException("Do not reopen the remote source after its verified copy committed.")));
            Assert.AreEqual(first, replay); Assert.AreEqual(0, sdkCalls); Assert.HasCount(0, center.Query());
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
            Assert.AreEqual(conflict, (await catalog.ReadRemoteConflictProjectionsAsync()).Single());
            if (side is not null) Assert.AreEqual(side == MirrorPulseConflictPreservedSide.Local ? "local receipt bytes" : "remote receipt bytes",
                await File.ReadAllTextAsync(replay.PreservedPath!));
            Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
    }

    [TestMethod]
    [DataRow(MirrorPulseConflictPreservedSide.Local)]
    [DataRow(MirrorPulseConflictPreservedSide.Remote)]
    public async Task ACompletedReceiptCannotHideDamageToItsPreservedCopy(MirrorPulseConflictPreservedSide side)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        int sdkCalls = 0;
        var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
        { sdkCalls++; return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied); }, new(fixture.Paths), catalog, new());
        var first = await actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepBoth, side, ReceiptFixture.OpenRemote);
        await File.WriteAllTextAsync(first.PreservedPath!, "damaged preserved copy");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => actions.ApplyAsync(command, conflict,
            MirrorPulseConflictAction.KeepBoth, side, ReceiptFixture.OpenRemote));
        Assert.AreEqual(1, sdkCalls);
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("change")]
    [DataRow("path")]
    [DataRow("revision")]
    [DataRow("detected")]
    public async Task AnObservedCommandReceiptRejectsAnotherConflictEpochBeforeSdkExecution(string changed)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); int sdkCalls = 0;
        var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
        { sdkCalls++; return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied); }, new(fixture.Paths), catalog, new());
        _ = await actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote);
        var changedConflict = new MirrorPulseConflictRecord(conflict.ConflictId,
            changed == "instance" ? InstanceId.New() : conflict.InstanceId,
            changed == "change" ? "another-change" : conflict.ChangeId,
            changed == "path" ? "Docs/another.txt" : conflict.RelativePath,
            conflict.Reason, conflict.VersionComparison, conflict.LocalRevision,
            changed == "revision" ? "another-revision" : conflict.RemoteRevision,
            changed == "detected" ? conflict.DetectedAt.AddTicks(1) : conflict.DetectedAt, source: conflict.Source);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => actions.ApplyAsync(command, changedConflict, MirrorPulseConflictAction.KeepRemote));
        Assert.AreEqual(1, sdkCalls); Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ACompletedReceiptNeedsNoOriginalSdkOrRemoteSourceCapability(bool remoteCopy)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        var action = remoteCopy ? MirrorPulseConflictAction.KeepBoth : MirrorPulseConflictAction.KeepLocal;
        MirrorPulseConflictPreservedSide? side = remoteCopy ? MirrorPulseConflictPreservedSide.Remote : null;
        MirrorPulseRemoteConflictActionOutcome first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) => ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied),
                new(fixture.Paths), catalog, new(), (_, _) => ValueTask.FromResult(CloudRemoteConflictDismissalStatus.Dismissed));
            first = await actions.ApplyAsync(command, conflict, action, side, ReceiptFixture.OpenRemote);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            int sdkCalls = 0;
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
            { sdkCalls++; return ValueTask.FromException<CloudRemoteApplyEntryStatus>(new AssertFailedException("Do not execute another SDK decision.")); },
                new(fixture.Paths), catalog, new());
            var replay = await actions.ApplyAsync(command, conflict, action, side);
            Assert.AreEqual(first, replay); Assert.AreEqual(0, sdkCalls);
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
            if (remoteCopy) Assert.AreEqual("remote receipt bytes", await File.ReadAllTextAsync(replay.PreservedPath!));
        }
    }

    [TestMethod]
    [DataRow(MirrorPulseConflictAction.KeepLocal)]
    [DataRow(MirrorPulseConflictAction.KeepRemote)]
    [DataRow(MirrorPulseConflictAction.KeepBoth)]
    public async Task AnActuallyReturnedTerminalDecisionSurvivesCallerCancellation(MirrorPulseConflictAction action)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            bool returned = false;
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
            { returned = true; cancellation.Cancel(); return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied); },
                new(fixture.Paths), catalog, new(), (_, _) =>
                { returned = true; cancellation.Cancel(); return ValueTask.FromResult(CloudRemoteConflictDismissalStatus.Dismissed); });
            var result = await actions.ApplyAsync(command, conflict, action,
                action == MirrorPulseConflictAction.KeepBoth ? MirrorPulseConflictPreservedSide.Local : null,
                cancellationToken: cancellation.Token);
            Assert.IsTrue(returned); Assert.IsTrue(cancellation.IsCancellationRequested); Assert.IsTrue(result.Resolved);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var receipt = await catalog.ReadRemoteConflictActionReceiptAsync(command);
            Assert.IsNotNull(receipt); Assert.AreEqual(action, receipt.Intent.Action);
            Assert.AreEqual(action == MirrorPulseConflictAction.KeepLocal ? MirrorPulseRemoteConflictObservedStatus.Dismissed :
                MirrorPulseRemoteConflictObservedStatus.Applied, receipt.Status);
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
            Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
    }

    private sealed class ReceiptFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-command-receipt", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public ReceiptFixture()
        {
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            Directory.CreateDirectory(Path.Combine(Paths.SyncRootPath, "Docs"));
            File.WriteAllText(Path.Combine(Paths.SyncRootPath, "Docs", "note.txt"), "local receipt bytes");
        }
        public static ValueTask<Stream> OpenRemote(CancellationToken _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("remote receipt bytes")));
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
