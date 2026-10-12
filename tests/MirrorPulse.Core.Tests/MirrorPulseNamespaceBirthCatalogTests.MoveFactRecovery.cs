using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private static readonly JsonSerializerOptions IndentedBirthProtectionJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MoveFactInsertionFailureRollsBackWithoutChangingNamesOrDiscardingPendingRecovery(bool projection)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup);
        if (projection) await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        string table = projection ? "namespace_member_move_projections" : "namespace_member_move_observations";
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER reject_move_fact BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'controlled fact interruption'); END;");
        if (projection)
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.RecordNamespaceMemberMoveProjectionAsync(MoveProjection(native)));
        else
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(native));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        Assert.AreEqual(projection ? native : null, await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_move_fact;");
        if (projection) await catalog.RecordNamespaceMemberMoveProjectionAsync(MoveProjection(native));
        else await catalog.RecordNamespaceMemberMoveObservationAsync(native);
    }

    [TestMethod]
    [DataRow("native-hash")]
    [DataRow("projection-hash")]
    [DataRow("native-canonical")]
    [DataRow("native-operation")]
    [DataRow("native-parent")]
    [DataRow("projection-time")]
    public async Task DamagedMoveFactsCannotBeRepairedFromPresentNameOrAnOtherwiseValidFingerprint(string damage)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveObservation native;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first); await StartMoveAsync(first, setup); native = MoveObservation(setup);
            await first.RecordNamespaceMemberMoveObservationAsync(native); await first.RecordNamespaceMemberMoveProjectionAsync(MoveProjection(native));
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            if (damage.EndsWith("hash", StringComparison.Ordinal))
                update.CommandText = damage == "native-hash"
                    ? "UPDATE namespace_member_move_observations SET fingerprint=zeroblob(32);"
                    : "UPDATE namespace_member_move_projections SET fingerprint=zeroblob(32);";
            else
            {
                byte[] payload = damage switch
                {
                    "native-canonical" => JsonSerializer.SerializeToUtf8Bytes(native, IndentedBirthProtectionJsonOptions),
                    "native-operation" => JsonSerializer.SerializeToUtf8Bytes(native with { OperationId = Guid.NewGuid() }, BirthProtectionJsonOptions),
                    "native-parent" => JsonSerializer.SerializeToUtf8Bytes(native with { Parent = native.Parent with { LocalObject = native.Child.LocalObject } }, BirthProtectionJsonOptions),
                    _ => JsonSerializer.SerializeToUtf8Bytes(MoveProjection(native) with { ObservedAt = native.Parent.ObservedAt.AddTicks(-1) }, BirthProtectionJsonOptions)
                };
                update.CommandText = damage == "projection-time"
                    ? "UPDATE namespace_member_move_projections SET payload=$payload,fingerprint=$hash;"
                    : "UPDATE namespace_member_move_observations SET payload=$payload,fingerprint=$hash;";
                update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            }
            await update.ExecuteNonQueryAsync();
        }
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await current.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await current.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }
}
