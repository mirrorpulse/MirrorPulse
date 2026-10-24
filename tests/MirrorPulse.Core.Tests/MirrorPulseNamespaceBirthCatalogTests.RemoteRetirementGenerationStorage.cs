using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteRetirementGenerationRequiresItsActualSettledFileOrEmptyDirectoryName(bool directory)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var selection = await PrepareOrdinaryRemoteGenerationAsync(catalog, directory);
        string original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        Assert.AreEqual(selection, await catalog.SelectRemoteIdentityGenerationAsync(selection));
        Assert.AreEqual(selection, await catalog.SelectRemoteIdentityGenerationAsync(selection));
        Assert.AreEqual(selection, await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.HasCount(0, await catalog.ReadNamespaceTransferSourceGenerationsAsync(selection.PreviousItemId));
        Assert.HasCount(1, await catalog.ReadNamespaceRemoteRetirementGenerationsAsync(selection.PreviousItemId));
        Assert.HasCount(0, await catalog.ReadNamespaceRemoteRetirementGenerationsAsync(selection.ItemId));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("instance")]
    [DataRow("root-key")]
    [DataRow("previous-item")]
    [DataRow("previous-address")]
    [DataRow("retirement")]
    [DataRow("epoch")]
    [DataRow("before-outcome")]
    [DataRow("transfer")]
    [DataRow("kind")]
    public async Task OrdinaryRemoteGenerationCannotBorrowForeignIdentityEpochOrTransferAuthority(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var good = await PrepareOrdinaryRemoteGenerationAsync(catalog, false);
        var bad = mismatch switch
        {
            "root" => good with { RootId = MirrorPulse.Core.Contracts.RootId.New() },
            "instance" => good with { InstanceId = MirrorPulse.Core.Contracts.InstanceId.New() },
            "root-key" => good with { RootKey = "foreign" },
            "previous-item" => good with { PreviousItemId = Guid.NewGuid() },
            "previous-address" => good with { PreviousRemoteId = "foreign" },
            "retirement" => good with { RetirementOperationId = Guid.NewGuid() },
            "epoch" => good with { RetiredNameEpoch = good.RetiredNameEpoch + 1 },
            "before-outcome" => good with { SelectedAt = good.SelectedAt.AddDays(-1) },
            "transfer" => good with { TransferOperationId = Guid.NewGuid() },
            _ => good with { Version = 1 }
        };
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        if (mismatch is "transfer" or "kind")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.SelectRemoteIdentityGenerationAsync(bad));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(bad));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(good.RootId, good.BaseItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task OrdinaryRemoteGenerationSelectionAndCurrentPointerRollbackTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var good = await PrepareOrdinaryRemoteGenerationAsync(catalog, false);
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_remote_generation BEFORE INSERT ON remote_identity_generations BEGIN SELECT RAISE(ABORT,'interrupted pointer'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SelectRemoteIdentityGenerationAsync(good));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(good.RootId, good.BaseItemId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM remote_identity_generation_history;"; Assert.AreEqual(0L, await count.ExecuteScalarAsync());
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_remote_generation;");
        Assert.AreEqual(good, await catalog.SelectRemoteIdentityGenerationAsync(good));
    }

    [TestMethod]
    public async Task SelectedOrdinaryRemoteGenerationCannotOutliveItsOriginalSdkOutcome()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var good = await PrepareOrdinaryRemoteGenerationAsync(catalog, false);
        await catalog.SelectRemoteIdentityGenerationAsync(good);
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_remote_retirement_outcomes;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRemoteIdentityGenerationAsync(good.RootId, good.BaseItemId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SelectRemoteIdentityGenerationAsync(good));
    }

    private static async Task<MirrorPulseRemoteIdentityGeneration> PrepareOrdinaryRemoteGenerationAsync(MirrorPulseProductCatalog catalog, bool directory)
    {
        var setup = await PrepareMovableMemberAsync(catalog, directory);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        intent = intent with { RemoteEntry = intent.RemoteEntry! with { Version = 2, RootKey = "docs" } };
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        var completed = await catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(intent, false));
        return new(2, intent.RootId, intent.RemoteEntry.InstanceId, intent.RemoteEntry.RootKey!, intent.RemoteEntry.ItemId,
            intent.RemoteEntry.RemoteId, null, Guid.NewGuid(), intent.RemoteEntry.ItemId, intent.RemoteEntry.RemoteId,
            Guid.NewGuid(), "opaque-remote-successor", Guid.Empty, intent.OperationId, completed.RemoteSourceNameEpoch!.Value,
            completed.CompletedAt!.Value.AddSeconds(1));
    }
}
