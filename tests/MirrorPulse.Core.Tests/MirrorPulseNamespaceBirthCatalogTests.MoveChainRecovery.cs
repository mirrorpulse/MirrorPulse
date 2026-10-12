using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("predecessor-index")]
    [DataRow("missing-completion")]
    [DataRow("missing-native")]
    [DataRow("damaged-ancestor")]
    [DataRow("source")]
    [DataRow("unlinked")]
    [DataRow("rewound-name")]
    [DataRow("cycle")]
    public async Task ReopenedCurrentPositionRejectsDamagedHistoryInsteadOfSelectingAnArbitraryRow(string damage)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent second;
        MirrorPulseNamespaceMemberMoveCompletion first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog); first = await CompleteMoveChainStepAsync(catalog, setup.Move);
            second = ContinueMove(setup.Move, first, setup.Move.Target, "Docs/second.txt");
            await CompleteMoveChainStepAsync(catalog, second);
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            update.Parameters.AddWithValue("$first", setup.Move.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$second", second.OperationId.ToString("D"));
            update.CommandText = damage switch
            {
                "predecessor-index" => "UPDATE namespace_member_moves SET previous_move_id=NULL WHERE operation_id=$second;",
                "missing-completion" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_move_completions WHERE operation_id=$first;",
                "missing-native" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_move_observations WHERE operation_id=$first;",
                "damaged-ancestor" => "UPDATE namespace_member_move_projections SET fingerprint=zeroblob(32) WHERE operation_id=$first;",
                "rewound-name" => "UPDATE namespace_member_names SET child_name_key='MOVED.TXT' WHERE child_name_key='SECOND.TXT';",
                _ => "UPDATE namespace_member_moves SET payload=$payload,fingerprint=$hash,previous_move_id=$previous WHERE operation_id=$second;"
            };
            if (damage is "source" or "unlinked" or "cycle")
            {
                var changed = damage switch
                {
                    "source" => second with { Source = second.Source with { RelativePath = "Docs/other.txt" } },
                    "cycle" => second with { PreviousMoveOperationId = second.OperationId },
                    _ => second with { PreviousMoveOperationId = null, Source = setup.Move.Source }
                };
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed, BirthProtectionJsonOptions);
                update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                update.Parameters.AddWithValue("$previous", (object?)changed.PreviousMoveOperationId?.ToString("D") ?? DBNull.Value);
            }
            await update.ExecuteNonQueryAsync();
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        if (damage == "rewound-name")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberChildAsync(second.Source.ParentMemberId, "moved.txt"));
            Assert.AreEqual(first, await reopened.ReadNamespaceMemberMoveCompletionAsync(first.OperationId));
        }
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberChildAsync(second.Target.ParentMemberId, "second.txt"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Schema38UpgradeRetainsFirstMoveBytesWithoutInventingAPredecessor(bool completed)
    {
        using var fixture = new Fixture(); MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (completed) await CompleteMoveChainStepAsync(catalog, setup.Move);
            else await StartMoveAsync(catalog, setup);
        }
        string previous = await ReadMoveChainMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX ux_namespace_move_previous; ALTER TABLE namespace_member_moves DROP COLUMN previous_move_id; PRAGMA user_version=38;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(previous, await ReadMoveChainMigrationBytesAsync(fixture.Paths));
        Assert.AreEqual(setup.Move, await current.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        var location = await current.ReadNamespaceMemberLocationAsync(setup.Member.MemberId);
        Assert.AreEqual(completed ? setup.Move.OperationId : (Guid?)null, location.CompletedMoveOperationId);
        Assert.AreEqual(completed ? setup.Move.Target.RelativePath : setup.Move.Source.RelativePath, location.RelativePath);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM namespace_member_moves WHERE previous_move_id IS NOT NULL;";
        Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(39L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA foreign_key_check;"; Assert.IsNull(await query.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task ConcurrentRequestsAdmitOneSuccessorAndCancellationPreservesTheCommittedPosition()
    {
        using var fixture = new Fixture(); await using var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(first); var completion = await CompleteMoveChainStepAsync(first, setup.Move);
        var next = ContinueMove(setup.Move, completion, setup.Move.Target, "Docs/next.txt");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first.PrepareNamespaceMemberMoveAsync(next, cancel.Token));
        var other = next with { OperationId = Guid.NewGuid(), Target = next.Target with { RelativePath = "Docs/other.txt" } };
        var outcomes = await Task.WhenAll(AdmitAsync(first, next), AdmitAsync(first, other));
        Assert.AreEqual(1, outcomes.Count(value => value));
        var pending = await first.ReadPendingNamespaceMemberMoveAsync(next.RootId);
        Assert.AreEqual(outcomes[0] ? next : other, pending);
        Assert.AreEqual(setup.Move.OperationId, (await first.ReadNamespaceMemberLocationAsync(next.MemberId)).CompletedMoveOperationId);

        static async Task<bool> AdmitAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceMemberMoveIntent intent)
        {
            try { await catalog.PrepareNamespaceMemberMoveAsync(intent); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static async Task<string> ReadMoveChainMigrationBytesAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT 'intent:'||hex(payload)||':'||hex(fingerprint) FROM namespace_member_moves
            UNION ALL SELECT 'start:'||hex(payload)||':'||hex(fingerprint) FROM namespace_member_move_starts
            UNION ALL SELECT 'completion:'||hex(payload)||':'||hex(fingerprint) FROM namespace_member_move_completions
            UNION ALL SELECT 'member:'||hex(payload)||':'||hex(fingerprint) FROM namespace_members;
            """;
        await using var reader = await query.ExecuteReaderAsync(); var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return string.Join('\n', rows.Order(StringComparer.Ordinal));
    }
}
