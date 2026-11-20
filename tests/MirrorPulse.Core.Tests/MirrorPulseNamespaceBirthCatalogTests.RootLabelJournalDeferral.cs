using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseLocalOperationBlockReason.RootReconciliationRequired)]
    [DataRow(MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch)]
    public async Task RootJournalDeferralCannotRecreateItsBlockAfterAnOriginalAckReceipt(MirrorPulseLocalOperationBlockReason reason)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var blocked = new MirrorPulseBlockedLocalOperation(original.Observation.OperationId, null,
            original.Observation.RelativePath, reason, original.Observation.ObservedAt);
        var ordinary = blocked with { OperationId = Guid.NewGuid(), Reason = MirrorPulseLocalOperationBlockReason.IncompleteLocalContent };
        await catalog.SaveBlockedLocalOperationAsync(ordinary);
        await catalog.SaveBlockedLocalOperationAsync(blocked);
        CollectionAssert.Contains((await catalog.ReadBlockedLocalOperationsAsync()).ToArray(), blocked);
        var receipt = await catalog.CompleteRootLabelJournalAcknowledgementAsync(original, original.StartedAt.AddSeconds(1));
        Assert.IsNotNull(receipt.AcknowledgedAt);
        Assert.IsFalse((await catalog.ReadBlockedLocalOperationsAsync()).Any(item => item.OperationId == blocked.OperationId));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.SaveBlockedLocalOperationAsync(blocked)));
        CollectionAssert.AreEqual(new[] { ordinary }, (await catalog.ReadBlockedLocalOperationsAsync()).ToArray());
        Assert.AreEqual(receipt, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        Assert.IsNull(await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RootJournalDeferralCannotBorrowACompletedStatusFromDamagedOriginalBytes()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        await using var injection = Connection(fixture.Paths); await injection.OpenAsync();
        await using var sql = injection.CreateCommand();
        sql.Parameters.AddWithValue("$id", original.Observation.OperationId.ToString("D"));
        sql.CommandText = "SELECT payload FROM root_label_journal_acknowledgements WHERE operation_id=$id;";
        byte[] before = (byte[])(await sql.ExecuteScalarAsync())!;
        sql.CommandText = "UPDATE root_label_journal_acknowledgements SET status=1 WHERE operation_id=$id;";
        Assert.AreEqual(1, await sql.ExecuteNonQueryAsync());
        var blocked = new MirrorPulseBlockedLocalOperation(original.Observation.OperationId, null,
            original.Observation.RelativePath, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired, original.Observation.ObservedAt);
        await catalog.SaveBlockedLocalOperationAsync(blocked);
        CollectionAssert.AreEqual(new[] { blocked }, (await catalog.ReadBlockedLocalOperationsAsync()).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        sql.CommandText = "SELECT payload FROM root_label_journal_acknowledgements WHERE operation_id=$id;";
        CollectionAssert.AreEqual(before, (byte[])(await sql.ExecuteScalarAsync())!);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
