using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteAfterAcknowledgedMoveRetainsItsPrefixAndSettlesTheOriginalJournal() =>
        RunKeepRemoteResidentFixtureAsync("post-move", 65537);

    private static async Task<CloudFile> MoveKeepRemoteOriginalAsync(CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession role, MirrorPulseNamespaceActor actor, CloudFile file, CancellationToken token)
    {
        var binding = MirrorPulseContentConfirmation.CaptureUploadBinding(await file.InspectAsync(token));
        var member = await catalog.ReadNamespaceMemberByBindingAsync(binding.LocalObject, token);
        Assert.IsNotNull(member);
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token);
        Assert.IsNotNull(location.ParentMemberId);
        var moves = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
        Guid moveId = Guid.NewGuid();
        await moves.PrepareAsync(moveId, member.MemberId, location.ParentMemberId.Value, "original.bin", token);
        await moves.ExecuteAsync(moveId, token);
        Assert.AreEqual("Docs/original.bin", (await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token)).RelativePath);
        return fileSystem.GetFile("Docs/original.bin");
    }

    private static async Task VerifyPostMoveKeepRemoteAsync(MirrorPulseStoragePaths paths, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespaceWorkAdmission work,
        CloudFileSystem fileSystem, CloudLocalChangeFeed feed, KeepBothRemote remote, CloudFile file, Guid conflictId,
        MirrorPulseJournalCoalescingPlan plan, CloudItemSnapshot before, byte[] local, byte[] selected, CancellationToken token)
    {
        var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, token);
        Assert.IsNotNull(execution); Assert.HasCount(2, execution.Steps);
        var move = await catalog.ReadMutationAsync(execution.Steps[0].OperationId, token);
        Assert.IsNotNull(move); Assert.AreEqual(MirrorPulseMutationState.Acknowledged, move.State);
        Assert.AreEqual("accepted-move", move.AcceptedRevision);
        var rejected = await catalog.ReadMutationAsync(execution.Steps[1].OperationId, token);
        Assert.IsNotNull(rejected); Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State);
        Assert.AreEqual("accepted-move", rejected.Intent.ExpectedRevision);
        string moveHistory = JsonSerializer.Serialize(move), rejectedHistory = JsonSerializer.Serialize(rejected);
        var actions = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
            paths, router, remote, scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor,
            namespaceExecution: role, namespaceWork: work, stats: remote, remoteRanges: remote, remoteDirectories: remote);
        var decision = await actions.ApplyAsync(conflictId, MirrorPulse.Core.Conflicts.MirrorPulseConflictAction.KeepRemote, token);
        Guid originalCommand = decision.ResolutionId;
        while ((await catalog.ReadUserCommandAsync(originalCommand, token))!.State == "pending")
        {
            var scan = await feed.BeginScanAsync(token);
            if (scan.RequiresFullRescan)
            {
                var policy = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog,
                    remote, remote, _ => true, remote, remote, remote, scheduler: scheduler, namespaceWork: work);
                var recovery = new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync,
                    feed.AcknowledgeFullRescanAsync, (_, _) => ValueTask.CompletedTask);
                await recovery.RunAsync(new(true), token);
            }
            await Task.Delay(25, token);
            decision = await actions.ApplyAsync(conflictId, MirrorPulse.Core.Conflicts.MirrorPulseConflictAction.KeepRemote, token);
            Assert.AreEqual(originalCommand, decision.ResolutionId);
        }
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(decision.ResolutionId, token))!.State);
        var retained = await catalog.ReadConflictKeepRemoteJournalAsync(decision.ResolutionId, token);
        Assert.IsNotNull(retained);
        CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), retained.Originals.Select(row => row.OperationId).ToArray());
        Assert.AreEqual(moveHistory, JsonSerializer.Serialize(await catalog.ReadMutationAsync(move.Intent.OperationId, token)));
        Assert.AreEqual(rejectedHistory, JsonSerializer.Serialize(await catalog.ReadMutationAsync(rejected.Intent.OperationId, token)));
        CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(decision.PreservedPath!, token));
        CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(file.FullPath, token));
        var after = await file.InspectAsync(token);
        Assert.AreEqual(before.LocalBinding, after.LocalBinding);
        Assert.AreEqual("selected-remote", after.RemoteRevision);
        Assert.AreEqual(CloudSynchronizationState.InSync, after.SynchronizationState);
        Assert.HasCount(0, await catalog.ReadUploadConflictsAsync(cancellationToken: token));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
        await using (var transaction = await state.OpenStore.BeginTransactionAsync(token))
        {
            var pending = await transaction.Operations.ListByItemIdAsync(plan.ItemId, 64, token);
            Assert.IsFalse(pending.Any(row => plan.Members.Any(member => member.OperationId == row.OperationId)));
            await transaction.RollbackAsync(token);
        }
        remote.DenySourceReads = true;
        Assert.AreEqual(decision.ResolutionId, (await actions.ApplyAsync(conflictId,
            MirrorPulse.Core.Conflicts.MirrorPulseConflictAction.KeepRemote, token)).ResolutionId);
        Assert.AreEqual(0, remote.AcceptedUploads);
    }

    // The SDK journal and native namespace are real. This Worker double accepts
    // the move, then exposes a concurrent remote content revision to the upload.
    private sealed class KeepRemoteAcceptedMove(KeepBothRemote remote) : IMirrorPulseWorkerMutationTransport
    {
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual("before.bin", request.SourcePath); Assert.AreEqual("original.bin", request.DestinationPath);
            Assert.AreEqual("prior-remote", request.ExpectedRevision); Assert.IsFalse(request.IsDirectory);
            Assert.IsNotNull(request.OperationId); Assert.AreEqual(request.RootKey, request.DestinationRootKey);
            return ValueTask.FromResult("accepted-move");
        }

        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token) => remote.DeleteAsync(request, token);
    }
}
