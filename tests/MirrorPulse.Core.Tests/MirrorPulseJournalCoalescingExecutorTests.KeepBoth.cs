using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    private static readonly byte[] SelectedRemoteBytes = [7, 7, 7];
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task KeepBothRetainsOriginalGroupAndRemoteCopyBeforeAuthorizingItsDistinctLocalProgram(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths);
        Guid commandId = Guid.NewGuid(); MirrorPulseConflictKeepBothDecision decision; MirrorPulseConflictRecord selected;
        string originalProgram; string originalRejected; string copyPath;
        MirrorPulseCoalescingContent? content = effect is MirrorPulseCoalescedEffect.MoveFile or MirrorPulseCoalescedEffect.VerifyRemoteUnchanged
            ? new(FinalBytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(FinalBytes)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile)) : null;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
            string target = execution.Steps[0].PreviousRelativePath ?? execution.Steps[0].RelativePath;
            remote.Seed(target, "selected-remote", SelectedRemoteBytes);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            Guid[] conflicts = await SaveGroupConflictsAsync(catalog, fixture, "selected-remote");
            selected = (await catalog.ReadUploadConflictAsync(conflicts[0]))!;
            await catalog.SaveUserCommandAsync(new(commandId, "keepboth", selected.ConflictId.ToString("D"), "pending"));
            var source = new MirrorPulseConflictRemoteSource(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, target, "selected-object", "selected-remote", 3);
            decision = await catalog.PrepareConflictKeepBothAsync(commandId, selected.ConflictId, fixture.Plan.ItemId, selected.RelativePath, source, content);
            originalProgram = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            originalRejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(decision.Intent.RejectedEffect.OperationId));
            Assert.IsFalse(decision.RetryAuthorized); Assert.IsNull(decision.Copy);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
            foreach (var member in fixture.Plan.Members) Assert.AreEqual(commandId, await catalog.ReadConflictKeepBothOwnerAsync(member.OperationId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AuthorizeAsync(catalog, decision));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepBothOriginalRetriesPreparedAsync(commandId));
            copyPath = await copies.PreserveRemoteDecisionAsync(commandId, selected, source,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemoteBytes, writable: false)));
            await using var retainedCopy = await copies.OpenRemoteDecisionAsync(commandId, selected, source);
            await catalog.RecordConflictKeepBothCopyAsync(commandId, retainedCopy.Proof);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync(), "Preservation cannot resolve any original conflict.");
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await reopened.ReadConflictKeepBothAsync(commandId))!;
        Assert.IsNotNull(retained.Copy); Assert.IsFalse(retained.RetryAuthorized);
        Assert.Contains(commandId, (await reopened.ReadPendingConflictKeepBothAsync()).ToArray());
        await using (var copy = await copies.OpenRemoteDecisionAsync(commandId, selected, retained.Intent.RemoteSource))
        {
            Assert.AreEqual(copyPath, copy.Path); Assert.AreEqual(retained.Copy, copy.Proof);
            var retry = await AuthorizeAsync(reopened, retained);
            Assert.AreEqual("selected-remote", retry.Intent.ExpectedRevision);
            Assert.AreNotEqual(retained.Intent.RejectedEffect.OperationId, retry.Intent.OperationId);
            Assert.IsTrue((await reopened.ReadConflictKeepBothAsync(commandId))!.RetryAuthorized);
            CollectionAssert.AreEqual(new[] { commandId }, (await reopened.ReadCurrentCoalescingKeepBothCommandsAsync(fixture.Plan.PlanId)).ToArray());
            foreach (var member in fixture.Plan.Members) Assert.IsNull(await reopened.ReadConflictKeepBothOwnerAsync(member.OperationId));
            Assert.IsEmpty(await reopened.ReadUploadConflictsAsync());
            Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(commandId))!.State);
        }
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.IsTrue(completed.All(value => value.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual(originalProgram, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(originalRejected, JsonSerializer.Serialize(await reopened.ReadMutationAsync(retained.Intent.RejectedEffect.OperationId)));
        CollectionAssert.AreEqual(SelectedRemoteBytes, await File.ReadAllBytesAsync(copyPath));
        await reopened.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(commandId))!.State);
        Assert.IsEmpty(await reopened.ReadPendingConflictKeepBothAsync());
        Assert.IsTrue((await reopened.ReadConflictKeepBothAsync(commandId))!.RetryAuthorized);
    }

    [TestMethod]
    [DataRow("retry")]
    [DataRow("keeplocal")]
    [DataRow("deletelocal")]
    [DataRow("deleteremote")]
    public async Task APreparedKeepBothGroupCannotBeStolenByAnotherChoice(string action)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(fixture.Plan.OriginalPath, "selected-remote", [7]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var ids = await SaveGroupConflictsAsync(catalog, fixture, "selected-remote"); Guid original = Guid.NewGuid(), other = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(original, "keepboth", ids[0].ToString("D"), "pending"));
        await catalog.PrepareConflictKeepBothAsync(original, ids[0], fixture.Plan.ItemId, fixture.Plan.FinalPath,
            new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, fixture.Plan.OriginalPath, "selected-object", "selected-remote", 1), null);
        await catalog.SaveUserCommandAsync(new(other, action, ids[1].ToString("D"), "pending"));
        if (action == "deletelocal")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareConflictLocalRemovalAsync(other, ids[1], fixture.Plan.RootKey, fixture.Plan.ItemId, fixture.Plan.FinalPath, fixture.Content));
        else if (action == "deleteremote")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareConflictDeletionAsync(other, ids[1], fixture.Plan.RootKey, fixture.Plan.ItemId, fixture.Plan.FinalPath, fixture.Content));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeJournalCoalescingRetryAsync(other, ids[1], action == "keeplocal"));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
        Assert.IsNull((await catalog.ReadConflictKeepBothAsync(original))!.Copy);
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    public async Task AnotherRemoteRejectionKeepsBothPreservedEpochsAndAuthorizesANewDistinctAttempt(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths); var paths = new List<string>(); var commands = new List<Guid>(); var attempts = new HashSet<Guid>();
        MirrorPulseCoalescingContent? content = effect == MirrorPulseCoalescedEffect.MoveFile
            ? new(FinalBytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(FinalBytes)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile)) : null;
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        string program = JsonSerializer.Serialize(original); string target = original.Steps[0].PreviousRelativePath ?? original.Steps[0].RelativePath;
        for (int epoch = 0; epoch < 2; epoch++)
        {
            string revision = "selected-epoch-" + epoch;
            remote.Seed(target, revision, SelectedRemoteBytes);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            Guid[] conflicts = await SaveGroupConflictsAsync(catalog, fixture, revision); Guid commandId = Guid.NewGuid(); commands.Add(commandId);
            var selected = (await catalog.ReadUploadConflictAsync(conflicts[0]))!;
            await catalog.SaveUserCommandAsync(new(commandId, "keepboth", selected.ConflictId.ToString("D"), "pending"));
            var decision = await catalog.PrepareConflictKeepBothAsync(commandId, selected.ConflictId, fixture.Plan.ItemId, selected.RelativePath,
                new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, target, "object-epoch-" + epoch, revision, SelectedRemoteBytes.Length), content);
            paths.Add(await copies.PreserveRemoteDecisionAsync(commandId, selected, decision.Intent.RemoteSource,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemoteBytes, writable: false))));
            await using var copy = await copies.OpenRemoteDecisionAsync(commandId, selected, decision.Intent.RemoteSource);
            await catalog.RecordConflictKeepBothCopyAsync(commandId, copy.Proof);
            var authorized = await AuthorizeAsync(catalog, decision);
            Assert.IsTrue(attempts.Add(authorized.Intent.OperationId));
            Assert.AreEqual(authorized.Intent.ExpectedRevision, revision);
            foreach (Guid historical in commands) Assert.IsTrue((await catalog.ReadConflictKeepBothAsync(historical))!.RetryAuthorized);
            CollectionAssert.AreEqual(new[] { commandId }, (await catalog.ReadCurrentCoalescingKeepBothCommandsAsync(fixture.Plan.PlanId)).ToArray());
        }
        var results = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.IsTrue(results.All(value => value.State == MirrorPulseMutationState.Acknowledged));
        await catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        foreach (Guid id in commands) Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(id))!.State);
        Assert.HasCount(2, paths.Distinct().ToArray());
        foreach (string path in paths) CollectionAssert.AreEqual(SelectedRemoteBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(program, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "unexpected-content")]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile, "missing-content")]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged, "missing-content")]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "remote-path")]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "remote-version")]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "resolved-command")]
    public async Task AnUnexecutablePreservationChoiceCannotReserveAnyOriginal(MirrorPulseCoalescedEffect effect, string invalid)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        string target = execution.Steps[0].PreviousRelativePath ?? execution.Steps[0].RelativePath;
        remote.Seed(target, "selected-remote", SelectedRemoteBytes);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Guid[] conflicts = await SaveGroupConflictsAsync(catalog, fixture, "selected-remote"); Guid command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, "keepboth", conflicts[0].ToString("D"), invalid == "resolved-command" ? "resolved" : "pending"));
        var source = new MirrorPulseConflictRemoteSource(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey,
            invalid == "remote-path" ? "another.bin" : target, "selected-object", invalid == "remote-version" ? "another-version" : "selected-remote", SelectedRemoteBytes.Length);
        Task Prepare() => catalog.PrepareConflictKeepBothAsync(command, conflicts[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, source,
            invalid == "unexpected-content" ? fixture.Content : null);
        if (invalid is "remote-path" or "remote-version") await Assert.ThrowsExactlyAsync<InvalidDataException>(Prepare);
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(Prepare);
        Assert.IsNull(await catalog.ReadConflictKeepBothAsync(command));
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await catalog.ReadConflictKeepBothOwnerAsync(member.OperationId));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(remote.Writes);
    }

    private static Task<MirrorPulseJournalCoalescingRetry> AuthorizeAsync(MirrorPulseProductCatalog catalog, MirrorPulseConflictKeepBothDecision decision) =>
        decision.Intent.AuthorizedContent is { } content ? catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(decision.Intent.CommandId, decision.Intent.ConflictId, content) :
            catalog.AuthorizeJournalCoalescingRetryAsync(decision.Intent.CommandId, decision.Intent.ConflictId, true);
}
