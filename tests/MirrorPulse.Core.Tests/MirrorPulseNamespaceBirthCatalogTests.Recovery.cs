using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task FiniteRecoveryPagesRetainEveryOriginalPhaseAcrossOwnersWithoutIncludingLaterAdmissions()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var births = new List<MirrorPulseNamespaceBirthIntent>();
        var plans = new List<MirrorPulseNamespaceBirthPlan>(); var starts = new List<MirrorPulseNamespaceBirthStart>();
        MirrorPulseNamespaceBirthObservation? observation = null;
        MirrorPulseNamespaceBirthRecoveryScan scan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, parent, protection);
            for (int index = 0; index < 4; index++)
            {
                var birth = Birth(parent, protection) with { RelativePath = $"Docs/phase-{index}.txt" };
                var plan = Plan(birth); var start = Start(plan);
                births.Add(birth); plans.Add(plan); starts.Add(start);
                await first.PrepareNamespaceBirthAsync(birth);
                if (index > 0) await first.PrepareNamespaceBirthPlanAsync(plan);
                if (index > 1) await first.RecordNamespaceBirthStartAsync(start);
                if (index == 3)
                {
                    observation = Observation(birth, plan, start);
                    await first.RecordNamespaceBirthObservationAsync(observation);
                }
            }
            scan = await first.BeginNamespaceBirthRecoveryScanAsync();
            Assert.AreEqual(4L, scan.ThroughSequence);
            await first.PrepareNamespaceBirthAsync(Birth(parent, protection) with { RelativePath = "Docs/later.txt" });
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var head = await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 2);
        Assert.HasCount(2, head.Entries); Assert.IsTrue(head.HasMore); Assert.AreEqual(2L, head.LastScannedSequence);
        Assert.AreEqual(births[0], head.Entries[0].Intent); Assert.IsNull(head.Entries[0].Plan);
        Assert.IsNull(head.Entries[0].Start); Assert.IsNull(head.Entries[0].Observation);
        Assert.AreEqual(plans[1], head.Entries[1].Plan); Assert.IsNull(head.Entries[1].Start);
        var tail = await second.ReadNamespaceBirthRecoveryPageAsync(scan, head.LastScannedSequence, 2);
        Assert.HasCount(2, tail.Entries); Assert.IsFalse(tail.HasMore); Assert.AreEqual(4L, tail.LastScannedSequence);
        Assert.AreEqual(starts[2], tail.Entries[0].Start); Assert.IsNull(tail.Entries[0].Observation);
        Assert.AreEqual(observation, tail.Entries[1].Observation);
        Assert.IsTrue(head.Entries.Concat(tail.Entries).All(entry => entry.OwnsNameReservation));
        CollectionAssert.AreEqual(births.ToArray(), head.Entries.Concat(tail.Entries).Select(entry => entry.Intent).ToArray());
        var terminal = await second.ReadNamespaceBirthRecoveryPageAsync(scan, tail.LastScannedSequence, 2);
        Assert.HasCount(0, terminal.Entries); Assert.IsFalse(terminal.HasMore); Assert.AreEqual(4L, terminal.LastScannedSequence);
        var next = await second.BeginNamespaceBirthRecoveryScanAsync();
        Assert.HasCount(1, (await second.ReadNamespaceBirthRecoveryPageAsync(next, scan.ThroughSequence, 2)).Entries);
        Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
        foreach (var birth in births) Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task MissingReservationsRemainVisibleAndAreNotClassifiedAsCompletion()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        var page = await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1);
        Assert.HasCount(1, page.Entries); Assert.IsFalse(page.Entries[0].OwnsNameReservation);
        Assert.AreEqual(birth, page.Entries[0].Intent); Assert.AreEqual(start, page.Entries[0].Start);
        Assert.IsNull(page.Entries[0].Observation);
        Assert.IsNull(await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("operation-index")]
    [DataRow("observation-fingerprint")]
    [DataRow("reservation-name")]
    public async Task CorruptAdmissionCannotBeSkippedByRecoveryPaging(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(Observation(birth, plan, start));
        }
        await ExecuteSqlAsync(fixture.Paths, scenario switch
        {
            "operation-index" => "PRAGMA foreign_keys=OFF; UPDATE namespace_birth_intents SET operation_id='not-an-operation';",
            "observation-fingerprint" => "UPDATE namespace_birth_observations SET fingerprint=zeroblob(32);",
            _ => "UPDATE namespace_birth_reservations SET child_name_key='ANOTHER.TXT';",
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1));
    }

    [TestMethod]
    public async Task EmptyAndCanceledRecoveryPassesPerformNoAdmissionOrNativeWork()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var scan = await catalog.BeginNamespaceBirthRecoveryScanAsync();
        Assert.AreEqual(0L, scan.ThroughSequence);
        var page = await catalog.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1);
        Assert.HasCount(0, page.Entries); Assert.IsFalse(page.HasMore);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.BeginNamespaceBirthRecoveryScanAsync(stop.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1, stop.Token));
        Assert.AreEqual(scan, await catalog.BeginNamespaceBirthRecoveryScanAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(-1L, 0L, 1)]
    [DataRow(0L, -1L, 1)]
    [DataRow(0L, 1L, 1)]
    [DataRow(0L, 0L, 0)]
    [DataRow(0L, 0L, 257)]
    public async Task RecoveryPageBoundsAreEnforced(long through, long after, int limit)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            catalog.ReadNamespaceBirthRecoveryPageAsync(new(through), after, limit));
    }
}
