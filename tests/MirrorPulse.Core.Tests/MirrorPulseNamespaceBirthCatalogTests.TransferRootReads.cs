using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("state")]
    [DataRow("fence")]
    [DataRow("effect")]
    public async Task RootTransferStatusReadsOnlyItsOriginalOwnerWhileGlobalRecoveryStillRejectsForeignDamage(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var seed = await PrepareTransferFixtureAsync(catalog); await catalog.PrepareNamespaceTransferAsync(seed.Plan);
        var foreign = await AddRootReadTransferAsync(catalog, seed, 1);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => $"UPDATE namespace_transfers SET fingerprint=zeroblob(32) WHERE operation_id='{foreign.OperationId:D}';",
            "state" => $"DELETE FROM namespace_transfer_states WHERE operation_id='{foreign.OperationId:D}';",
            "fence" => $"DELETE FROM namespace_transfer_roots WHERE operation_id='{foreign.OperationId:D}';",
            _ => $"DELETE FROM namespace_transfer_effects WHERE operation_id='{foreign.OperationId:D}';"
        });
        string original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        foreach (var root in new[] { seed.Plan.SourceRoute.RootId, seed.Plan.TargetRoute.RootId })
        {
            var result = await catalog.ReadPendingNamespaceTransferStatusesAsync(root);
            Assert.HasCount(1, result); Assert.AreEqual(seed.Plan.OperationId, result[0].OperationId);
            Assert.AreEqual("Prepared", result[0].Stage); Assert.IsTrue(result[0].RootsFenced); Assert.IsFalse(result[0].Completed);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(foreign.SourceRoute.RootId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RootTransferStatusFindsLostStateEvenWhenItsReservationWasAlsoRemoved(bool removeFence)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_transfer_states;" + (removeFence ? "DELETE FROM namespace_transfer_roots;" : string.Empty));
        foreach (var root in new[] { plan.SourceRoute.RootId, plan.TargetRoute.RootId })
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(root));
    }

    [TestMethod]
    public async Task RootTransferStatusRejectsAReservationPointingAtAnotherRootsOriginalTransfer()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var seed = await PrepareTransferFixtureAsync(catalog); await catalog.PrepareNamespaceTransferAsync(seed.Plan);
        var other = await AddRootReadTransferAsync(catalog, seed, 1);
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_transfer_roots SET operation_id='{other.OperationId:D}' WHERE root_id='{seed.Plan.SourceRoute.RootId}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(seed.Plan.SourceRoute.RootId));
    }

    [TestMethod]
    public async Task RootTransferCandidateReadsUseThreeIndexesAndBoundedBranches()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var seed = await PrepareTransferFixtureAsync(catalog); await catalog.PrepareNamespaceTransferAsync(seed.Plan);
        for (int index = 1; index <= 8; index++) await AddRootReadTransferAsync(catalog, seed, index);
        Assert.HasCount(1, await catalog.ReadPendingNamespaceTransferStatusesAsync(seed.Plan.SourceRoute.RootId));
        Assert.HasCount(0, await catalog.ReadPendingNamespaceTransferStatusesAsync(RootId.New()));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN " + MirrorPulseProductCatalog.NamespaceTransferRootCandidatesSql;
        query.Parameters.AddWithValue("$root", seed.Plan.SourceRoute.RootId.ToString());
        var details = new List<string>(); await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.IsTrue(details.Any(value => value.Contains("SEARCH namespace_transfers USING INDEX namespace_transfer_pending_source_root (source_root_id=?)", StringComparison.Ordinal)));
        Assert.IsTrue(details.Any(value => value.Contains("SEARCH namespace_transfers USING INDEX namespace_transfer_pending_target_root (target_root_id=?)", StringComparison.Ordinal)));
        Assert.IsTrue(details.Any(value => value.Contains("SEARCH namespace_transfer_roots USING INDEX sqlite_autoindex_namespace_transfer_roots_1 (root_id=?)", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(value => value.Contains("SCAN namespace_transfers", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DerivedRootReadIndexesRebuildWithoutChangingOriginalTransferBytesOrCatalogVersion()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
            original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX namespace_transfer_pending_source_root; DROP INDEX namespace_transfer_pending_target_root;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.HasCount(1, await reopened.ReadPendingNamespaceTransferStatusesAsync(plan.TargetRoute.RootId));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;"; Assert.AreEqual(91L, await version.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task RootTransferStatusDoesNotReturnACompletedOriginalOrAcquireNewAcknowledgement()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await PrepareCompletedRetiredTransferAsync(catalog);
        string original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        Assert.HasCount(0, await catalog.ReadPendingNamespaceTransferStatusesAsync(completed.Plan.SourceRoute.RootId));
        Assert.HasCount(0, await catalog.ReadPendingNamespaceTransferStatusesAsync(completed.Plan.TargetRoute.RootId));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task GlobalTransferRecoveryReadsAcrossIdPagesAndRejectsALateCorruptOwnerWithoutReturningPartialRoots()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var seed = await PrepareTransferFixtureAsync(catalog);
        seed = seed with { Plan = seed.Plan with { OperationId = Guid.Parse("00000000-0000-0000-0000-000000000001") } };
        await catalog.PrepareNamespaceTransferAsync(seed.Plan);
        var plans = new List<MirrorPulseNamespaceTransferPlan> { seed.Plan };
        for (int index = 2; index <= 17; index++)
            plans.Add(await AddRootReadTransferAsync(catalog, seed, index, Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.HasCount(34, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        string original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = MirrorPulseProductCatalog.NamespacePendingTransferPageSql;
            query.Parameters.AddWithValue("$after", string.Empty);
            var first = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync()) while (await reader.ReadAsync()) first.Add(reader.GetString(0));
            Assert.HasCount(16, first); Assert.AreEqual(plans[15].OperationId.ToString("D"), first[^1]);
            query.Parameters["$after"].Value = first[^1];
            await using var tail = await query.ExecuteReaderAsync();
            Assert.IsTrue(await tail.ReadAsync()); Assert.AreEqual(plans[16].OperationId.ToString("D"), tail.GetString(0));
            Assert.IsFalse(await tail.ReadAsync());
        }
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_transfer_effects WHERE operation_id='{plans[^1].OperationId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.HasCount(1, await catalog.ReadPendingNamespaceTransferStatusesAsync(seed.Plan.SourceRoute.RootId));
    }

    [TestMethod]
    public async Task PendingTransferIdPagesUseTheOriginalPartitionIndexAndStrictContinuation()
    {
        using var fixture = new Fixture(); await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN " + MirrorPulseProductCatalog.NamespacePendingTransferPageSql;
        query.Parameters.AddWithValue("$after", Guid.NewGuid().ToString("D"));
        var details = new List<string>(); await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.HasCount(1, details);
        StringAssert.Contains(details[0], "SEARCH namespace_transfer_states USING COVERING INDEX namespace_transfer_pending (completed=? AND operation_id>?)");
    }

    [TestMethod]
    public async Task GlobalOrphanTransferStateStillStopsAnUnrelatedRootStatusQuery()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, $"PRAGMA foreign_keys=OFF; INSERT INTO namespace_transfer_states VALUES('{Guid.NewGuid():D}',1,zeroblob(32));");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(RootId.New()));
    }

    private static async Task<MirrorPulseNamespaceTransferPlan> AddRootReadTransferAsync(MirrorPulseProductCatalog catalog, TransferFixture seed,
        int index, Guid? operationId = null)
    {
        var topology = await catalog.ReadAdapterTopologyAsync();
        var source = seed.Source.Parent with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = RootId.New(),
            RelativePath = "Source" + index,
            LocalObject = seed.Source.Parent.LocalObject with { LocalFileId = Guid.NewGuid() },
            CapturedAt = DateTimeOffset.UtcNow
        };
        var sourceProof = Protection(source); await ProtectAsync(catalog, source, sourceProof);
        var sourceMember = OriginalMember(source, sourceProof, seed.Source.ParentMember.ParentMemberId);
        await catalog.RegisterNamespaceMemberAsync(sourceMember);
        var born = await AddBornAsync(catalog, Birth(source, sourceProof) with { RelativePath = source.RelativePath + "/new.txt" });
        var child = BornMember(born, sourceMember); await catalog.RegisterNamespaceMemberAsync(child);
        var target = source with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = RootId.New(),
            RelativePath = "Target" + index,
            LocalObject = source.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var targetProof = Protection(target); await ProtectAsync(catalog, target, targetProof);
        var targetMember = OriginalMember(target, targetProof, seed.Source.ParentMember.ParentMemberId);
        await catalog.RegisterNamespaceMemberAsync(targetMember);
        var template = topology.Instances[0];
        AdapterInstance Instance() => new(template.AdapterId, template.InstallId, InstanceId.New(), template.DisplayName,
            template.Configuration, template.CredentialReferences, template.FileCacheDirectory, template.TransferCacheDirectory,
            template.Enabled, template.LifecycleState, null, DateTimeOffset.UtcNow);
        var sourceInstance = Instance(); var targetInstance = Instance();
        var sourceRoot = new RootRegistration(sourceInstance.AdapterId, sourceInstance.InstanceId, source.RootId!.Value,
            "source", source.RelativePath, source.RelativePath, false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        var targetRoot = new RootRegistration(targetInstance.AdapterId, targetInstance.InstanceId, target.RootId!.Value,
            "target", target.RelativePath, target.RelativePath, false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        await catalog.SaveAdapterTopologyAsync(new(topology.Installations, [.. topology.Instances, sourceInstance, targetInstance],
            [.. topology.Roots, sourceRoot, targetRoot]));
        var childSnapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.MemberId))!;
        var parentSnapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(sourceMember.MemberId))!;
        var targetSnapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(targetMember.MemberId))!;
        var preparedAt = new[] { DateTimeOffset.UtcNow, child.RegisteredAt, sourceMember.RegisteredAt, targetMember.RegisteredAt,
            childSnapshot.Context.Verification.ObservedAt, parentSnapshot.Context.Verification.ObservedAt, targetSnapshot.Context.Verification.ObservedAt }.Max().AddSeconds(1);
        var plan = new MirrorPulseNamespaceTransferPlan(1, operationId ?? Guid.NewGuid(), MirrorPulseTransferRoute.Capture(sourceInstance, sourceRoot, 2),
            MirrorPulseTransferRoute.Capture(targetInstance, targetRoot, 2), childSnapshot, parentSnapshot, targetSnapshot,
            target.RelativePath + "/moved.txt", "source-v1", Guid.NewGuid(), seed.Plan.Length, seed.Plan.Sha256, Guid.NewGuid(), Guid.NewGuid(), preparedAt);
        await catalog.PrepareNamespaceTransferAsync(plan); return plan;
    }
}
