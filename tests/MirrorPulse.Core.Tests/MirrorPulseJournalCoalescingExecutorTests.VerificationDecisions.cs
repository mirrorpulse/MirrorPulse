using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task KeepLocalReplacesVerificationWithAnExplicitConditionalEffectWithoutRewritingHistory(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        var content = DecisionContent(effect);
        Guid command; Guid effectId; string originalProgram;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            originalProgram = JsonSerializer.Serialize(prepared);
            var authorized = await AuthorizeVerificationDecisionAsync(catalog, fixture, content);
            command = authorized.CommandId; effectId = authorized.Intent.OperationId;
            Assert.AreEqual(2, authorized.Version);
            Assert.AreEqual(content, authorized.AuthorizedContent);
            Assert.AreEqual("changed", authorized.Intent.ExpectedRevision);
            Assert.AreEqual(effect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? MirrorPulseWorkerChangeKind.Delete
                : MirrorPulseWorkerChangeKind.ContentUpdate, authorized.Intent.Kind);
            Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(prepared.Steps[0].OperationId))!.State);
            Assert.AreEqual(originalProgram, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
            Assert.IsEmpty(remote.Writes);
            Assert.IsEmpty(await catalog.ReadUploadConflictsAsync());
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, (proof, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual(content, proof);
            return ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes));
        });
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual(effectId, completed[0].Intent.OperationId);
        Assert.HasCount(1, remote.Writes);
        Assert.AreEqual(effect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? "Delete" : "Upload", remote.Writes[0].Kind);
        Assert.AreEqual(effectId, remote.Writes[0].Id);
        if (effect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence) Assert.IsFalse(remote.Exists(fixture.Plan.OriginalPath));
        else CollectionAssert.AreEqual(FinalBytes, remote.Read(fixture.Plan.FinalPath));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(command))!.State);
        await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, (_, _) => throw new AssertFailedException("Completed decision must not reopen content."));
        Assert.HasCount(1, remote.Writes);
        Assert.AreEqual(originalProgram, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        // The production Host separately verifies that all original official IDs are absent.
        await reopened.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task VerificationReplacementRecoversLostRepliesByActualEffectReadbackWithoutAnotherWrite(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        Guid operation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            operation = (await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(effect))).Intent.OperationId;
            remote.LoseReplyAfter = effect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? "Delete" : "Upload";
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            Assert.AreEqual(MirrorPulseMutationState.Ambiguous, (await catalog.ReadMutationAsync(operation))!.State);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("Lost replies require remote readback, not a second content stream."));
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual(operation, completed[0].Intent.OperationId);
        Assert.HasCount(1, remote.Writes);
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task RetryingARejectedReplacementKeepsItsEffectAndProofUntilAnotherExplicitKeepLocalDecision(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var first = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(effect));
        remote.Seed(fixture.Plan.OriginalPath, "changed-again", [7, 7]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed-again");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[0].ToString("D"), "pending"));
        var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false);
        Assert.AreEqual(2, retry.Version); Assert.AreEqual(first.AuthorizedContent, retry.AuthorizedContent);
        Assert.AreEqual(first.Intent with { OperationId = retry.Intent.OperationId }, retry.Intent);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
        var last = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(effect), "changed-again");
        Assert.AreNotEqual(first.Intent.OperationId, retry.Intent.OperationId); Assert.AreNotEqual(retry.Intent.OperationId, last.Intent.OperationId);
        await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.HasCount(1, remote.Writes);
        Assert.AreEqual(last.Intent.OperationId, remote.Writes[0].Id);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(first.Intent.OperationId))!.State);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(retry.Intent.OperationId))!.State);
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("baseline")]
    [DataRow("cycle")]
    public async Task ReplacementReplayValidatesEveryPriorAuthorizationAndTheOriginalRejectedBaseline(string corruption)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var program = await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var first = await AuthorizeVerificationDecisionAsync(catalog, fixture, DecisionContent(fixture.Plan.Effect));
        remote.Seed(fixture.Plan.OriginalPath, "changed-again", [7, 7]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed-again");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[0].ToString("D"), "pending"));
        var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false);
        if (corruption == "action")
            await ExecuteRetrySqlAsync(fixture, $"UPDATE user_commands SET action='retry' WHERE command_id='{first.CommandId:D}';");
        else if (corruption == "baseline")
            await ExecuteRetrySqlAsync(fixture, $"UPDATE mutation_intents SET payload=CAST(json_set(CAST(payload AS TEXT),'$.ExpectedRevision','forged') AS BLOB) WHERE operation_id='{program.Steps[0].OperationId:D}';");
        else
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            update.CommandText = "SELECT payload FROM journal_coalescing_retries WHERE command_id=$command;";
            update.Parameters.AddWithValue("$command", first.CommandId.ToString("D"));
            var changed = JsonNode.Parse((byte[])(await update.ExecuteScalarAsync())!)!;
            changed["PreviousOperationId"] = first.Intent.OperationId.ToString("D");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed);
            update.CommandText = "UPDATE journal_coalescing_retries SET payload=$payload,fingerprint=$hash WHERE command_id=$command;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await update.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0));
        Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(retry.Intent.OperationId))!.State);
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    public async Task NewVerificationContentCannotReplaceAnExistingUploadProgram()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareRejectedVerificationAsync(catalog, fixture, remote);
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "keeplocal", conflicts[0].ToString("D"), "pending"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(
            command, conflicts[0], DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)!));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(remote.Writes);
    }

    private static MirrorPulseCoalescingContent? DecisionContent(MirrorPulseCoalescedEffect effect) =>
        effect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null :
        new(4, Convert.ToHexString(SHA256.HashData(FinalBytes)), new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.None));

    private static async Task<MirrorPulseJournalCoalescingExecution> PrepareRejectedVerificationAsync(MirrorPulseProductCatalog catalog,
        Fixture fixture, FileRemote remote)
    {
        var program = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(fixture.Plan.OriginalPath, "changed", [6, 6, 6]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        return program;
    }

    private static async Task<MirrorPulseJournalCoalescingRetry> AuthorizeVerificationDecisionAsync(MirrorPulseProductCatalog catalog,
        Fixture fixture, MirrorPulseCoalescingContent? content, string observed = "changed")
    {
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, observed);
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "keeplocal", conflicts[^1].ToString("D"), "pending"));
        return content is null ? await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[^1], true) :
            await catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(command, conflicts[^1], content);
    }
}
