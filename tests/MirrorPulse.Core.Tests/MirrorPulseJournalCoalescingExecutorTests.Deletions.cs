using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task DeleteRemoteOwnsTheCompleteRejectedGroupWithoutRewritingItsExecution(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        Guid command; string execution;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareDeletionAsync(catalog, fixture, remote);
            command = prepared.Intent.CommandId;
            execution = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            Assert.AreEqual(effect is MirrorPulseCoalescedEffect.MoveFile or MirrorPulseCoalescedEffect.MoveAndUpdateFile or MirrorPulseCoalescedEffect.DeleteFile
                ? fixture.Plan.OriginalPath : fixture.Plan.FinalPath, prepared.Intent.RemoteDeletion.RelativePath);
            Assert.AreEqual("concurrent-delete", prepared.Intent.RemoteDeletion.ExpectedRevision);
            CollectionAssert.AreEquivalent(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), prepared.Intent.OriginalOperationIds.ToArray());
            foreach (var original in prepared.Intent.OriginalOperationIds)
                Assert.AreEqual(command, await catalog.ReadConflictDeletionOwnerAsync(original));
            Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(prepared.Intent.RemoteDeletion.OperationId))!.State);
            Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0)).State);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
            Assert.IsEmpty(remote.Writes);
            Guid retry = Guid.NewGuid();
            await catalog.SaveUserCommandAsync(new(retry, "retry", prepared.Intent.ConflictId.ToString("D"), "pending"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeJournalCoalescingRetryAsync(retry, prepared.Intent.ConflictId, false));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await reopened.ReadConflictDeletionAsync(command))!;
        var completed = await DeletionExecutor(reopened, remote).ExecuteAsync(command,
            (deletion, token) => CompleteSyntheticLocalRetirementAsync(reopened, deletion, token), (intent, _) =>
            {
                CollectionAssert.AreEquivalent(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), intent.OriginalOperationIds.ToArray());
                Assert.IsFalse(remote.Exists(retained.Intent.RemoteDeletion.RelativePath));
                return ValueTask.CompletedTask;
            });
        Assert.AreEqual(MirrorPulseConflictDeletionPhase.Completed, completed.Phase);
        Assert.HasCount(1, remote.Writes); Assert.AreEqual("Delete", remote.Writes[0].Kind);
        Assert.AreEqual(retained.Intent.RemoteDeletion.OperationId, remote.Writes[0].Id);
        Assert.AreEqual(execution, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.IsEmpty(await reopened.ReadUploadConflictsAsync());
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
        await DeletionExecutor(reopened, remote).ExecuteAsync(command, (_, _) => throw new AssertFailedException("Completed local work must not repeat."),
            (_, _) => throw new AssertFailedException("Completed acknowledgement must not repeat."));
        Assert.HasCount(1, remote.Writes);
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("reply")]
    [DataRow("acknowledgement")]
    public async Task DeletionRecoversInterruptedBoundariesWithoutReuploadingLocalBytes(string boundary)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        Guid command;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareDeletionAsync(catalog, fixture, remote); command = prepared.Intent.CommandId;
            if (boundary == "reply") remote.LoseReplyAfter = "Delete";
            await Assert.ThrowsExactlyAsync<IOException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(command, async (deletion, token) =>
            {
                if (boundary == "local") throw new IOException("Synthetic local preservation failure.");
                await CompleteSyntheticLocalRetirementAsync(catalog, deletion, token);
            }, (_, _) => boundary == "acknowledgement" ? throw new IOException("Synthetic official acknowledgement failure.") : ValueTask.CompletedTask).AsTask());
            Assert.HasCount(boundary == "local" ? 0 : 1, remote.Writes);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        int localCalls = 0;
        await DeletionExecutor(reopened, remote).ExecuteAsync(command, async (deletion, token) =>
        { localCalls++; await CompleteSyntheticLocalRetirementAsync(reopened, deletion, token); }, (_, _) => ValueTask.CompletedTask);
        Assert.AreEqual(boundary == "local" ? 1 : 0, localCalls);
        Assert.HasCount(1, remote.Writes); Assert.AreEqual("Delete", remote.Writes[0].Kind);
        Assert.IsFalse(remote.Exists(fixture.Plan.FinalPath));
        Assert.IsEmpty(await reopened.ReadPendingConflictDeletionsAsync());
    }

    [TestMethod]
    public async Task ConcurrentRemoteRevisionNeverAuthorizesAnUnconditionalDeleteOrOriginalAcknowledgement()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareDeletionAsync(catalog, fixture, remote);
        remote.Seed(fixture.Plan.FinalPath, "later-remote", [8, 8]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(prepared.Intent.CommandId,
            (deletion, token) => CompleteSyntheticLocalRetirementAsync(catalog, deletion, token),
            (_, _) => throw new AssertFailedException("A rejected deletion must retain every original journal ID.")).AsTask());
        Assert.IsTrue(remote.Exists(fixture.Plan.FinalPath)); Assert.IsEmpty(remote.Writes);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(prepared.Intent.RemoteDeletion.OperationId))!.State);
        Assert.AreEqual(MirrorPulseConflictDeletionPhase.Retired, (await catalog.ReadConflictDeletionAsync(prepared.Intent.CommandId))!.Phase);
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("command")]
    [DataRow("membership")]
    public async Task CorruptDeletionEvidenceCannotReachLocalOrRemoteOperations(string corruption)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareDeletionAsync(catalog, fixture, remote);
        await ExecuteRetrySqlAsync(fixture, corruption switch
        {
            "fingerprint" => "UPDATE conflict_deletions SET fingerprint=zeroblob(32);",
            "command" => "UPDATE user_commands SET action='keeplocal';",
            _ => "DELETE FROM conflict_deletion_members;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(prepared.Intent.CommandId,
            (_, _) => throw new AssertFailedException("Corrupt evidence cannot authorize local retirement."),
            (_, _) => throw new AssertFailedException("Corrupt evidence cannot acknowledge original operations.")).AsTask());
        Assert.IsEmpty(remote.Writes);
    }

    private static async Task<MirrorPulseConflictDeletion> PrepareDeletionAsync(MirrorPulseProductCatalog catalog, Fixture fixture, FileRemote remote)
    {
        var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(execution.Steps[0].PreviousRelativePath ?? execution.Steps[0].RelativePath, "concurrent-delete", [6, 6]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Guid selected = (await SaveGroupConflictsAsync(catalog, fixture, "concurrent-delete"))[0];
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "deleteremote", selected.ToString("D"), "pending"));
        var content = new MirrorPulseCoalescingContent(FinalBytes.Length, Convert.ToHexString(SHA256.HashData(FinalBytes)),
            new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.None));
        return await catalog.PrepareConflictDeletionAsync(command, selected, fixture.Plan.RootKey, fixture.Plan.ItemId, fixture.Plan.FinalPath, content);
    }

    // This port exercises Core's durable orchestration only. Native preservation/ACL
    // authority is separately exercised through the production Host coordinator.
    private static async ValueTask CompleteSyntheticLocalRetirementAsync(MirrorPulseProductCatalog catalog, MirrorPulseConflictDeletion deletion, CancellationToken token)
    {
        while (deletion.Phase < MirrorPulseConflictDeletionPhase.Retired)
        {
            var next = (MirrorPulseConflictDeletionPhase)((int)deletion.Phase + 1);
            await catalog.AdvanceConflictDeletionAsync(deletion.Intent.CommandId, deletion.Phase, next, token);
            deletion = deletion with { Phase = next };
        }
    }

    private static MirrorPulseConflictDeletionExecutor DeletionExecutor(MirrorPulseProductCatalog catalog, FileRemote remote) => new(catalog, remote, new(remote));
}
