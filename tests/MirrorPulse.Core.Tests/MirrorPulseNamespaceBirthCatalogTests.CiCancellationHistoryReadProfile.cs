using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiCancellationHistoryReadProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(12)]
    public async Task CancellationHistoryReadsRetainClosedReceiptsAndRevalidateTheNextAdmission(int epochs)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootJournalFixtureAsync(catalog, true, true, async (store, members) =>
        {
            var time = members.Max(member => member.RegisteredAt).AddMilliseconds(10);
            for (int epoch = 0; epoch < epochs; epoch++)
                foreach (var member in members)
                {
                    await store.RecordNamespaceBirthProtectionAsync(await LocatedBornEpochAsync(store, member, time));
                    time = time.AddMilliseconds(10);
                }
        });
        MirrorPulseRootLabelUnsealedCancellation last = null!;
        for (int index = 0; index < epochs; index++)
        {
            if (index != 0)
            {
                var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Renamed", "Docs",
                    setup.Rename.Proof! with { CapturedAt = setup.Rename.Proof.CapturedAt.AddSeconds(10) });
                setup = setup with
                {
                    Rename = rename,
                    Move = setup.Move with { OperationId = rename.Intent.OperationId, PreparedAt = rename.Proof!.CapturedAt.AddSeconds(1) }
                };
            }
            last = await UnsealedCancellationAsync(catalog, setup);
            await catalog.CancelUnsealedRootLabelAsync(last);
        }
        var receipt = await MeasureRegisteredBirthReadAsync(catalog,
            () => catalog.ReadUnsealedRootLabelCancellationAsync(last.Observation.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(last), JsonSerializer.Serialize(receipt.Result));
        var fences = await MeasureRegisteredBirthReadAsync(catalog, () => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsEmpty(fences.Result);
        Console.WriteLine("CiCancellationHistorySql: " + JsonSerializer.Serialize(new
        {
            epochs,
            members = setup.Members.Length,
            receiptSelects = receipt.Selects,
            fenceSelects = fences.Selects,
            nativeExecuted = false,
            publicDeadlineSpeedupEstablished = false
        }));
        // Budget the fixed three-member fixture without granting native authority.
        Assert.IsLessThanOrEqualTo(70 * epochs + 100, receipt.Selects);
        Assert.IsLessThanOrEqualTo(70 * epochs + 100, fences.Selects);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_birth_protections SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadUnsealedRootLabelCancellationAsync(last.Observation.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
