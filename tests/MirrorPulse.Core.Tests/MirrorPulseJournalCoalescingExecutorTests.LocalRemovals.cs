using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
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
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task DeleteLocalRetainsEveryOriginalAndCompletesWithoutAnyRemoteEffect(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        Guid command; string execution; string rejected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var removal = await PrepareLocalRemovalAsync(catalog, fixture, remote); command = removal.Intent.CommandId;
            execution = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(removal.Intent.RejectedEffect!.OperationId));
            foreach (Guid original in removal.Intent.OriginalOperationIds)
            {
                Assert.AreEqual(command, await catalog.ReadConflictLocalRemovalOwnerAsync(original));
                Assert.IsNull(await catalog.ReadConflictDeletionOwnerAsync(original));
            }
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
            Assert.IsEmpty(remote.Writes);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await new MirrorPulseConflictLocalRemovalExecutor(reopened).ExecuteAsync(command,
            (choice, token) => CompleteSyntheticLocalRemovalAsync(reopened, choice, token), async (intent, token) =>
            {
                Assert.AreEqual(MirrorPulseConflictLocalRemovalPhase.Retired, (await reopened.ReadConflictLocalRemovalAsync(command, token))!.Phase);
                CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), intent.OriginalOperationIds.ToArray());
                Assert.HasCount(fixture.Plan.Members.Count, await reopened.ReadUploadConflictsAsync(cancellationToken: token));
            });
        Assert.AreEqual(MirrorPulseConflictLocalRemovalPhase.Completed, completed.Phase);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
        Assert.IsEmpty(await reopened.ReadUploadConflictsAsync()); Assert.IsEmpty(remote.Writes);
        Assert.AreEqual(execution, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await reopened.ReadMutationAsync(completed.Intent.RejectedEffect!.OperationId)));
        await new MirrorPulseConflictLocalRemovalExecutor(reopened).ExecuteAsync(command,
            (_, _) => throw new AssertFailedException("Completed local removal must not repeat."),
            (_, _) => throw new AssertFailedException("Completed original acknowledgement must not repeat."));
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow(MirrorPulseConflictLocalRemovalPhase.Prepared)]
    [DataRow(MirrorPulseConflictLocalRemovalPhase.Preserved)]
    [DataRow(MirrorPulseConflictLocalRemovalPhase.Retiring)]
    [DataRow(MirrorPulseConflictLocalRemovalPhase.Retired)]
    public async Task LocalOnlyInterruptionsRetainTheChoiceAndOriginalsAcrossCatalogReopen(MirrorPulseConflictLocalRemovalPhase cut)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture); Guid command;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var removal = await PrepareLocalRemovalAsync(catalog, fixture, remote); command = removal.Intent.CommandId;
            await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseConflictLocalRemovalExecutor(catalog).ExecuteAsync(command,
                async (choice, token) =>
                {
                    while (choice.Phase < cut)
                    {
                        var next = choice.Phase + 1;
                        await catalog.AdvanceConflictLocalRemovalAsync(command, choice.Phase, next, token);
                        choice = choice with { Phase = next };
                    }
                    if (cut != MirrorPulseConflictLocalRemovalPhase.Retired) throw new IOException("Synthetic local boundary interruption.");
                }, (_, _) => throw new IOException("Synthetic lost official acknowledgement reply.")).AsTask());
            Assert.AreEqual(cut, (await catalog.ReadConflictLocalRemovalAsync(command))!.Phase);
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await new MirrorPulseConflictLocalRemovalExecutor(reopened).ExecuteAsync(command,
            (choice, token) => CompleteSyntheticLocalRemovalAsync(reopened, choice, token), (_, _) => ValueTask.CompletedTask);
        Assert.AreEqual(MirrorPulseConflictLocalRemovalPhase.Completed, completed.Phase); Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    public async Task ALocalOnlyChoiceCannotBeReplacedByRemoteDeletionOrAnotherGroupRetry()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var choice = await PrepareLocalRemovalAsync(catalog, fixture, remote);
        foreach (string action in new[] { "deletelocal", "deleteremote", "retry" })
        {
            Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, action, choice.Intent.ConflictId.ToString("D"), "pending"));
            if (action == "deletelocal")
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareConflictLocalRemovalAsync(command, choice.Intent.ConflictId,
                    choice.Intent.RootKey, choice.Intent.ItemId, choice.Intent.LocalRelativePath, choice.Intent.LocalContent));
            else if (action == "deleteremote")
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareConflictDeletionAsync(command, choice.Intent.ConflictId,
                    choice.Intent.RootKey, choice.Intent.ItemId, choice.Intent.LocalRelativePath, choice.Intent.LocalContent));
            else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeJournalCoalescingRetryAsync(command, choice.Intent.ConflictId, false));
        }
        Assert.AreEqual(choice.Intent.CommandId, await catalog.ReadConflictLocalRemovalOwnerAsync(choice.Intent.OriginalOperationIds[0]));
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("authorization")]
    [DataRow("members")]
    [DataRow("epoch")]
    public async Task DamagedLocalOnlyEvidenceCannotReachLocalWorkOrOriginalAcknowledgement(string damage)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var choice = await PrepareLocalRemovalAsync(catalog, fixture, remote);
        string sql = damage switch
        {
            "fingerprint" => "UPDATE conflict_local_removals SET fingerprint=zeroblob(32);",
            "authorization" => "UPDATE user_commands SET action='deleteremote';",
            "members" => "DELETE FROM conflict_local_removal_members;",
            _ => "UPDATE upload_conflicts SET status=1;"
        };
        await ExecuteRetrySqlAsync(fixture, sql);
        await Assert.ThrowsAsync<InvalidDataException>(() => new MirrorPulseConflictLocalRemovalExecutor(catalog).ExecuteAsync(choice.Intent.CommandId,
            (_, _) => throw new AssertFailedException("Damaged provenance must be rejected before local work."),
            (_, _) => throw new AssertFailedException("Damaged provenance cannot acknowledge original IDs.")).AsTask());
        Assert.IsEmpty(remote.Writes);
    }

    private static async Task<MirrorPulseConflictLocalRemoval> PrepareLocalRemovalAsync(MirrorPulseProductCatalog catalog, Fixture fixture, FileRemote remote)
    {
        var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var first = execution.Steps[0];
        remote.Seed(first.PreviousRelativePath ?? first.RelativePath, "concurrent-local", [7, 7, 7]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "concurrent-local");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "deletelocal", conflicts[0].ToString("D"), "pending"));
        return await catalog.PrepareConflictLocalRemovalAsync(command, conflicts[0], fixture.Plan.RootKey, fixture.Plan.ItemId, fixture.Plan.FinalPath,
            fixture.Plan.Effect is MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null :
                DecisionContent(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged));
    }

    [TestMethod]
    public async Task Schema63UpgradeRetainsExistingRejectionsWithoutInventingALocalChoice()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var execution = await PrepareRejectedVerificationAsync(catalog, fixture, remote);
            original = JsonSerializer.Serialize(execution);
        }
        await ExecuteRetrySqlAsync(fixture,
            "DROP TABLE conflict_local_removal_members; DROP TABLE conflict_local_removals; PRAGMA user_version=63;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsEmpty(await reopened.ReadPendingConflictLocalRemovalsAsync());
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await reopened.ReadConflictLocalRemovalOwnerAsync(member.OperationId));
        Assert.AreEqual(original, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.IsEmpty(remote.Writes);
    }

    private static async ValueTask CompleteSyntheticLocalRemovalAsync(MirrorPulseProductCatalog catalog, MirrorPulseConflictLocalRemoval choice, CancellationToken token)
    {
        // This Core fixture supplies Host-local proof; native preservation and
        // controlled retirement are verified separately by the production fixture.
        while (choice.Phase < MirrorPulseConflictLocalRemovalPhase.Retired)
        {
            var next = choice.Phase + 1;
            await catalog.AdvanceConflictLocalRemovalAsync(choice.Intent.CommandId, choice.Phase, next, token);
            choice = choice with { Phase = next };
        }
    }
}
