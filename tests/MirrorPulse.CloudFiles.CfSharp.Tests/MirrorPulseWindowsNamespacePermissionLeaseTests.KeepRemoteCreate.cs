using System.Text.Json;
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
    public Task NativeKeepRemoteControlledCreatePreservesItsAbsentBaselineAndAllOriginalJournalEffects() =>
        RunKeepRemoteResidentFixtureAsync("create-conflict", 65537);

    private static async Task<CloudFile> CreateKeepRemoteOriginalAsync(CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession role, MirrorPulseNamespaceActor actor, CancellationToken token)
    {
        var parent = fileSystem.GetDirectory("Docs");
        var binding = (await parent.InspectAsync(token)).LocalBinding!;
        var member = await catalog.ReadNamespaceMemberByBindingAsync(new(binding.VolumeSerialNumber,
            binding.SyncRootFileId, binding.LocalFileId), token);
        Assert.IsNotNull(member);
        var created = await new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor)
            .CreateAsync(Guid.NewGuid(), member.MemberId, "created.bin", false, token);
        Assert.AreEqual("Docs/created.bin", (await catalog.ReadNamespaceMemberLocationAsync(created.MemberId, token)).RelativePath);
        return fileSystem.GetFile("Docs/created.bin");
    }

    private static async Task VerifyCreateKeepRemoteAsync(MirrorPulseStoragePaths paths, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespaceWorkAdmission work,
        CloudFileSystem fileSystem, CloudLocalChangeFeed feed, KeepBothRemote remote, CloudFile file, Guid conflictId,
        MirrorPulseJournalCoalescingPlan plan, CloudItemSnapshot before, byte[] local, byte[] selected, CancellationToken token)
    {
        var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, token);
        Assert.IsNotNull(execution); Assert.HasCount(1, execution.Steps); Assert.IsNull(execution.ExpectedRevision);
        var rejected = await catalog.ReadMutationAsync(execution.Steps[0].OperationId, token);
        Assert.IsNotNull(rejected); Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State);
        Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, rejected.Intent.Kind); Assert.IsNull(rejected.Intent.ExpectedRevision);
        string rejectedHistory = JsonSerializer.Serialize(rejected);
        var actions = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
            paths, router, remote, scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor, namespaceExecution: role,
            namespaceWork: work, stats: remote, remoteRanges: remote, remoteDirectories: remote);
        var decision = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token);
        // A public choice can legitimately return pending while the SDK requests
        // reconciliation. The normal pump must resume this same durable command.
        if ((await catalog.ReadUserCommandAsync(decision.ResolutionId, token))!.State == "pending")
        {
            await using var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, remote, remote, state,
                paths.SyncRootPath, paths.DataRootPath, _ => true,
                new(feed, state, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))), mutations: remote,
                ranges: remote, directories: remote, fileSystem: fileSystem, scheduler: scheduler,
                namespaceActor: actor, namespaceExecution: role, namespaceWork: work, conflictActions: actions);
            await pump.StartAsync(token);
            while ((await catalog.ReadUserCommandAsync(decision.ResolutionId, token))!.State == "pending")
                await Task.Delay(25, token);
            Assert.IsTrue(pump.Health.Healthy);
        }
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(decision.ResolutionId, token))!.State);
        Guid originalCommand = decision.ResolutionId;
        remote.DenySourceReads = true;
        decision = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token);
        Assert.AreEqual(originalCommand, decision.ResolutionId);
        var retained = await catalog.ReadConflictKeepRemoteAsync(decision.ResolutionId, token);
        Assert.IsNotNull(retained); Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, retained.Intent.RejectedEffect.Kind);
        var birth = await catalog.ReadConflictKeepRemoteOriginalBirthAsync(decision.ResolutionId, token);
        Assert.IsNotNull(birth); Assert.AreEqual(before.ItemId, birth.ItemId); Assert.AreEqual(before.RemoteId, birth.RemoteId);
        Assert.AreNotEqual(birth.RemoteId, retained.Intent.RemoteSource.RemoteId);
        Assert.IsNull(retained.Intent.RejectedEffect.ExpectedRevision);
        CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), retained.Intent.OriginalOperationIds.ToArray());
        CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(decision.PreservedPath!, token));
        CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(file.FullPath, token));
        var after = await file.InspectAsync(token);
        Assert.AreEqual(before.LocalBinding, after.LocalBinding); Assert.AreEqual(before.ItemId, after.ItemId);
        Assert.AreEqual(before.RemoteId, after.RemoteId); Assert.AreEqual("selected-remote", after.RemoteRevision);
        Assert.AreEqual(CloudSynchronizationState.InSync, after.SynchronizationState);
        Assert.AreEqual(rejectedHistory, JsonSerializer.Serialize(await catalog.ReadMutationAsync(rejected.Intent.OperationId, token)));
        var acknowledged = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(decision.ResolutionId, token);
        Assert.IsNotNull(acknowledged?.Result);
        await using (var read = await state.OpenStore.BeginTransactionAsync(token))
        {
            foreach (var member in plan.Members) Assert.IsNull(await read.Operations.GetAsync(member.OperationId, token));
            await read.RollbackAsync(token);
        }
        string history = JsonSerializer.Serialize(acknowledged);
        Assert.AreEqual(decision.ResolutionId, (await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token)).ResolutionId);
        Assert.AreEqual(history, JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteAcknowledgementAsync(decision.ResolutionId, token)));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token)); Assert.AreEqual(0, remote.AcceptedUploads);
    }
}
