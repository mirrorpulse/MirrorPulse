using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("batch")]
    [DataRow("invocation")]
    [DataRow("owner")]
    public async Task OrphanedOriginalRemoteTreeEvidenceCannotBeReturnedOrReleaseDispatchFences(string lost)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (batch, _, _) = await PrepareTreeSettlementAsync(first, 1);
            Assert.IsNotNull(await first.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        }
        string table = lost switch
        {
            "batch" => "namespace_member_retirement_batches",
            "invocation" => "namespace_remote_retirement_tree_starts",
            _ => "namespace_remote_retirement_tree_settlements"
        };
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM " + table + ";");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task OriginalRemoteTreeCannotSettleMembersBeforeItsActualSdkRootResult()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, candidate) = await PrepareTreeSettlementAsync(catalog, 1, recordOutcome: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            [TreeMemberSettlement(batch, candidate, entries[1])]));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeMemberSettlementAsync(batch.OperationId, 1));
        Assert.AreEqual(1, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.NextOrdinal);
    }

    [TestMethod]
    public async Task OriginalRemoteTreeCannotRemoveAParentWhileAnOriginalDescendantIsRetained()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", true));
        var nestedMember = await catalog.RegisterNamespaceMemberAsync(BornMember(nested, setup.Member));
        var leaf = await AddBornAsync(catalog, NestedBirth(nested, "retained.txt", false));
        await catalog.RegisterNamespaceMemberAsync(BornMember(leaf, nestedMember));
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        var entries = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>();
        foreach (var node in await catalog.ReadNamespaceMemberTreeAsync(setup.Member.MemberId))
        {
            var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(node.MemberId))!;
            entries.Add(new(entries.Count, node.Depth, snapshot, entries.Count == 0 ? intent.RemoteEntry!.NativeRemoteRevision : null,
                entries.Count == 0 ? intent.RemoteEntry!.OfficialRemoteRevision : null));
        }
        DateTimeOffset prepared = entries.Max(entry => entry.Original.Context.Verification.ObservedAt).AddSeconds(1);
        if (prepared < intent.PreparedAt) prepared = intent.PreparedAt;
        var batch = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(intent.OperationId, intent.RemoteEntry!, entries, prepared);
        var start = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(batch.OperationId, prepared);
        var outcome = TreeConflictOutcome(batch, TreeAppliedOutcome(batch, start));
        await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome);
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, [TreeMemberSettlement(batch, outcome, entries[2], missing: false)]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            [TreeMemberSettlement(batch, outcome, entries[1])]));
        Assert.AreEqual(1, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.NextOrdinal);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberLocationAsync(nestedMember.MemberId));
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            [TreeMemberSettlement(batch, outcome, entries[1], missing: false), TreeMemberSettlement(batch, outcome, entries[0], missing: false)]);
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5));
        var current = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(batch.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value);
        Assert.IsNull(current.RetiredTreeMembers); Assert.HasCount(5, current.Members); Assert.IsEmpty(current.FencedRoots);
    }

    [TestMethod]
    [DataRow("outcome")]
    [DataRow("page")]
    [DataRow("completion")]
    public async Task ForeignOwnerStillFencesEveryOriginalRemoteTreeSettlementBoundary(string boundary)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, outcome) = await PrepareTreeSettlementAsync(catalog, 1, recordOutcome: boundary != "outcome");
        var members = entries.Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray();
        if (boundary == "completion") await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, members);
        var foreign = new MirrorPulseNamespaceRetirementBatch(1, Guid.NewGuid(), batch.MemberId, batch.RootId, batch.Entries, batch.PreparedAt);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(foreign, TreeStartFixtureOptions);
        await ExecuteSqlAsync(fixture.Paths, "INSERT INTO namespace_member_retirement_batches(operation_id,member_id,root_id,payload,fingerprint,phase) VALUES('" +
            foreign.OperationId.ToString("D") + "','" + foreign.MemberId.ToString("D") + "','" + foreign.RootId + "',X'" + Convert.ToHexString(payload) +
            "',X'" + Convert.ToHexString(SHA256.HashData(payload)) + "',0);");
        if (boundary == "outcome") await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome));
        else if (boundary == "page") await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, members));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId,
            TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5)));
        var progress = (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!;
        Assert.IsNull(progress.CompletedAt); Assert.AreEqual(boundary == "completion" ? -1 : 1, progress.NextOrdinal);
    }

    [TestMethod]
    public async Task Schema72SettlementUpgradeCannotInventAnOwnerForCorruptOriginalStartHistory()
    {
        using var fixture = new Fixture(); Guid operation;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (batch, _, _) = await PrepareTreeSettlementAsync(first, 1, recordOutcome: false); operation = batch.OperationId;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_retirement_tree_member_settlements; DROP TABLE namespace_remote_retirement_tree_outcomes; DROP TABLE namespace_remote_retirement_tree_settlements; UPDATE namespace_remote_retirement_tree_starts SET fingerprint=zeroblob(32); PRAGMA user_version=72;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(72L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT COUNT(*) FROM namespace_remote_retirement_tree_settlements WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operation.ToString("D")); Assert.AreEqual(0L, await query.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task CurrentInventoryWithoutTreeRetirementKeepsThePriorSerializationShape()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.ParentMemberId!.Value);
        Assert.IsNull(inventory.RetiredTreeMembers);
        Assert.IsFalse(JsonSerializer.Serialize(inventory, TreeStartFixtureOptions).Contains("RetiredTreeMembers", StringComparison.Ordinal));
    }
}
