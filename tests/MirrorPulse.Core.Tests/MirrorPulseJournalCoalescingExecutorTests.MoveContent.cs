using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    public async Task MoveOnlyKeepLocalMovesThenUploadsAuthorizedBytesWithoutRewritingTheOriginalProgram()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile);
        var remote = new FileRemote(fixture);
        var content = DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)!;
        string original; Guid rejected; Guid command;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var execution = await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            original = JsonSerializer.Serialize(execution); rejected = execution.Steps[0].OperationId;
            var authorized = await AuthorizeVerificationDecisionAsync(catalog, fixture, content);
            command = authorized.CommandId;
            Assert.AreEqual(3, authorized.Version);
            Assert.AreEqual(content, authorized.AuthorizedContent);
            Assert.AreEqual(MirrorPulseWorkerChangeKind.Move, authorized.Intent.Kind);
            Assert.AreEqual("changed", authorized.Intent.ExpectedRevision);
            Assert.IsEmpty(remote.Writes);
            Assert.AreEqual(original, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, (proof, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual(content, proof);
            return ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes));
        });
        Assert.HasCount(2, completed);
        CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
        Assert.AreEqual("moved/changed", completed[1].Intent.ExpectedRevision);
        Assert.AreEqual(content.UploadBinding, completed[1].Intent.UploadBinding);
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        CollectionAssert.AreEqual(FinalBytes, remote.Read(fixture.Plan.FinalPath));
        Assert.IsFalse(remote.Exists(fixture.Plan.OriginalPath));
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await reopened.ReadMutationAsync(rejected))!.State);
        Assert.AreEqual(original, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(command))!.State);
        await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, (_, _) => throw new AssertFailedException("Completed move and content must not be sent twice."));
        Assert.HasCount(2, remote.Writes);
        await reopened.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    public async Task MoveOnlyKeepLocalRetainsTheUploadAcrossLostReplyAndCatalogRestart()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        Guid command; Guid upload;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            var decision = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged));
            command = decision.CommandId; upload = decision.FollowupOperationId!.Value;
            remote.LoseReplyAfter = "Upload";
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            Assert.AreEqual(MirrorPulseMutationState.Ambiguous, (await catalog.ReadMutationAsync(upload))!.State);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("A lost upload reply requires readback, not another write."));
        Assert.HasCount(2, completed); Assert.AreEqual(upload, completed[1].Intent.OperationId);
        CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(command))!.State);
        await reopened.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    public async Task MoveOnlyKeepLocalDoesNotUploadUntilTheAcceptedMoveProjectionCompletes()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        Guid move; Guid upload;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            var decision = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged));
            move = decision.Intent.OperationId; upload = decision.FollowupOperationId!.Value;
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent,
                (_, _) => throw new IOException("Synthetic interruption after remote move, before local projection.")).AsTask());
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(move))!.State);
            Assert.IsNull(await catalog.ReadMutationAsync(upload));
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 1));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId));
            Assert.HasCount(1, remote.Writes);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.AreEqual(move, completed[0].Intent.OperationId); Assert.AreEqual(upload, completed[1].Intent.OperationId);
        Assert.AreEqual("moved/changed", completed[0].AcceptedRevision);
        Assert.AreEqual("moved/changed", completed[1].Intent.ExpectedRevision);
        CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
    }

    [TestMethod]
    public async Task MoveOnlyKeepLocalPreservesItsContentSuffixAcrossAnotherRejectedMoveAndExplicitRetry()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var content = DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)!;
        var first = await AuthorizeVerificationDecisionAsync(catalog, fixture, content);
        remote.Seed(fixture.Plan.OriginalPath, "changed-again", [9, 9]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed-again");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[0].ToString("D"), "pending"));
        var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false);
        Assert.AreEqual(3, retry.Version); Assert.AreEqual(content, retry.AuthorizedContent);
        Assert.AreNotEqual(first.FollowupOperationId, retry.FollowupOperationId);
        Assert.AreEqual(first.Intent with { OperationId = retry.Intent.OperationId }, retry.Intent);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
        var final = await AuthorizeVerificationDecisionAsync(catalog, fixture, content, "changed-again");
        var completed = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.AreEqual(final.FollowupOperationId, completed[1].Intent.OperationId);
        Assert.IsNull(await catalog.ReadMutationAsync(first.FollowupOperationId!.Value));
        Assert.IsNull(await catalog.ReadMutationAsync(retry.FollowupOperationId!.Value));
        CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
    }

    [TestMethod]
    public async Task MoveOnlyKeepLocalCanAuthorizeNewBytesAfterTheFollowupUploadConflictsWithoutRepeatingTheMove()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var firstContent = DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)!;
        var first = await AuthorizeVerificationDecisionAsync(catalog, fixture, firstContent);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent,
            (accepted, _) =>
            {
                if (accepted.Intent.Kind == MirrorPulseWorkerChangeKind.Move) remote.Seed(fixture.Plan.FinalPath, "after-move", [6, 5]);
                return ValueTask.CompletedTask;
            }).AsTask());
        Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await catalog.ReadMutationAsync(first.Intent.OperationId))!.State);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(first.FollowupOperationId!.Value))!.State);
        byte[] edited = [4, 2, 4, 2];
        var newContent = firstContent with { Sha256 = Convert.ToHexString(SHA256.HashData(edited)) };
        var last = await AuthorizeVerificationDecisionAsync(catalog, fixture, newContent, "after-move");
        Assert.AreEqual(2, last.Version); Assert.AreEqual(1, last.Ordinal);
        var completed = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, (proof, _) =>
        {
            Assert.AreEqual(newContent, proof); return ValueTask.FromResult<Stream>(new MemoryStream(edited));
        });
        Assert.AreEqual(first.Intent.OperationId, completed[0].Intent.OperationId);
        Assert.AreEqual(last.Intent.OperationId, completed[1].Intent.OperationId);
        CollectionAssert.AreEqual(edited, remote.Read(fixture.Plan.FinalPath));
        CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
        Assert.AreEqual(firstContent, first.AuthorizedContent);
        Assert.IsNull((await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!.Content);
    }

    [TestMethod]
    [DataRow("followup-index")]
    [DataRow("authorization")]
    [DataRow("fingerprint")]
    public async Task MoveOnlyKeepLocalRefusesDamagedSuffixEvidenceBeforeAnyRemoteEffect(string corruption)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var decision = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged));
        await ExecuteRetrySqlAsync(fixture, corruption switch
        {
            "followup-index" => "UPDATE journal_coalescing_retries SET followup_operation_id=NULL;",
            "authorization" => "UPDATE user_commands SET action='retry';",
            _ => "UPDATE journal_coalescing_retries SET fingerprint=zeroblob(32);",
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
        Assert.IsNull(await catalog.ReadMutationAsync(decision.FollowupOperationId!.Value));
    }

    [TestMethod]
    public async Task MoveOnlyKeepLocalReservesItsFutureUploadBeforeThatMutationCanBePrepared()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var decision = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(decision.Intent with
        { OperationId = decision.FollowupOperationId!.Value, Origin = MirrorPulseMutationOrigin.Journal }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TryRecordWorkerRequestAsync(
            decision.FollowupOperationId!.Value, fixture.Plan.InstanceId, new byte[32]));
        using var other = new Fixture(MirrorPulseCoalescedEffect.MoveFile);
        var window = other.Window.ToArray(); window[0] = window[0] with { OperationId = decision.FollowupOperationId!.Value };
        var intruder = MirrorPulseJournalCoalescingPlanner.TryPlan(window, other.Plan.ItemId, 100, new HashSet<Guid>())!;
        Assert.IsNotNull(intruder);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionAsync(intruder,
            window, other.Revision));
        Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(decision.FollowupOperationId!.Value));
        Assert.IsNull(await catalog.ReadWorkerRequestAsync(decision.FollowupOperationId!.Value));
        Assert.IsNull(await catalog.ReadMutationAsync(decision.FollowupOperationId!.Value));
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MoveOnlyKeepLocalVerifiesADeletedRemoteSourceBeforeConditionallyCreatingItsTarget(bool occupiedTarget)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        await remote.DeleteAsync(new(fixture.Plan.InstanceId, fixture.Plan.OriginalPath, "changed", false, Guid.NewGuid(), "files"), default);
        remote.Writes.Clear();
        if (occupiedTarget) remote.Seed(fixture.Plan.FinalPath, "occupied", [7, 7]);
        var decision = await AuthorizeVerificationDecisionAsync(catalog, fixture,
            DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged), observed: null);
        Assert.AreEqual(3, decision.Version);
        Assert.AreEqual(MirrorPulseWorkerChangeKind.MetadataUpdate, decision.Intent.Kind);
        Assert.AreEqual(fixture.Plan.OriginalPath, decision.Intent.RelativePath);
        Assert.IsNull(decision.Intent.ExpectedRevision);
        if (occupiedTarget)
        {
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            Assert.IsEmpty(remote.Writes);
            CollectionAssert.AreEqual(new byte[] { 7, 7 }, remote.Read(fixture.Plan.FinalPath));
            Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(decision.FollowupOperationId!.Value))!.State);
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(decision.CommandId))!.State);
        }
        else
        {
            var completed = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
            Assert.HasCount(2, completed); Assert.IsNull(completed[1].Intent.ExpectedRevision);
            Assert.HasCount(1, remote.Writes); Assert.AreEqual("Upload", remote.Writes[0].Kind);
            CollectionAssert.AreEqual(FinalBytes, remote.Read(fixture.Plan.FinalPath));
            await catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(decision.CommandId))!.State);
        }
        Assert.IsFalse(remote.Exists(fixture.Plan.OriginalPath));
    }

    [TestMethod]
    public async Task Schema62UpgradePreservesOldMoveRetryWithoutInventingAnUploadSuffix()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        Guid command; string retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed");
            command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[0].ToString("D"), "pending"));
            retained = JsonSerializer.Serialize(await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false));
        }
        await ExecuteRetrySqlAsync(fixture, "DROP INDEX ix_journal_coalescing_followup; ALTER TABLE journal_coalescing_retries DROP COLUMN followup_operation_id; PRAGMA user_version=62;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var dispatch = await reopened.ReadJournalCoalescingDispatchAsync(fixture.Plan.PlanId);
        Assert.HasCount(1, dispatch.Steps); Assert.IsNull(dispatch.Content);
        Assert.AreEqual(retained, JsonSerializer.Serialize(await reopened.AuthorizeJournalCoalescingRetryAsync(command,
            Guid.Parse((await reopened.ReadUserCommandAsync(command))!.TargetId), false)));
        Assert.IsEmpty(remote.Writes);
    }
}
