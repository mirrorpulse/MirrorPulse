using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("admission")]
    [DataRow("child-observation")]
    [DataRow("parent-observation")]
    public async Task RemoteUpdateCannotPredateItsRegisteredMemberOrReuseAnEarlierNativeObservation(string stale)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        var bad = stale switch
        {
            "admission" => plan with
            {
                PreparedAt = setup.Member.RegisteredAt.AddTicks(-1),
                ChildBefore = plan.Child.Context.Verification,
                ParentBefore = plan.Parent.Context.Verification
            },
            "child-observation" => plan with { ChildBefore = plan.ChildBefore with { ObservedAt = setup.Member.RegisteredAt.AddTicks(-1) } },
            _ => plan with { ParentBefore = plan.ParentBefore with { ObservedAt = plan.Parent.Context.Member.RegisteredAt.AddTicks(-1) } }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRemoteUpdateAsync(bad));
        Assert.IsNull(await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("before-start")]
    [DataRow("child-binding")]
    [DataRow("child-dacl")]
    [DataRow("parent-binding")]
    [DataRow("parent-dacl")]
    [DataRow("parent-time")]
    [DataRow("child-time")]
    [DataRow("native-revision")]
    [DataRow("official-revision")]
    [DataRow("not-in-sync")]
    [DataRow("applied-conflict")]
    [DataRow("conflict-id")]
    [DataRow("conflict-native-revision")]
    [DataRow("conflict-official-revision")]
    [DataRow("source")]
    [DataRow("disposition")]
    [DataRow("stale-admission")]
    public async Task RemoteUpdateCannotSettleWithoutItsOwnFreshNativeAndSdkFacts(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        if (damaged == "stale-admission") plan = plan with { NativeRemoteRevision = "other-v1", OfficialRemoteRevision = "other-v1" };
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
        if (damaged != "before-start") await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        var outcome = RemoteUpdateOutcome(plan, damaged.StartsWith("conflict-", StringComparison.Ordinal), false);
        var bad = damaged switch
        {
            "child-binding" => outcome with { Child = outcome.Child with { LocalObject = outcome.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "child-dacl" => outcome with { Child = outcome.Child with { Dacl = "D:P" } },
            "parent-binding" => outcome with { Parent = outcome.Parent with { LocalObject = outcome.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent-dacl" => outcome with { Parent = outcome.Parent with { Dacl = "D:P" } },
            "parent-time" => outcome with { Parent = outcome.Parent with { ObservedAt = plan.PreparedAt } },
            "child-time" => outcome with { Child = outcome.Child with { ObservedAt = plan.PreparedAt } },
            "native-revision" => outcome with { NativeRemoteRevision = "foreign" },
            "official-revision" => outcome with { OfficialRemoteRevision = "foreign" },
            "not-in-sync" => outcome with { NativeIsInSync = false },
            "applied-conflict" => outcome with { ConflictId = Guid.NewGuid() },
            "conflict-id" => outcome with { ConflictId = null },
            "conflict-native-revision" => outcome with { NativeRemoteRevision = plan.RemoteRevision },
            "conflict-official-revision" => outcome with { OfficialRemoteRevision = plan.RemoteRevision },
            "source" => outcome with { Source = (MirrorPulseNamespaceRemoteUpdateEvidenceSource)99 },
            "disposition" => outcome with { Disposition = (MirrorPulseNamespaceRemoteUpdateDisposition)99 },
            _ => outcome
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteUpdateAsync(bad));
        var retained = (await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal))!;
        Assert.IsNull(retained.Outcome);
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("index")]
    [DataRow("missing-fence")]
    [DataRow("finished-index")]
    [DataRow("orphan-fence")]
    [DataRow("missing-completion")]
    public async Task RemoteUpdateCorruptionCannotHideItsUnsettledOwner(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
        if (damaged == "missing-completion")
        {
            await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
            await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, false, false));
        }
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "hash" => "UPDATE namespace_remote_updates SET fingerprint=zeroblob(32);",
            "index" => "UPDATE namespace_remote_updates SET batch_fingerprint='BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB';",
            "missing-fence" => "DELETE FROM namespace_remote_update_pending;",
            "finished-index" => "UPDATE namespace_remote_updates SET finished=1; DELETE FROM namespace_remote_update_pending;",
            "orphan-fence" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_remote_updates;",
            _ => "DELETE FROM namespace_remote_update_completions;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task RemoteUpdateOutcomeAndRootFenceReleaseCommitAtomically()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan); await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
        var outcome = RemoteUpdateOutcome(plan, false, false);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER interrupt_remote_update_release BEFORE DELETE ON namespace_remote_update_pending BEGIN SELECT RAISE(ABORT,'injected release commit gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SettleNamespaceRemoteUpdateAsync(outcome));
        Assert.IsNull((await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal))!.Outcome);
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_remote_update_release;");
        Assert.AreEqual(outcome, (await catalog.SettleNamespaceRemoteUpdateAsync(outcome)).Outcome);
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ACompletedRemoteUpdateEntryCannotBecomeAnotherUpdateBirthOrMove()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan); await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
        var terminal = await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, true, false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceRemoteUpdateAsync(plan with { OperationId = Guid.NewGuid() }));
        var move = setup.Move with
        {
            Version = 2,
            RemoteEntry = new(1, plan.InstanceId, plan.BatchId, plan.BatchFingerprint, plan.BatchEntryCount, plan.Ordinal,
                plan.ChangeId, plan.PreviousRemoteRevision, plan.RemoteRevision, plan.NativeRemoteRevision)
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
        var birth = Birth(setup.Parent, setup.ParentProof) with
        {
            Version = 3,
            Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation,
            RelativePath = "Docs/another.txt",
            RemoteEntry = RemoteBirthReference(setup.ParentMember.MemberId) with
            {
                InstanceId = plan.InstanceId,
                BatchId = plan.BatchId,
                BatchFingerprint = plan.BatchFingerprint,
                BatchEntryCount = plan.BatchEntryCount,
                Ordinal = plan.Ordinal,
                ChangeId = plan.ChangeId
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth));
        Assert.AreEqual(terminal, await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
    }
}
