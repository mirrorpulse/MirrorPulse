using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelPendingPermissionReplay")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task CancellationPendingPermissionInventoryRetainsOriginalIntentAndInitializerWithoutPretendingItIsVerified(bool enabled, bool applied)
    {
        // Actual private SQLite metadata only: no native permission or SDK receipt is asserted.
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseNamespacePermissionIntent intent; MirrorPulseRootLabelCancellationRecoveryOwner owner;
        MirrorPulseNamespaceInitializationStatus initializerBefore;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
            owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId))!;
            initializerBefore = await catalog.ReadNamespaceInitializationStatusAsync(initializer);
            var original = (await catalog.ReadNamespacePermissionChangeAsync(setup.Root.ProtectionId))!;
            var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(original.Intent.EvidenceId))!;
            const string laterRole = "S-1-5-5-123-458";
            intent = original.Intent with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                RoleSid = laterRole,
                ExpectedDacl = original.Verification!.Dacl,
                TargetDacl = original.Intent.TargetDacl.Replace(original.Intent.RoleSid, laterRole, StringComparison.Ordinal),
                PreparedAt = setup.Move.PreparedAt.AddSeconds(-1)
            };
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
            if (applied) await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, setup.Root.LocalObject, setup.Move.PreparedAt);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await reopened.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId);
        Assert.IsNotNull(plan); Assert.AreEqual(owner, plan.Owner); Assert.AreEqual(setup.Root.MemberId, plan.RootMemberId);
        Assert.HasCount(1, plan.PendingOriginals);
        var context = plan.PendingOriginals.Single();
        Assert.AreEqual(setup.Root, context.Member); Assert.AreEqual(intent, context.Permission!.Intent);
        Assert.AreEqual(applied ? MirrorPulseNamespacePermissionPhase.Applied : MirrorPulseNamespacePermissionPhase.Prepared, context.Permission.Phase);
        Assert.IsNull(context.Permission.Verification);
        Assert.AreEqual(JsonSerializer.Serialize(initializerBefore), JsonSerializer.Serialize(await reopened.ReadNamespaceInitializationStatusAsync(owner.InitializationOperationId)));
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.IsNull(await reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelPendingPermissionReplay")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PendingPermissionInventoryUsesOnlySelectedParentIndexedPagesAndPreservesItsReadOnlyOwner(bool enabled)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
        var before = await catalog.ReadNamespaceInitializationStatusAsync(initializer);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var statements = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        MirrorPulseRootLabelPendingPermissionRecovery? plan;
        try { plan = await catalog.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Assert.IsNotNull(plan); Assert.AreEqual(setup.Root.MemberId, plan.RootMemberId); Assert.HasCount(0, plan.PendingOriginals);
        Assert.IsTrue(statements.Any(sql => sql.Contains("child_name_key>", StringComparison.Ordinal) && sql.Contains("LIMIT 16", StringComparison.Ordinal)), "The selected recovery must actually issue indexed child pages.");
        foreach (string sql in statements.Where(sql => sql.Contains("FROM namespace_member_names", StringComparison.Ordinal)))
            Assert.IsTrue(sql.Contains("WHERE parent_member_id=", StringComparison.Ordinal) || sql.Contains("WHERE member_id=", StringComparison.Ordinal),
                "A selected cancellation inventory must not materialize the global names table.");
        Assert.IsFalse(statements.Any(sql => sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)));
        await using var query = connection.CreateCommand();
        string executedPageSql = statements.First(sql => sql.Contains("child_name_key>", StringComparison.Ordinal) && sql.Contains("LIMIT 16", StringComparison.Ordinal));
        query.CommandText = "EXPLAIN QUERY PLAN " + executedPageSql;
        var details = new List<string>(); await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.IsTrue(details.Any(detail => detail.Contains("SEARCH namespace_member_names USING INDEX", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(detail => detail.Contains("SCAN ", StringComparison.Ordinal) || detail.Contains("TEMP B-TREE", StringComparison.Ordinal)));
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationStatusAsync(initializer)));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
