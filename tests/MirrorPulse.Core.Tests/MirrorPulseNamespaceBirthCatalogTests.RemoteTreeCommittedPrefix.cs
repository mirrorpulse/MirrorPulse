using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OriginalTreeCommittedPrefixRecoveryDoesNotInventALostSdkReturn()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, supplied) = await PrepareTreeSettlementAsync(catalog, 1, recordOutcome: false);
        var recovered = supplied with { EntryStatus = "CommittedPrefix" };
        Assert.AreEqual(recovered, await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(recovered));
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            entries.Reverse().Select(entry => TreeMemberSettlement(batch, recovered, entry)).ToArray());
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, recovered), recovered.VerifiedAt.AddSeconds(5));
        Assert.AreEqual("CommittedPrefix", (await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId))!.EntryStatus);
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task Schema73UpgradePreservesOriginalTreeStartOutcomeAndPartialMemberProgress()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome; MirrorPulseNamespaceRemoteRetirementTreeStart start;
        MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress progress;
        MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement receipt;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            MirrorPulseNamespaceRemoteRetirementTreeEntry[] entries;
            (batch, entries, outcome) = await PrepareTreeSettlementAsync(first, 1);
            start = (await first.ReadNamespaceRemoteRetirementTreeStartAsync(batch.OperationId))!;
            receipt = TreeMemberSettlement(batch, outcome, entries[1]);
            progress = await first.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, [receipt]);
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=73;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await second.ReadNamespaceRemoteRetirementTreeStartAsync(batch.OperationId));
        Assert.AreEqual(outcome, await second.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        Assert.AreEqual(progress, await second.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId));
        Assert.AreEqual(receipt, await second.ReadNamespaceRemoteRetirementTreeMemberSettlementAsync(batch.OperationId, 1));
        Assert.AreEqual("Applied", outcome.EntryStatus);
    }
}
