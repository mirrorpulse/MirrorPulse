using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceDirectoryMovePublicOutcome.Projected)]
    [DataRow(MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected)]
    public async Task DirectorySettlementCanReconcileAgainAfterItsImmutableRootProjection(MirrorPulseNamespaceDirectoryMovePublicOutcome outcome)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        var publicTime = setup.Completion.Entries[0].Projection.ObservedAt.AddMilliseconds(1);
        var entries = setup.Completion.Entries.Select(entry => entry.MemberId == setup.Move.Member.MemberId ? entry : entry with
        {
            Projection = entry.Projection with { ObservedAt = publicTime.AddMilliseconds(1) },
        }).ToArray();
        var recovery = setup.Completion with
        {
            PublicResult = setup.Completion.PublicResult with
            {
                Outcome = outcome,
                ObservedAt = publicTime,
                DurableStateEntriesUpdated = outcome == MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected ? 0 : entries.Length
            },
            Entries = entries
        };
        var tooEarly = entries.ToArray();
        tooEarly[0] = tooEarly[0] with
        {
            Completion = recovery.Root with
            {
                Child = recovery.Root.Child with { ObservedAt = publicTime }
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(
            recovery with { Root = tooEarly[0].Completion, Entries = tooEarly }));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
        await catalog.CompleteNamespaceDirectoryMoveAsync(recovery);
        Assert.AreEqual(setup.Completion.Entries[0].Projection, await catalog.ReadNamespaceMemberMoveProjectionAsync(recovery.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(recovery.OperationId));
    }

    [TestMethod]
    public async Task DirectorySettlementCanExplicitlyAnchorALegacyCompletedChildWithoutBackfill()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        var legacyCompletion = await catalog.ReadNamespaceMemberMoveCompletionAsync(setup.ChildMoveId!.Value);
        // Model a retained direct completion predating position facts; preserve its original bytes.
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_member_positions WHERE operation_id='{setup.ChildMoveId:D}';");
        var entries = setup.Completion.Entries.Select(entry => entry.MemberId == setup.Child.MemberId
            ? entry with { LegacyAnchor = true } : entry).ToArray();
        var receipt = setup.Completion with { Entries = entries };
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
        Assert.IsNull(await catalog.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, setup.ChildMoveId.Value));
        var position = (await catalog.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, receipt.OperationId))!;
        Assert.IsTrue(position.LegacyAnchor);
        Assert.AreEqual(setup.ChildMoveId, position.PreviousPositionOperationId);
        Assert.AreEqual(legacyCompletion, await catalog.ReadNamespaceMemberMoveCompletionAsync(setup.ChildMoveId.Value));
        Assert.IsNotNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(receipt.OperationId));
    }

    [TestMethod]
    public async Task RepeatedAncestorMovesAndLaterChildMoveKeepIndependentDirectAndPositionHistories()
    {
        using var fixture = new Fixture();
        DirectorySettlementFixture setup;
        MirrorPulseNamespaceDirectoryMoveCompletion second;
        MirrorPulseNamespaceMemberMoveCompletion childMove;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareDirectorySettlementAsync(first, movedChild: true);
            await first.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
            var next = setup.Move with
            {
                Move = setup.Move.Move with
                {
                    OperationId = Guid.NewGuid(),
                    PreviousMoveOperationId = setup.Completion.OperationId,
                    Source = setup.Move.Move.Target,
                    Target = setup.Move.Move.Target with { RelativePath = "Docs/second-directory" },
                    PreparedAt = setup.Completion.Root.VerifiedAt.AddSeconds(1),
                    DirectoryMoveEvidence = "AQ=="
                }
            };
            second = await PrepareDirectoryCompletionFactsAsync(first, next);
            await first.CompleteNamespaceDirectoryMoveAsync(second);
            var childPosition = await first.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, second.OperationId);
            Assert.IsNotNull(childPosition);
            Assert.AreEqual(setup.Completion.OperationId, childPosition.PreviousPositionOperationId);
            Assert.AreEqual(setup.ChildMoveId, childPosition.CompletedMoveOperationId);
            var childProtection = (await first.ReadLatestNamespaceBirthProtectionAsync(setup.Child.OriginId))!;
            var childIdentity = (await first.ReadNamespaceMemberIdentityAsync(setup.Child.MemberId))!;
            var childIntent = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), setup.Child.MemberId,
                childProtection.ProtectionId, setup.Move.Move.RootId, setup.Child.LocalObject, childIdentity.ItemId!.Value,
                childIdentity.RemoteId!, setup.Child.OwnerSid, childProtection.RoleSid,
                new(setup.Move.Member.MemberId, setup.Move.Member.LocalObject, setup.Move.Born.Protection.ProtectionId, childPosition.RelativePath)
                { ParentPositionOperationId = second.OperationId },
                new(setup.Move.ParentMember.MemberId, setup.Move.ParentMember.LocalObject, setup.Move.ParentProof.OperationId, "Docs/child-after-directory.txt"),
                second.Root.VerifiedAt.AddSeconds(1))
            { PreviousMoveOperationId = setup.ChildMoveId, SourcePositionOperationId = second.OperationId };
            childMove = await CompleteMoveChainStepAsync(first, childIntent);
            var newPosition = (await first.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, childMove.OperationId))!;
            Assert.AreEqual(second.OperationId, newPosition.PreviousPositionOperationId);
            Assert.AreEqual(childMove.OperationId, newPosition.CompletedMoveOperationId);
            await first.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
            await first.CompleteNamespaceDirectoryMoveAsync(second);
            Assert.AreEqual(childMove.RelativePath, (await first.ReadNamespaceMemberLocationAsync(setup.Child.MemberId)).RelativePath);
            Assert.HasCount(3, await first.ReadNamespaceMemberTreeAsync(setup.Move.Member.MemberId));
        }
        await using var secondOwner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Completion.Root, await secondOwner.ReadNamespaceMemberMoveCompletionAsync(setup.Completion.OperationId));
        Assert.AreEqual(second.Root, await secondOwner.ReadNamespaceMemberMoveCompletionAsync(second.OperationId));
        Assert.AreEqual(childMove.RelativePath, (await secondOwner.ReadNamespaceMemberLocationAsync(setup.Child.MemberId)).RelativePath);
        Assert.AreEqual("Docs/second-directory/nested/grand.txt",
            (await secondOwner.ReadNamespaceMemberLocationAsync(setup.Members[^1].MemberId)).RelativePath);
        foreach (var member in setup.Members) Assert.AreEqual(member, await secondOwner.ReadNamespaceMemberAsync(member.MemberId));
    }

    [TestMethod]
    [DataRow("member-path")]
    [DataRow("public-result")]
    [DataRow("proof")]
    public async Task DirectoryCompletionRejectsRecomputedFingerprintCorruptionAfterPriorSuccessfulRead(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        Assert.IsNotNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(setup.Completion.OperationId));
        var entries = setup.Completion.Entries.ToArray();
        entries[1] = entries[1] with { Completion = entries[1].Completion with { RelativePath = "Docs/foreign/child.txt" } };
        var corrupt = mismatch switch
        {
            "member-path" => setup.Completion with { Entries = entries },
            "public-result" => setup.Completion with { PublicResult = setup.Completion.PublicResult with { RequiresFullRescan = true } },
            _ => setup.Completion with { PublicEvidenceSha256 = new string('0', 64) }
        };
        await WriteDirectoryCompletionPayloadAsync(fixture.Paths, corrupt);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDirectoryMoveCompletionAsync(corrupt.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberLocationAsync(setup.Child.MemberId));
    }

    [TestMethod]
    [DataRow("missing-position")]
    [DataRow("missing-receipt")]
    [DataRow("forged-legacy")]
    public async Task DirectoryPositionCannotLoseItsWholeTreeReceiptOrInventALegacyPredecessor(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, setup.Completion.OperationId));
        if (mismatch == "missing-receipt")
            await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_directory_move_completions;");
        else if (mismatch == "missing-position")
            await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_member_positions WHERE member_id='{setup.Child.MemberId:D}' AND operation_id='{setup.Completion.OperationId:D}';");
        else
        {
            var position = (await catalog.ReadNamespaceMemberPositionAsync(setup.Child.MemberId, setup.Completion.OperationId))!;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(position with { LegacyAnchor = true });
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_member_positions SET payload=$payload,fingerprint=$hash WHERE member_id=$member AND operation_id=$id; DELETE FROM namespace_member_positions WHERE member_id=$member AND operation_id=$old;";
            update.Parameters.AddWithValue("$member", setup.Child.MemberId.ToString("D"));
            update.Parameters.AddWithValue("$id", setup.Completion.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$old", setup.ChildMoveId!.Value.ToString("D"));
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            Assert.AreEqual(2, await update.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberMoveCompletionAsync(setup.Completion.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberLocationAsync(setup.Child.MemberId));
    }

    private static async Task WriteDirectoryCompletionPayloadAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths,
        MirrorPulseNamespaceDirectoryMoveCompletion completion)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completion);
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE namespace_directory_move_completions SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$id", completion.OperationId.ToString("D"));
        update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
    }
}
