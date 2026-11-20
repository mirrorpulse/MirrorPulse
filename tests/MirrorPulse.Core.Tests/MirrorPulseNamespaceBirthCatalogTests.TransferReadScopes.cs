using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("birth-plan")]
    [DataRow("source-permission")]
    [DataRow("original-journal")]
    public async Task TransferReadScopesObserveAtomicFenceReleaseAndRejectNextTransactionDamage(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog, sameInstance: false);
        var started = await StartTransferAckAsync(catalog, target);
        var operation = started.Plan.OperationId;
        Assert.IsFalse((await catalog.ReadNamespaceTransferStatusAsync(operation))!.Completed);
        Assert.HasCount(1, await catalog.ReadPendingNamespaceTransferStatusesAsync(started.Plan.SourceRoute.RootId));
        foreach (var original in started.JournalClaim!.Members)
            Assert.AreEqual(operation, await catalog.ReadNamespaceTransferJournalOwnerAsync(original.OperationId));
        var measured = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceTransferAsync(operation));
        Assert.AreEqual(JsonSerializer.Serialize(started), JsonSerializer.Serialize(measured.Result));
        // The catalog fixture supplies synthetic SDK acknowledgement facts. The
        // actual transaction must still publish the receipt and release both fences.
        var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(1));
        Assert.AreEqual(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(await catalog.ReadNamespaceTransferAsync(operation)));
        var status = (await catalog.ReadNamespaceTransferStatusAsync(operation))!;
        Assert.IsTrue(status.Completed); Assert.IsTrue(status.JournalAcknowledged); Assert.IsFalse(status.RootsFenced);
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsEmpty(await catalog.ReadPendingNamespaceTransferStatusesAsync(started.Plan.SourceRoute.RootId));
        Assert.IsEmpty(await catalog.ReadPendingNamespaceTransferStatusesAsync(started.Plan.TargetRoute.RootId));
        string sql = damaged switch
        {
            "birth-plan" => $"UPDATE namespace_birth_plans SET fingerprint=zeroblob(32) WHERE operation_id='{target.Member.OriginId:D}';",
            "source-permission" => $"UPDATE namespace_permission_changes SET phase=0 WHERE operation_id='{started.Plan.SourceParent.Context.Protection.ProtectionId:D}';",
            _ => $"DELETE FROM namespace_transfer_journal_members WHERE operation_id='{started.JournalClaim.Members[0].OperationId:D}';"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceTransferAsync(operation));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Console.WriteLine("TransferReadScopeSql: " + JsonSerializer.Serialize(new
        { damaged, measured.Selects, measured.TableReads, originalAcknowledgementSynthetic = true, actualFenceTransition = true, nativeExecuted = false }));
    }
}
