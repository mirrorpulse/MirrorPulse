using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false, 12L)]
    [DataRow(false, true, 0L)]
    [DataRow(true, false, 12L)]
    [DataRow(true, true, 123L)]
    public async Task TransferJournalReservationRetainsExactOriginalsAcrossOwnersWithoutAcknowledgingOrReleasingRoots(bool sameInstance, bool empty, long throughSequence)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; MirrorPulseNamespaceTransferJournalClaim claim;
        MirrorPulseNamespaceTransferCopy started;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first, sameInstance)).Plan);
            await first.PrepareNamespaceTransferAsync(plan); claim = TransferJournalClaim(plan);
            claim = claim with { ThroughSequence = throughSequence, Members = empty ? [] : claim.Members };
            var reserved = await first.PrepareNamespaceTransferJournalClaimAsync(claim);
            Assert.AreEqual(claim, reserved.JournalClaim); Assert.AreEqual(claim.GetHashCode(), reserved.JournalClaim!.GetHashCode());
            Assert.AreEqual(JsonSerializer.Serialize(claim), JsonSerializer.Serialize(reserved.JournalClaim));
            started = await first.StartNamespaceTransferCopyAsync(plan.OperationId, claim.CapturedAt.AddSeconds(1));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferAsync(plan.OperationId))!;
        Assert.AreEqual(started, actual); Assert.AreEqual(claim, actual.JournalClaim);
        Assert.AreEqual(claim.GetHashCode(), actual.JournalClaim!.GetHashCode());
        Assert.AreEqual(JsonSerializer.Serialize(claim), JsonSerializer.Serialize(actual.JournalClaim));
        Assert.AreEqual(JsonSerializer.Serialize(actual), JsonSerializer.Serialize(await reopened.PrepareNamespaceTransferJournalClaimAsync(claim with { Members = claim.Members.ToArray() })));
        foreach (var member in claim.Members)
        {
            Assert.AreEqual(plan.OperationId, await reopened.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
            Assert.IsNull(await reopened.ReadMutationAsync(member.OperationId));
        }
        Assert.IsNull(await reopened.ReadNamespaceTransferJournalOwnerAsync(Guid.NewGuid()));
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        var status = (await reopened.ReadNamespaceTransferStatusAsync(plan.OperationId))!;
        Assert.IsFalse(status.JournalAcknowledged); Assert.IsFalse(status.Completed); Assert.IsTrue(status.RootsFenced);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("negative-window")]
    [DataRow("before-plan")]
    [DataRow("duplicate-id")]
    [DataRow("duplicate-sequence")]
    [DataRow("reversed-sequence")]
    [DataRow("past-window")]
    [DataRow("no-id")]
    [DataRow("copy-id")]
    [DataRow("command-id")]
    [DataRow("delete-id")]
    [DataRow("content-id")]
    [DataRow("foreign-item")]
    [DataRow("missing-item")]
    [DataRow("empty-item")]
    [DataRow("different-name")]
    [DataRow("different-case")]
    [DataRow("missing-path")]
    [DataRow("previous-path")]
    [DataRow("directory")]
    [DataRow("move")]
    [DataRow("delete")]
    [DataRow("metadata")]
    [DataRow("unknown-kind")]
    [DataRow("future-observation")]
    [DataRow("missing-time")]
    [DataRow("missing-member")]
    [DataRow("too-many")]
    public async Task InvalidTransferJournalOwnershipCannotReserveAnyOriginalOrChangeItsAdmission(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var original = TransferJournalClaim(plan); var member = original.Members[1];
        var badMember = change switch
        {
            "duplicate-id" => member with { OperationId = original.Members[0].OperationId },
            "duplicate-sequence" => member with { Sequence = original.Members[0].Sequence },
            "reversed-sequence" => member with { Sequence = 1 },
            "past-window" => member with { Sequence = original.ThroughSequence + 1 },
            "no-id" => member with { OperationId = Guid.Empty },
            "copy-id" => member with { OperationId = plan.CopyOperationId },
            "command-id" => member with { OperationId = plan.OperationId },
            "delete-id" => member with { OperationId = plan.DeleteOperationId },
            "content-id" => member with { OperationId = plan.PrivateContentId },
            "foreign-item" => member with { ItemId = Guid.NewGuid() },
            "missing-item" => member with { ItemId = null },
            "empty-item" => member with { ItemId = Guid.Empty },
            "different-name" => member with { RelativePath = "Docs/other.txt" },
            "different-case" => member with { RelativePath = member.RelativePath.ToUpperInvariant() },
            "missing-path" => member with { RelativePath = null! },
            "previous-path" => member with { PreviousRelativePath = member.RelativePath },
            "directory" => member with { IsDirectory = true },
            "move" => member with { Kind = MirrorPulseWorkerChangeKind.Move },
            "delete" => member with { Kind = MirrorPulseWorkerChangeKind.Delete },
            "metadata" => member with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate },
            "unknown-kind" => member with { Kind = (MirrorPulseWorkerChangeKind)999 },
            "future-observation" => member with { ObservedAt = original.CapturedAt.AddTicks(1) },
            "missing-time" => member with { ObservedAt = default },
            "missing-member" => null!,
            _ => member
        };
        var invalid = original with { Members = [original.Members[0], badMember] };
        invalid = change switch
        {
            "version" => invalid with { Version = 2 },
            "negative-window" => invalid with { ThroughSequence = -1 },
            "before-plan" => invalid with { CapturedAt = plan.PreparedAt.AddTicks(-1) },
            "too-many" => invalid with { Members = Enumerable.Repeat(member, 4097).ToArray() },
            _ => invalid
        };
        string before = await ReadTransferPayloadHexAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(invalid));
        Assert.AreEqual(before, await ReadTransferPayloadHexAsync(fixture.Paths));
        foreach (var expected in original.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(expected.OperationId));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyOrAlreadyStartedTransfersCannotRetroactivelyAdoptOriginalNotifications(bool legacy)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan;
        if (!legacy) plan = WithTransferCommandOrigin(plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan);
        if (!legacy) await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        if (legacy) await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(claim));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(claim));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim);
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
    }

    [TestMethod]
    public async Task OriginalTransferJournalReservationRejectsReplacementLateMembersAndCopyChronology()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan);
        await catalog.PrepareNamespaceTransferJournalClaimAsync(claim);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt));
        var late = claim.Members[1] with { OperationId = Guid.NewGuid(), Sequence = 13 };
        foreach (var replacement in new[] { claim with { Members = [claim.Members[0]] }, claim with { ThroughSequence = 99 },
            claim with { CapturedAt = claim.CapturedAt.AddTicks(1) }, claim with { ThroughSequence = 13, Members = [.. claim.Members, late] } })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(replacement));
        Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(late.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(claim), JsonSerializer.Serialize((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim));
    }

    [TestMethod]
    [DataRow("mutation")]
    [DataRow("coalescing")]
    public async Task PreviouslyOwnedJournalOriginalsCannotBeAdoptedByATransfer(string existing)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan); var claim = TransferJournalClaim(plan);
        if (existing == "mutation") await catalog.PrepareMutationAsync(TransferJournalMutation(plan, claim.Members[1].OperationId));
        else await catalog.PrepareJournalCoalescingAsync(TransferJournalCoalescing(plan, claim.Members.Select(member => member.OperationId).ToArray()));
        await catalog.PrepareNamespaceTransferAsync(plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(claim));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim);
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
    }

    [TestMethod]
    [DataRow("mutation")]
    [DataRow("coalescing-member")]
    [DataRow("coalescing-plan")]
    public async Task TransferJournalOwnershipPreventsAnotherMutationOrCoalescingOwner(string competitor)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan); await catalog.PrepareNamespaceTransferJournalClaimAsync(claim);
        Guid original = claim.Members[0].OperationId;
        if (competitor == "mutation")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(TransferJournalMutation(plan, original)));
        else
        {
            var coalescing = TransferJournalCoalescing(plan, competitor == "coalescing-member" ? [original, Guid.NewGuid()] : [Guid.NewGuid(), Guid.NewGuid()]);
            if (competitor == "coalescing-plan") coalescing = coalescing with { PlanId = original };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(coalescing));
            Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(original));
        }
        Assert.IsNull(await catalog.ReadMutationAsync(original));
        Assert.AreEqual(plan.OperationId, await catalog.ReadNamespaceTransferJournalOwnerAsync(original));
    }

    [TestMethod]
    public async Task LateJournalIndexWriteFailureRollsBackTheWholeClaimAndEveryEarlierReservation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan); string before = await ReadTransferPayloadHexAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_late_claim BEFORE INSERT ON namespace_transfer_journal_members WHEN NEW.sequence=12 BEGIN SELECT RAISE(ABORT,'injected late original index failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(claim));
        Assert.AreEqual(before, await ReadTransferPayloadHexAsync(fixture.Paths));
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_claim;");
        await catalog.PrepareNamespaceTransferJournalClaimAsync(claim);
        foreach (var member in claim.Members) Assert.AreEqual(plan.OperationId, await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
    }

    [TestMethod]
    [DataRow("missing-member")]
    [DataRow("wrong-sequence")]
    [DataRow("extra-member")]
    [DataRow("orphan")]
    [DataRow("noncanonical-id")]
    public async Task ContradictoryOriginalJournalIndexesCannotHidePendingTransferWork(string corruption)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first)).Plan);
            await first.PrepareNamespaceTransferAsync(plan);
            var claim = TransferJournalClaim(plan);
            if (corruption == "noncanonical-id") claim = claim with
            { Members = [claim.Members[0] with { OperationId = Guid.Parse("ab000000-0000-4000-8000-000000000001") }, claim.Members[1]] };
            await first.PrepareNamespaceTransferJournalClaimAsync(claim);
        }
        string sql = corruption switch
        {
            "missing-member" => "DELETE FROM namespace_transfer_journal_members WHERE sequence=11;",
            "wrong-sequence" => "UPDATE namespace_transfer_journal_members SET sequence=99 WHERE sequence=12;",
            "extra-member" => $"INSERT INTO namespace_transfer_journal_members VALUES('{Guid.NewGuid():D}','{plan.OperationId:D}',99);",
            "noncanonical-id" => "UPDATE namespace_transfer_journal_members SET operation_id=UPPER(operation_id) WHERE sequence=11;",
            _ => "DELETE FROM namespace_transfer_roots; DELETE FROM namespace_transfer_effects; DELETE FROM namespace_transfers;"
        };
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        if (corruption != "orphan") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceTransferAsync(plan.OperationId));
    }

    [TestMethod]
    public async Task CompetingJournalCapturesCannotBothReplaceTheSameOriginalTransferReservation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan);
        var other = claim with { Members = [claim.Members[0] with { OperationId = Guid.NewGuid() }, claim.Members[1] with { OperationId = Guid.NewGuid() }] };
        async Task<bool> Reserve(MirrorPulseNamespaceTransferJournalClaim choice)
        {
            try { await catalog.PrepareNamespaceTransferJournalClaimAsync(choice); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = Task.Run(async () => { await start.Task; return await Reserve(claim); });
        Task<bool> second = Task.Run(async () => { await start.Task; return await Reserve(other); });
        start.SetResult();
        Assert.AreEqual(1, (await Task.WhenAll(first, second)).Count(won => won));
        var actual = (await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim!;
        foreach (var member in actual.Members) Assert.AreEqual(plan.OperationId, await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        var loser = actual.Members[0].OperationId == claim.Members[0].OperationId ? other : claim;
        foreach (var member in loser.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
    }

    [TestMethod]
    public async Task Schema81MigrationRetainsOriginalTransferBytesAndInventsNoJournalClaim()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; string original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first)).Plan);
            await first.PrepareNamespaceTransferAsync(plan); original = await ReadTransferPayloadHexAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_journal_members; PRAGMA user_version=81;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await ReadTransferPayloadHexAsync(fixture.Paths));
        var copy = (await reopened.ReadNamespaceTransferAsync(plan.OperationId))!;
        Assert.AreEqual(plan, copy.Plan); Assert.IsNull(copy.JournalClaim);
        Assert.IsFalse(JsonSerializer.Serialize(copy).Contains("JournalClaim", StringComparison.Ordinal));
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema82CannotRecreateAMissingOriginalJournalOwnershipTable()
    {
        using var fixture = new Fixture();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first)).Plan);
            await first.PrepareNamespaceTransferAsync(plan); await first.PrepareNamespaceTransferJournalClaimAsync(TransferJournalClaim(plan));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_journal_members;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task CancelledJournalCaptureLeavesTheOriginalAdmissionAndEveryOriginalUnclaimed()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan);
        string original = await ReadTransferPayloadHexAsync(fixture.Paths);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareNamespaceTransferJournalClaimAsync(claim, cancellation.Token));
        Assert.AreEqual(original, await ReadTransferPayloadHexAsync(fixture.Paths));
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task CallerCannotMutateTheOriginalJournalReservationThroughItsSuppliedCollection()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan); var claim = TransferJournalClaim(plan);
        var members = claim.Members.ToArray(); string original = JsonSerializer.Serialize(claim);
        var actual = await catalog.PrepareNamespaceTransferJournalClaimAsync(claim with { Members = members });
        members[0] = members[0] with { OperationId = Guid.NewGuid() };
        Assert.AreEqual(original, JsonSerializer.Serialize(actual.JournalClaim));
        Assert.AreEqual(original, JsonSerializer.Serialize((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim));
        Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(members[0].OperationId));
    }

    [TestMethod]
    public async Task OriginalWindowsJournalPathsRemainVerbatimAcrossCatalogOwnersAndCannotBeRewrittenOnReplay()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferJournalClaim original; string rawPath;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first)).Plan);
            await first.PrepareNamespaceTransferAsync(plan); rawPath = plan.Source.Context.Location.RelativePath.Replace('/', '\\');
            var claim = TransferJournalClaim(plan);
            original = claim with { Members = claim.Members.Select(member => member with { RelativePath = rawPath }).ToArray() };
            await first.PrepareNamespaceTransferJournalClaimAsync(original);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferAsync(original.OperationId))!;
        Assert.AreEqual(original, actual.JournalClaim);
        foreach (var member in actual.JournalClaim!.Members) Assert.AreEqual(rawPath, member.RelativePath);
        Assert.AreEqual(rawPath.Replace('\\', '/'), actual.Plan.Source.Context.Location.RelativePath);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceTransferJournalClaimAsync(original with
        { Members = original.Members.Select(member => member with { RelativePath = member.RelativePath.Replace('\\', '/') }).ToArray() }));
        Assert.AreEqual(original, (await reopened.ReadNamespaceTransferAsync(original.OperationId))!.JournalClaim);
    }

    private static MirrorPulseNamespaceTransferJournalClaim TransferJournalClaim(MirrorPulseNamespaceTransferPlan plan) => new(1,
        plan.OperationId, 12, plan.PreparedAt.AddSeconds(1), [
            new(Guid.NewGuid(), 11, MirrorPulseWorkerChangeKind.Create, plan.Source.Identity.ItemId,
                plan.Source.Context.Location.RelativePath, null, false, plan.PreparedAt.AddSeconds(-1)),
            new(Guid.NewGuid(), 12, MirrorPulseWorkerChangeKind.ContentUpdate, plan.Source.Identity.ItemId,
                plan.Source.Context.Location.RelativePath, null, false, plan.PreparedAt)]);

    private static MirrorPulseMutationIntent TransferJournalMutation(MirrorPulseNamespaceTransferPlan plan, Guid id) => new(id,
        plan.SourceRoute.InstanceId, plan.SourceRoute.RootKey, MirrorPulseWorkerChangeKind.ContentUpdate, "new.txt", null, false,
        plan.SourceRemoteRevision, plan.Length, plan.Sha256, MirrorPulseMutationOrigin.Journal);

    private static MirrorPulseJournalCoalescingPlan TransferJournalCoalescing(MirrorPulseNamespaceTransferPlan plan, Guid[] ids) => new(
        Guid.NewGuid(), plan.SourceRoute.InstanceId, plan.SourceRoute.RootKey, plan.Source.Identity.ItemId!.Value, 12,
        "new.txt", "new.txt", MirrorPulseCoalescedEffect.UpdateFile,
        ids.Select((id, index) => new MirrorPulseJournalOperationReference(id, 11 + index, new string('A', 64))).ToArray());
}
