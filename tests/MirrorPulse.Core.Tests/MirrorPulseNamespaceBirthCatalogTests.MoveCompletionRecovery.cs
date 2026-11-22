using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("operation")]
    [DataRow("identity")]
    [DataRow("remote")]
    [DataRow("path")]
    [DataRow("kind")]
    [DataRow("child")]
    [DataRow("parent")]
    [DataRow("time")]
    [DataRow("revision")]
    [DataRow("missing-native")]
    [DataRow("missing-projection")]
    [DataRow("missing-start")]
    public async Task CompletedMoveReadRevalidatesHistoricalReferencesEvenWithACanonicalValidHash(string damage)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveCompletion completion;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
            var native = MoveObservation(setup); var projection = MoveProjection(native);
            await catalog.RecordNamespaceMemberMoveObservationAsync(native);
            await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
            completion = MoveCompletion(native, projection);
            await catalog.CompleteNamespaceMemberMoveAsync(completion);
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            if (damage.StartsWith("missing-", StringComparison.Ordinal))
                update.CommandText = damage switch
                {
                    "missing-native" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_move_observations;",
                    "missing-projection" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_move_projections;",
                    _ => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_move_starts;"
                };
            else
            {
                var altered = damage switch
                {
                    "operation" => completion with { OperationId = Guid.NewGuid() },
                    "identity" => completion with { ItemId = Guid.NewGuid() },
                    "remote" => completion with { RemoteId = "unrelated" },
                    "path" => completion with { RelativePath = setup.Move.Source.RelativePath },
                    "kind" => completion with { IsDirectory = true },
                    "child" => completion with { Child = completion.Child with { LocalObject = completion.Parent.LocalObject } },
                    "parent" => completion with { Parent = completion.Parent with { Dacl = setup.Parent.OriginalDacl } },
                    "time" => completion with { VerifiedAt = completion.Child.ObservedAt.AddTicks(-1) },
                    _ => completion with { OfficialRemoteRevision = "unproved" }
                };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(altered, BirthProtectionJsonOptions);
                update.CommandText = "UPDATE namespace_member_move_completions SET payload=$payload,fingerprint=$hash;";
                update.Parameters.AddWithValue("$payload", bytes); update.Parameters.AddWithValue("$hash", SHA256.HashData(bytes));
            }
            await update.ExecuteNonQueryAsync();
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberMoveCompletionAsync(completion.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberChildAsync(setup.Move.Target.ParentMemberId, "moved.txt"));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task CompletedMoveCannotAuthorizeARewoundHistoricalNameIndex()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        var completion = MoveCompletion(native, projection);
        await catalog.CompleteNamespaceMemberMoveAsync(completion);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_names SET child_name_key='NEW.TXT' WHERE child_name_key='MOVED.TXT';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(completion, await catalog.ReadNamespaceMemberMoveCompletionAsync(completion.OperationId));
    }
}
