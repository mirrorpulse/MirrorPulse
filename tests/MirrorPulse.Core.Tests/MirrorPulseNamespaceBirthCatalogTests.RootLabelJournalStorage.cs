using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("status-completed")]
    [DataRow("status-pending")]
    [DataRow("root-index")]
    [DataRow("member-index")]
    [DataRow("label-index")]
    [DataRow("start-index")]
    [DataRow("noncanonical-payload")]
    [DataRow("rehashed-foreign-item")]
    public async Task RootLabelJournalCorruptionCannotDisappearFromPendingDiscoveryOrBecomeAnAckReceipt(string corruption)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await PrepareRootLabelJournalStartAsync(catalog);
            if (corruption == "status-pending")
                await catalog.CompleteRootLabelJournalAcknowledgementAsync(original, original.StartedAt.AddSeconds(1));
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Simulate persisted corruption, including rows ordinary FK-checked writes refuse.
            command.CommandText = "PRAGMA foreign_keys=OFF;";
            await command.ExecuteNonQueryAsync();
            command.Parameters.AddWithValue("$id", original.Observation.OperationId.ToString("D"));
            string column, value;
            switch (corruption)
            {
                case "fingerprint": column = "fingerprint"; value = "zeroblob(32)"; break;
                case "status-completed": column = "status"; value = "1"; break;
                case "status-pending": column = "status"; value = "0"; break;
                case "root-index": column = "root_id"; value = "$foreign"; command.Parameters.AddWithValue("$foreign", Guid.NewGuid().ToString("D")); break;
                case "member-index": column = "member_id"; value = "$foreign"; command.Parameters.AddWithValue("$foreign", Guid.NewGuid().ToString("D")); break;
                case "label-index": column = "label_operation_id"; value = "$foreign"; command.Parameters.AddWithValue("$foreign", Guid.NewGuid().ToString("D")); break;
                case "start-index": column = "started_utc"; value = "'2000-01-01T00:00:00.0000000+00:00'"; break;
                case "noncanonical-payload":
                case "rehashed-foreign-item":
                    command.CommandText = "SELECT payload FROM root_label_journal_acknowledgements WHERE operation_id=$id;";
                    byte[] payload = (byte[])(await command.ExecuteScalarAsync())!;
                    if (corruption == "noncanonical-payload") payload = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(payload));
                    else
                    {
                        string foreign = Guid.NewGuid().ToString("D");
                        // Retain every canonical byte outside the two original item-ID fields.
                        payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)
                            .Replace(original.Witness.ItemId.ToString("D"), foreign, StringComparison.Ordinal));
                    }
                    command.Parameters.AddWithValue("$payload", payload);
                    command.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                    column = "payload"; value = "$payload,fingerprint=$hash"; break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            command.CommandText = $"UPDATE root_label_journal_acknowledgements SET {column}={value} WHERE operation_id=$id;";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        InvalidDataException failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        if (corruption == "rehashed-foreign-item")
            StringAssert.Contains(failure.Message, "immutable Label, member and complete subtree facts");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadPendingRootLabelJournalAcknowledgementsAsync(limit: 1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.CompleteRootLabelJournalAcknowledgementAsync(original, original.StartedAt.AddDays(1)));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("status-index")]
    [DataRow("payload-index")]
    [DataRow("unique-index")]
    [DataRow("changed-index")]
    [DataRow("downgraded-nonempty")]
    public async Task RootLabelJournalSchemaLossIsRefusedWithoutRepairingOrReattributingAnOriginal(string loss)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            original = await PrepareRootLabelJournalStartAsync(catalog);
        string sql = loss switch
        {
            "table" => "DROP TABLE root_label_journal_acknowledgements;",
            "status-index" => "DROP INDEX root_label_journal_pending_status;",
            "payload-index" => "DROP INDEX root_label_journal_pending_payload;",
            "unique-index" => "DROP INDEX root_label_journal_pending_root;",
            "changed-index" => "DROP INDEX root_label_journal_pending_status; CREATE INDEX root_label_journal_pending_status ON root_label_journal_acknowledgements(root_id);",
            "downgraded-nonempty" => "PRAGMA user_version=89;",
            _ => throw new ArgumentOutOfRangeException(nameof(loss)),
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(loss == "downgraded-nonempty" ? 89L : 90L, await command.ExecuteScalarAsync());
        if (loss != "table")
        {
            command.CommandText = "SELECT operation_id FROM root_label_journal_acknowledgements;";
            Assert.AreEqual(original.Observation.OperationId.ToString("D"), await command.ExecuteScalarAsync());
        }
    }

    [TestMethod]
    public async Task RootLabelJournalEmptyInterruptedMigrationRetainsTheOriginalCatalogAndCreatesNoOwners()
    {
        using var fixture = new Fixture();
        Guid member;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRootSubtreeFixtureAsync(catalog);
            member = setup.Root.MemberId;
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=89;");
        await using var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNotNull(await migrated.ReadNamespaceMemberAsync(member));
        Assert.IsEmpty(await migrated.ReadPendingRootLabelJournalAcknowledgementsAsync());
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(90L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM root_label_journal_acknowledgements;";
        Assert.AreEqual(0L, await command.ExecuteScalarAsync());
    }

    private static async Task<MirrorPulseRootLabelJournalAcknowledgement> PrepareRootLabelJournalStartAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
        var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
        DateTimeOffset audit = receipt.Root.VerifiedAt.AddSeconds(3);
        return await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
            audit, RootLabelNativeIdentity(setup, audit), RootLabelObservation(setup, audit), audit.AddSeconds(1));
    }

    [TestMethod]
    [DataRow("Later")]
    [DataRow("Docs")]
    public async Task RootLabelJournalLateOriginalUsesTheFreshLocalPolicyWitnessWithoutClaimingRenameCausality(string latestName)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement first, second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRootSubtreeFixtureAsync(catalog);
            var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
            var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
            DateTimeOffset audit = receipt.Root.VerifiedAt.AddSeconds(3);
            first = await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
                audit, RootLabelNativeIdentity(setup, audit), RootLabelObservation(setup, audit), audit.AddSeconds(1));
            first = await catalog.CompleteRootLabelJournalAcknowledgementAsync(first, first.StartedAt.AddSeconds(1));

            var proof = setup.Rename.Proof! with { CapturedAt = first.AcknowledgedAt!.Value.AddSeconds(3) };
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Registration.RootId, latestName, "Renamed", proof);
            var next = setup with
            {
                Rename = rename,
                Move = setup.Move with
                {
                    OperationId = rename.Intent.OperationId,
                    Source = setup.Move.Target,
                    Target = setup.Move.Target with { RelativePath = latestName },
                    PreparedAt = proof.CapturedAt.AddSeconds(1),
                    DirectoryMoveEvidence = proof.DirectoryMoveEvidence,
                    PreviousMoveOperationId = setup.Move.OperationId,
                    SourcePositionOperationId = setup.Move.OperationId,
                },
            };
            var nextReceipt = await PrepareRootSubtreeFactsAsync(catalog, next);
            await catalog.CompleteNamespaceDirectoryMoveAsync(nextReceipt);
            var current = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
            DateTimeOffset freshAudit = nextReceipt.Root.VerifiedAt.AddSeconds(3);
            var late = first.Observation with { OperationId = Guid.NewGuid(), Sequence = first.Observation.Sequence + 1 };
            second = await catalog.StartRootLabelJournalAcknowledgementAsync(current, root.Context.Protection.RoleSid,
                freshAudit, RootLabelNativeIdentity(next, freshAudit), late, freshAudit.AddSeconds(1));
            Assert.AreEqual(first.Observation.PreviousRelativePath, second.Observation.PreviousRelativePath);
            Assert.AreEqual(first.Observation.RelativePath, second.Observation.RelativePath);
            Assert.AreEqual(first.Observation.ObservedAt, second.Observation.ObservedAt);
            Assert.AreEqual(rename.Intent.OperationId, second.Witness.LabelOperationId);
            Assert.AreNotEqual(first.Witness.LabelOperationId, second.Witness.LabelOperationId);
            Assert.AreEqual(latestName, second.Witness.DirectoryName);
            Assert.AreEqual(freshAudit, second.Witness.RootObservedAt);
            Assert.AreEqual(current.Fingerprint, second.Witness.InventoryFingerprint);
            Assert.AreEqual(first, await catalog.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(first, await reopened.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
        Assert.AreEqual(second, (await reopened.ReadPendingRootLabelJournalAcknowledgementsAsync(limit: 1)).Single());
        var completed = await reopened.CompleteRootLabelJournalAcknowledgementAsync(second, second.StartedAt.AddSeconds(1));
        Assert.AreEqual(second with { AcknowledgedAt = second.StartedAt.AddSeconds(1) }, completed);
        Assert.AreEqual(first, await reopened.CompleteRootLabelJournalAcknowledgementAsync(first with { AcknowledgedAt = null }, completed.AcknowledgedAt!.Value));
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }
}
