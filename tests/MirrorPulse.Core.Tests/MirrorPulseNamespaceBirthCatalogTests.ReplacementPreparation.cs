using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task AtomicReplacementPreparationRetainsTheCompleteFirstAuthorityAcrossOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent;
        MirrorPulseNamespaceReplacementStart start; MirrorPulseNamespaceReplacementAction action;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            var facts = ReplacementExecutionFacts(intent); start = facts.Start; action = facts.Action;
            var prepared = await catalog.PrepareNamespaceReplacementExecutionAsync(intent, start, action);
            Assert.AreEqual(intent, prepared.Intent); Assert.AreEqual(start, prepared.Start); Assert.AreEqual(action, prepared.Action);
            Assert.IsNull(prepared.Backup); Assert.IsNull(prepared.WriteStart); Assert.IsNull(prepared.ContentCompletion);
            Assert.IsNull(await catalog.ReadMutationAsync(intent.OperationId));
            CollectionAssert.AreEqual(new[] { intent.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = await reopened.PrepareNamespaceReplacementExecutionAsync(intent, start, action);
        Assert.AreEqual(action, actual.Action); Assert.AreEqual(start, actual.Start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceReplacementExecutionAsync(intent, start,
            action with { ActionId = Guid.NewGuid() }));
        Assert.AreEqual(action, (await reopened.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.Action);
    }

    [TestMethod]
    [DataRow("namespace_replacement_pending")]
    [DataRow("namespace_replacement_starts")]
    [DataRow("namespace_replacement_execution_facts")]
    public async Task AtomicPreparationFailureNeverLeavesAnAdmissionWithoutItsFirstAction(string boundary)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent;
        MirrorPulseNamespaceReplacementStart start; MirrorPulseNamespaceReplacementAction action;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            var facts = ReplacementExecutionFacts(intent); start = facts.Start; action = facts.Action;
            await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER fail_preparation BEFORE INSERT ON {boundary} BEGIN SELECT RAISE(ABORT,'preparation interrupted'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceReplacementExecutionAsync(intent, start, action));
            Assert.IsNull(await catalog.ReadNamespaceReplacementAsync(intent.OperationId));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.AreEqual(intent.Original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(intent.MemberId));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_preparation;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(action, (await reopened.PrepareNamespaceReplacementExecutionAsync(intent, start, action)).Action);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AtomicPreparationCanCompleteOnlyAnUnchangedLegacyAdmission(bool alreadyStarted)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent);
        await catalog.PrepareNamespaceReplacementAsync(intent);
        if (alreadyStarted) await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        var prepared = await catalog.PrepareNamespaceReplacementExecutionAsync(intent, facts.Start, facts.Action);
        Assert.AreEqual(facts.Start, prepared.Start); Assert.AreEqual(facts.Action, prepared.Action);
        Assert.IsNull(prepared.Backup); Assert.IsNull(await catalog.ReadMutationAsync(intent.OperationId));
    }
}
