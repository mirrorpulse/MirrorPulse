using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("namespace_birth_plans")]
    [DataRow("namespace_birth_starts")]
    [DataRow("namespace_birth_conversions")]
    public async Task OneCoherentProtectionReadLoadsEachValidatedBirthArtifactOnce(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        int reads = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.Contains("FROM " + table + " WHERE operation_id=", StringComparison.Ordinal)) reads++;
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId)); }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        Console.WriteLine($"ImmutableBirthArtifactRead: table={table}; reads={reads}; nativeExecuted=False.");
        Assert.AreEqual(1, reads, "The same immutable positive row should be loaded once in a coherent read transaction.");
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("namespace_birth_plans")]
    [DataRow("namespace_birth_starts")]
    [DataRow("namespace_birth_conversions")]
    public async Task BirthArtifactReuseCannotHideFingerprintDamageInTheNextTransaction(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Member.OriginId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
