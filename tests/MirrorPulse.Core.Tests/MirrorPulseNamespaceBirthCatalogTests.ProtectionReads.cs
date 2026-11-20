using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task AProtectionSnapshotReadsEachImmutableBirthEpochOnceInItsTransaction()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var handle = connection.Handle ?? throw new InvalidOperationException("The fixture catalog is not open.");
        int allStatements = 0, epochReads = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            Interlocked.Increment(ref allStatements);
            if (sql.Contains("FROM namespace_birth_protections WHERE protection_id=", StringComparison.Ordinal)) Interlocked.Increment(ref epochReads);
        };
        SQLitePCL.raw.sqlite3_trace(handle, trace, null);
        try
        {
            var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId);
            Assert.IsNotNull(snapshot); Assert.AreEqual(setup.Born.Protection.ProtectionId, snapshot.Context.Protection.ProtectionId);
        }
        finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Console.WriteLine($"ImmutableBirthEpochReadBudget: actualSqlStatements={allStatements}; protectionRowReads={epochReads}; expectedDistinctProtectionRows=1; nativeExecution=False.");
        Assert.IsGreaterThan(0, allStatements);
        Assert.AreEqual(1, epochReads, "Repeated lineage checks in the same SQLite transaction must reuse the already verified immutable row.");
    }

    [TestMethod]
    public async Task ANewTransactionObservesAnAppendedProtectionHeadWithoutChangingTheFirstEpoch()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var first = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!;
        var next = setup.Born.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = setup.Born.Protection.ProtectionId,
            Verification = setup.Born.Protection.Verification with { ObservedAt = setup.Member.RegisteredAt.AddSeconds(10) }
        };
        await catalog.RecordNamespaceBirthProtectionAsync(next);
        var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!;
        Assert.AreEqual(next.ProtectionId, current.Context.Protection.ProtectionId);
        Assert.AreEqual(setup.Born.Protection.ProtectionId, first.Context.Protection.ProtectionId);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("namespace_birth_protections")]
    [DataRow("namespace_birth_intents")]
    [DataRow("namespace_birth_observations")]
    public async Task ReusingAnImmutableProtectionRowCannotHideLaterCorruptionOfItsEpochOrReferences(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }
}
