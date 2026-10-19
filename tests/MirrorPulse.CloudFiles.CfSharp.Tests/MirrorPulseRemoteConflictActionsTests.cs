using System.Text;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class MirrorPulseRemoteConflictActionsTests
{
    [TestMethod]
    public async Task KeepsASelectedCopyOutsideSyncRootBeforeApplyingRemote()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
        await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "note.txt"), "local side");
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var center = new MirrorPulseConflictCenter();
            var copies = new MirrorPulseConflictCopyStore(paths);
            var local = Create("Docs/note.txt");
            center.Upsert(local);
            var decisions = new List<CloudRemoteConflictDecision>();
            var actions = new MirrorPulseRemoteConflictActions(
                (id, decision, _) =>
                {
                    Assert.AreEqual(local.ConflictId, id);
                    decisions.Add(decision);
                    return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied);
                }, copies, catalog, center);
            Guid commandId = Guid.NewGuid();
            MirrorPulseRemoteConflictActionOutcome result = await actions.ApplyAsync(
                commandId, local, MirrorPulseConflictAction.KeepBoth,
                MirrorPulseConflictPreservedSide.Local);
            Assert.IsTrue(result.Resolved);
            Assert.IsNotNull(result.PreservedPath);
            Assert.IsFalse(SourceDirectoryPathNormalizer.IsWithin(paths.SyncRootPath, result.PreservedPath));
            Assert.AreEqual("local side", await File.ReadAllTextAsync(result.PreservedPath));
            Assert.AreEqual(CloudRemoteConflictDecision.KeepRemote, decisions.Single());
            Assert.IsEmpty(center.Query());
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId))?.State);

            // A replay keeps the original copy even after the source has changed.
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "note.txt"), "new remote");
            MirrorPulseRemoteConflictActionOutcome replay = await actions.ApplyAsync(
                commandId, local, MirrorPulseConflictAction.KeepBoth,
                MirrorPulseConflictPreservedSide.Local);
            Assert.AreEqual(result.PreservedPath, replay.PreservedPath);
            Assert.AreEqual("local side", await File.ReadAllTextAsync(replay.PreservedPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteCopyDeferAndUnsupportedCommandsNeverPretendToResolve()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var center = new MirrorPulseConflictCenter();
            var copies = new MirrorPulseConflictCopyStore(paths);
            var conflict = Create("Docs/remote.txt");
            center.Upsert(conflict);
            var decisions = new List<CloudRemoteConflictDecision>();
            var actions = new MirrorPulseRemoteConflictActions(
                (_, decision, _) =>
                {
                    decisions.Add(decision);
                    return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Conflict);
                }, copies, catalog, center);

            MirrorPulseRemoteConflictActionOutcome deferred = await actions.ApplyAsync(
                Guid.NewGuid(), conflict, MirrorPulseConflictAction.Defer);
            Assert.IsFalse(deferred.Resolved);
            Assert.AreEqual(CloudRemoteConflictDecision.Defer, decisions.Single());
            Assert.HasCount(1, center.Query());

            Guid queuedId = Guid.NewGuid();
            MirrorPulseRemoteConflictActionOutcome queued = await actions.ApplyAsync(
                queuedId, conflict, MirrorPulseConflictAction.DeleteRemote);
            Assert.IsTrue(queued.CommandQueued);
            Assert.AreEqual(queuedId, queued.CommandId);
            await actions.ApplyAsync(queuedId, conflict, MirrorPulseConflictAction.DeleteRemote);
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(queuedId))?.State);
            Assert.HasCount(1, center.Query());
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => actions.ApplyAsync(
                Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepLocal));

            MirrorPulseRemoteConflictActionOutcome copy = await actions.ApplyAsync(
                Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepBoth,
                MirrorPulseConflictPreservedSide.Remote,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("remote side"))));
            Assert.IsFalse(copy.Resolved);
            Assert.AreEqual("failed", (await catalog.ReadUserCommandAsync(copy.CommandId))?.State);
            Assert.AreEqual("remote side", await File.ReadAllTextAsync(copy.PreservedPath!));
            Assert.HasCount(1, center.Query());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task KeepLocalUsesCfSharpPreviewTwoDismissalAndPersistsResolution()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var center = new MirrorPulseConflictCenter();
            var conflict = Create("Docs/keep-local.txt");
            center.Upsert(conflict);
            var actions = new MirrorPulseRemoteConflictActions(
                (_, _, _) => ValueTask.FromResult(CloudRemoteApplyEntryStatus.Conflict),
                new MirrorPulseConflictCopyStore(paths), catalog, center,
                (_, _) => ValueTask.FromResult(CloudRemoteConflictDismissalStatus.Dismissed));
            Guid commandId = Guid.NewGuid();

            MirrorPulseRemoteConflictActionOutcome result = await actions.ApplyAsync(
                commandId, conflict, MirrorPulseConflictAction.KeepLocal);

            Assert.IsTrue(result.Resolved);
            Assert.AreEqual(CloudRemoteConflictDismissalStatus.Dismissed, result.CfSharpDismissalStatus);
            Assert.IsEmpty(center.Query());
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId))?.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MirrorPulseConflictRecord Create(string path) => new(
        Guid.NewGuid(), InstanceId.New(), "remote-change", path,
        MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Unknown,
        null, "r2", DateTimeOffset.UtcNow,
        source: MirrorPulseConflictSource.CfSharpRemote);
}
