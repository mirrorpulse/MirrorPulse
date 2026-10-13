using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CompletedFileMovesRetainOnePositionChainAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent second;
        MirrorPulseNamespaceMemberPosition firstPosition;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            var completed = await CompleteMoveChainStepAsync(first, setup.Move);
            firstPosition = (await first.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, completed.OperationId))!;
            Assert.AreEqual(completed.VerifiedAt, firstPosition.VerifiedAt);
            Assert.AreEqual(completed.OperationId, firstPosition.CompletedMoveOperationId);
            Assert.IsNull(firstPosition.PreviousPositionOperationId);
            Assert.IsFalse(firstPosition.LegacyAnchor);
            second = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/second.txt");
            await first.PrepareNamespaceMemberMoveAsync(second);
            Assert.IsNull(await first.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, second.OperationId));
            Assert.AreEqual(firstPosition.OperationId, (await first.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var result = await CompleteMoveChainStepAsync(reopened, second);
        var position = await reopened.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, second.OperationId);
        Assert.IsNotNull(position);
        Assert.AreEqual(firstPosition.OperationId, position.PreviousPositionOperationId);
        Assert.AreEqual(result.VerifiedAt, position.VerifiedAt);
        Assert.IsFalse(position.LegacyAnchor);
        Assert.AreEqual(firstPosition, await reopened.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, firstPosition.OperationId));
        Assert.AreEqual(new(setup.Member.MemberId, second.Target.ParentMemberId, second.Target.RelativePath, second.OperationId),
            await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task PositionInsertFailureRollsBackNameCompletionAndFenceRelease()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveCompletion completed;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            await StartMoveAsync(first, setup);
            var native = MoveObservation(setup); var projection = MoveProjection(native);
            await first.RecordNamespaceMemberMoveObservationAsync(native);
            await first.RecordNamespaceMemberMoveProjectionAsync(projection);
            completed = MoveCompletion(native, projection);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_position BEFORE INSERT ON namespace_member_positions BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => first.CompleteNamespaceMemberMoveAsync(completed));
            Assert.IsNull(await first.ReadNamespaceMemberMoveCompletionAsync(completed.OperationId));
            Assert.IsNull(await first.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, completed.OperationId));
            Assert.AreEqual(setup.Move, await first.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_position;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await second.CompleteNamespaceMemberMoveAsync(completed));
        Assert.IsNotNull(await second.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, completed.OperationId));
        Assert.IsNull(await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task PositionSchemaUpgradePreservesLegacyBytesAndAnchorsOnlyTheNextMove()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveCompletion firstCompletion;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            firstCompletion = await CompleteMoveChainStepAsync(first, setup.Move);
        }
        string original = await ReadMoveChainMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_member_positions;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await ReadMoveChainMigrationBytesAsync(fixture.Paths));
        Assert.IsNull(await current.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, firstCompletion.OperationId));
        Assert.AreEqual(firstCompletion.OperationId, (await current.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        var next = ContinueMove(setup.Move, firstCompletion, setup.Move.Target, "Docs/second.txt");
        await CompleteMoveChainStepAsync(current, next);
        var position = await current.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, next.OperationId);
        Assert.IsNotNull(position);
        Assert.IsTrue(position.LegacyAnchor);
        Assert.AreEqual(firstCompletion.OperationId, position.PreviousPositionOperationId);
        Assert.AreEqual(firstCompletion, await current.ReadNamespaceMemberMoveCompletionAsync(firstCompletion.OperationId));
        Assert.IsNull(await current.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, firstCompletion.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("index")]
    [DataRow("path")]
    [DataRow("time")]
    [DataRow("predecessor")]
    [DataRow("direct-head")]
    [DataRow("missing-previous")]
    [DataRow("false-legacy")]
    public async Task CurrentPositionRejectsDamagedSettlementFactsRatherThanFallingBackToAPath(string damage)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent next;
        MirrorPulseNamespaceMemberPosition position;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            var completed = await CompleteMoveChainStepAsync(first, setup.Move);
            next = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/second.txt");
            await CompleteMoveChainStepAsync(first, next);
            position = (await first.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, next.OperationId))!;
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.Parameters.AddWithValue("$member", setup.Member.MemberId.ToString("D"));
            update.Parameters.AddWithValue("$id", next.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$first", setup.Move.OperationId.ToString("D"));
            update.CommandText = damage switch
            {
                "index" => "UPDATE namespace_member_positions SET previous_operation_id=NULL WHERE member_id=$member AND operation_id=$id;",
                "missing-previous" => "DELETE FROM namespace_member_positions WHERE member_id=$member AND operation_id=$first;",
                _ => "UPDATE namespace_member_positions SET payload=$payload,fingerprint=$hash,previous_operation_id=$previous WHERE member_id=$member AND operation_id=$id;"
            };
            if (damage is not ("index" or "missing-previous"))
            {
                var altered = damage switch
                {
                    "path" => position with { RelativePath = "Docs/unproved.txt" },
                    "time" => position with { VerifiedAt = position.VerifiedAt.AddTicks(1) },
                    "direct-head" => position with { CompletedMoveOperationId = setup.Move.OperationId },
                    "false-legacy" => position with { LegacyAnchor = true },
                    _ => position with { PreviousPositionOperationId = null }
                };
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(altered);
                update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                update.Parameters.AddWithValue("$previous", (object?)altered.PreviousPositionOperationId?.ToString("D") ?? DBNull.Value);
            }
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, next.OperationId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }
}
