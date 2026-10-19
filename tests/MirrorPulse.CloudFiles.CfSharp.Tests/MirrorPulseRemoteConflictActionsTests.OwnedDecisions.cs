using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteConflictActionsTests
{
    [TestMethod]
    [DataRow(CloudRemoteChangeKind.FileUpsert)]
    [DataRow(CloudRemoteChangeKind.DirectoryUpsert)]
    [DataRow(CloudRemoteChangeKind.Move)]
    public void AProtectedDeleteBoundaryCannotGrantItsRoleToAnotherNamespaceLifecycle(CloudRemoteChangeKind kind)
    {
        var expected = new MirrorPulseConflictRecord(Guid.NewGuid(), InstanceId.New(), "original-change", "Docs/target",
            MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Unknown, null, "v2", DateTimeOffset.UtcNow,
            source: MirrorPulseConflictSource.CfSharpRemote);
        bool directory = kind == CloudRemoteChangeKind.DirectoryUpsert;
        var change = new CloudRemoteChange(expected.ChangeId, kind, "remote-target", "v2", directory ? CloudItemKind.Directory : CloudItemKind.File,
            expected.RelativePath, Guid.NewGuid(), previousRelativePath: kind == CloudRemoteChangeKind.Move ? "Docs/source" : null,
            length: directory || kind == CloudRemoteChangeKind.Move ? null : 4,
            metadata: kind == CloudRemoteChangeKind.Move ? null : directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build());
        var conflict = new CloudRemoteConflict(change, null, CloudRemoteConflictReason.Content, expected.DetectedAt);
        var intent = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), expected, MirrorPulseConflictAction.KeepRemote, null,
            MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
        Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseRemoteDeleteResolutionCoordinator.RequireOriginalConflict(intent, expected.ConflictId, conflict));
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("change")]
    [DataRow("path")]
    [DataRow("revision")]
    [DataRow("detected")]
    [DataRow("reason")]
    public void AnOwnedDecisionRejectsAChangedPublicSdkConflictBeforeNamespaceExecution(string changed)
    {
        var expected = new MirrorPulseConflictRecord(Guid.NewGuid(), InstanceId.New(), "original-delete", "Docs/file.txt",
            MirrorPulseConflictReason.Delete, MirrorPulseVersionComparison.Unknown, null, "v2", DateTimeOffset.UtcNow,
            source: MirrorPulseConflictSource.CfSharpRemote);
        var intent = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), expected, MirrorPulseConflictAction.KeepRemote, null,
            MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
        CloudRemoteConflict Conflict(bool alter) =>
            new(new(alter && changed == "change" ? "another-delete" : expected.ChangeId, CloudRemoteChangeKind.Delete, "remote-file",
                alter && changed == "revision" ? "v3" : expected.RemoteRevision!, CloudItemKind.File,
                alter && changed == "path" ? "Docs/other.txt" : expected.RelativePath, Guid.NewGuid()), null,
                alter && changed == "reason" ? CloudRemoteConflictReason.Content : CloudRemoteConflictReason.Delete,
                alter && changed == "detected" ? expected.DetectedAt.AddTicks(1) : expected.DetectedAt);
        MirrorPulseRemoteDeleteResolutionCoordinator.RequireOriginalConflict(intent, expected.ConflictId, Conflict(false));
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteDeleteResolutionCoordinator.RequireOriginalConflict(intent,
            changed == "id" ? Guid.NewGuid() : expected.ConflictId, Conflict(true)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnOwnedDecisionReceivesItsRetainedCommandAndReplaysWithoutRepeatingExecution(bool already)
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        int ownedCalls = 0; MirrorPulseRemoteConflictActionOutcome first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
                ValueTask.FromException<CloudRemoteApplyEntryStatus>(new AssertFailedException("The admitted decision cannot use the raw SDK callback.")),
                new(fixture.Paths), catalog, new(), resolveOwned: async (intent, token) =>
                {
                    ownedCalls++; Assert.AreEqual(command, intent.CommandId); Assert.AreEqual(conflict, intent.Conflict);
                    Assert.AreEqual(MirrorPulseConflictAction.KeepRemote, intent.Action);
                    Assert.AreEqual(MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, intent.PackageVersion);
                    Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(intent.CommandId, token))!.State);
                    Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId, token));
                    return already ? CloudRemoteApplyEntryStatus.AlreadyApplied : CloudRemoteApplyEntryStatus.Applied;
                });
            first = await actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote);
            Assert.IsTrue(first.Resolved);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = new MirrorPulseRemoteConflictActions((_, _, _) =>
            ValueTask.FromException<CloudRemoteApplyEntryStatus>(new AssertFailedException("Do not execute after the original receipt.")),
            new(fixture.Paths), reopened, new(), resolveOwned: (_, _) =>
            { ownedCalls++; return ValueTask.FromException<CloudRemoteApplyEntryStatus>(new AssertFailedException("Do not repeat an owned completed decision.")); });
        Assert.AreEqual(first, await replay.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote));
        Assert.AreEqual(1, ownedCalls); Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task AnOwnedDecisionFailureRetainsTheCommandAndNeverFallsBackToRawSdk()
    {
        using var fixture = new ReceiptFixture(); var conflict = Create("Docs/note.txt"); Guid command = Guid.NewGuid();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var center = new MirrorPulseConflictCenter(); center.Upsert(conflict); await catalog.SaveRemoteConflictProjectionAsync(conflict);
        int rawCalls = 0;
        var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
        { rawCalls++; return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied); }, new(fixture.Paths), catalog, center,
            resolveOwned: (_, _) => ValueTask.FromException<CloudRemoteApplyEntryStatus>(new IOException("The protected original decision is still pending.")));
        await Assert.ThrowsExactlyAsync<IOException>(() => actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote));
        Assert.AreEqual(0, rawCalls); Assert.AreEqual("failed", (await catalog.ReadUserCommandAsync(command))!.State);
        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(command)); Assert.HasCount(1, center.Query());
        Assert.AreEqual(conflict, (await catalog.ReadRemoteConflictProjectionsAsync()).Single());
    }
}
