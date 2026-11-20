using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryBirthReadsCheckBothExclusiveOwnerIndexesWithoutSeparateRoundTrips(bool protectionSnapshot)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var birth = (await catalog.ReadNamespaceBirthAsync(setup.Member.OriginId))!;
        Assert.IsNull(birth.RemoteEntry); Assert.IsNull(birth.TransferEntry);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        int combined = 0, separate = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            bool remote = sql.Contains("namespace_remote_birth_entries", StringComparison.Ordinal);
            bool transfer = sql.Contains("namespace_transfer_target_births", StringComparison.Ordinal);
            if (remote && transfer) combined++;
            else if (remote || transfer) separate++;
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try
        {
            if (protectionSnapshot) Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
            else Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        Console.WriteLine($"BirthOwnerIndexWork: protectionSnapshot={protectionSnapshot}; combined={combined}; separate={separate}; nativeExecuted=False.");
        Assert.AreEqual(0, separate, "The same birth's absence from both exclusive owner indexes should use a fresh combined check.");
        Assert.IsGreaterThan(0, combined, "Absence still requires actual database checks, rather than retained negative results.");
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("namespace_remote_birth_entries")]
    [DataRow("namespace_transfer_target_births")]
    public async Task EarlierOwnerAbsenceCannotHideAnUnexpectedOwnerOnTheNextRead(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var original = (await catalog.ReadNamespaceBirthAsync(setup.Member.OriginId))!;
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        string insert = table == "namespace_remote_birth_entries"
            ? $"INSERT INTO namespace_remote_birth_entries VALUES('{original.OperationId:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}','{new string('A', 64)}',1,0,'{Guid.NewGuid():D}','{Guid.NewGuid():D}');"
            : $"INSERT INTO namespace_transfer_target_births VALUES('{Guid.NewGuid():D}','{original.OperationId:D}','{Guid.NewGuid():D}',x'7B7D',zeroblob(32));";
        // Deliberate private test-database corruption, not a valid mutation or a public owner admission.
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + insert);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBirthAsync(original.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        string key = table == "namespace_remote_birth_entries" ? "operation_id" : "birth_id";
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM {table} WHERE {key}='{original.OperationId:D}';");
        Assert.AreEqual(original, await catalog.ReadNamespaceBirthAsync(original.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
