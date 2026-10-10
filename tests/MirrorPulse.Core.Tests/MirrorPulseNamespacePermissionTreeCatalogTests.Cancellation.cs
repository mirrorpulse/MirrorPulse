using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CancelledCaptureRetainsOriginalsAcrossRestartAndNeverAdmitsItsOperationIds(int captured)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        DateTimeOffset at = SealTime(definition);
        MirrorPulseNamespacePermissionTree cancelled;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            if (captured > 0) await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..captured]);
            cancelled = await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, at);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, cancelled.Phase);
            Assert.AreEqual(captured, cancelled.Cancellation!.CapturedMembers);
            Assert.AreEqual(64, cancelled.Cancellation.Fingerprint.Length);
            Assert.IsNull(cancelled.Seal);
            Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(cancelled, await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.AreEqual(cancelled, await reopened.CreateNamespacePermissionTreeAsync(definition));
        Assert.AreEqual(cancelled, await reopened.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, at));
        CollectionAssert.AreEqual(members[..captured], (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId)).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, at.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, at));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [members[0]]));
        foreach (var member in members.Take(Math.Max(captured, 1)))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespacePermissionChangeAsync(member.Preparation.Baseline, member.Preparation.Intent));
            Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(member.Preparation.Baseline.EvidenceId));
        }
        var fresh = members.Select(member => member with
        {
            Preparation = member.Preparation with { Intent = member.Preparation.Intent with { OperationId = Guid.NewGuid() } },
        }).ToArray();
        var next = definition with { ManifestId = Guid.NewGuid(), Anchor = fresh[0].Preparation };
        await reopened.CreateNamespacePermissionTreeAsync(next);
        await reopened.AppendNamespacePermissionTreeMembersAsync(next.ManifestId, fresh);
        await reopened.SealNamespacePermissionTreeAsync(next.ManifestId, at);
        foreach (var member in fresh)
            await reopened.PrepareNamespacePermissionChangeAsync(member.Preparation.Baseline, member.Preparation.Intent);
        Assert.HasCount(3, await reopened.ReadNamespacePermissionChangesAsync());
        Assert.AreEqual(cancelled, await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
    }

    [TestMethod]
    public async Task CancellationReleasesOnlyItsCaptureFenceAndNeverChangesExecutableEvidence()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        var second = definition with { ManifestId = Guid.NewGuid(), Anchor = definition.Anchor with { Intent = definition.Anchor.Intent with { OperationId = Guid.NewGuid() } } };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
        await catalog.CreateNamespacePermissionTreeAsync(second);
        var unrelated = members[2].Preparation with { Intent = members[2].Preparation.Intent with { OperationId = Guid.NewGuid() } };
        await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(unrelated.Baseline, unrelated.Intent));
        await catalog.CancelNamespacePermissionTreeCaptureAsync(second.ManifestId, SealTime(definition));
        var prepared = await catalog.PrepareNamespacePermissionChangeAsync(unrelated.Baseline, unrelated.Intent);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, prepared.Phase);
        Assert.AreEqual(prepared, await catalog.ReadNamespacePermissionChangeAsync(unrelated.Intent.OperationId));
        Assert.HasCount(1, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task SealedCaptureCannotBeCancelledBeforeOrAfterItsPermissionChangeIsPrepared()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
        var sealedTree = await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition)));
        var change = await catalog.PrepareNamespacePermissionChangeAsync(definition.Anchor.Baseline, definition.Anchor.Intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition)));
        Assert.AreEqual(sealedTree, await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.AreEqual(change, await catalog.ReadNamespacePermissionChangeAsync(definition.Anchor.Intent.OperationId));
    }

    [TestMethod]
    [DataRow("UPDATE namespace_permission_tree_cancellations SET payload=json_set(payload,'$.CapturedMembers',0);")]
    [DataRow("UPDATE namespace_permission_tree_cancellations SET payload=json_set(payload,'$.Fingerprint','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA');")]
    [DataRow("DELETE FROM namespace_permission_tree_members WHERE sequence=1;")]
    [DataRow("UPDATE namespace_permission_tree_members SET relative_path_key='CORRUPT' WHERE sequence=1;")]
    [DataRow("UPDATE namespace_permission_trees SET root_id='00000000-0000-0000-0000-000000000001';")]
    public async Task CorruptCancellationCannotReleasePreparationAdmission(string corruption)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
            await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition));
        }
        await ExecuteSqlAsync(fixture.Paths, corruption);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.PrepareNamespacePermissionChangeAsync(
            definition.Anchor.Baseline, definition.Anchor.Intent with { OperationId = Guid.NewGuid() }));
        Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task CancelledTokenAndEarlierTimeLeaveTheOriginalCaptureOpen()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition), new(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, definition.CreatedAt.AddTicks(-1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, definition.CreatedAt));
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Capturing, (await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.Phase);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(definition.Anchor.Baseline, definition.Anchor.Intent));
    }

    [TestMethod]
    public async Task ConcurrentAppendAndCancellationRetainOneConsistentImmutablePrefix()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MirrorPulseNamespacePermissionTree> cancel = Task.Run(async () =>
        {
            await start.Task;
            return await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition));
        });
        Task<MirrorPulseNamespacePermissionTree> append = Task.Run(async () =>
        {
            await start.Task;
            return await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
        });
        start.SetResult();
        int captured = (await cancel).CapturedMembers;
        if (captured == 0) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => append);
        else Assert.AreEqual(3, (await append).CapturedMembers);
        Assert.IsTrue(captured is 0 or 3);
        Assert.HasCount(captured, await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
        Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task CancelledLargeCaptureVerifiesEveryRetainedPageBeforeReleasingAdmission()
    {
        using var fixture = new CatalogFixture();
        var (definition, _) = Tree(4098);
        var members = new MirrorPulseNamespacePermissionTreeMember[4097];
        members[0] = new(0, null, definition.Anchor);
        for (int index = 1; index < members.Length; index++)
            members[index] = Child(definition.Anchor, index, $"Docs/entry-{index}.txt");
        MirrorPulseNamespacePermissionTree cancelled;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..4096]);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[4096..]);
            cancelled = await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(cancelled, await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
            CollectionAssert.AreEqual(members[4096..], (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId, 4095)).ToArray());
        }
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_permission_tree_members SET fingerprint=zeroblob(32) WHERE sequence=4096;");
        await using var corrupted = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupted.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupted.PrepareNamespacePermissionChangeAsync(
            definition.Anchor.Baseline, definition.Anchor.Intent with { OperationId = Guid.NewGuid() }));
        Assert.HasCount(0, await corrupted.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task Schema23UpgradePreservesOpenAndSealedCapturesAndWorkerEvidence()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        var (open, _) = Tree();
        Guid workerOperation = Guid.NewGuid();
        var instance = MirrorPulse.Core.Contracts.InstanceId.New();
        byte[] fingerprint = Enumerable.Repeat((byte)42, 32).ToArray();
        MirrorPulseNamespacePermissionTree retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
            retained = await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
            await catalog.PrepareNamespacePermissionChangeAsync(definition.Anchor.Baseline, definition.Anchor.Intent);
            await catalog.CreateNamespacePermissionTreeAsync(open);
            await catalog.TryRecordWorkerRequestAsync(workerOperation, instance, fingerprint);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_permission_tree_cancellations; PRAGMA user_version=23;");
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(retained, await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
            Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Capturing, (await reopened.ReadNamespacePermissionTreeAsync(open.ManifestId))!.Phase);
            CollectionAssert.AreEqual(members, (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId)).ToArray());
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await reopened.ReadNamespacePermissionChangeAsync(definition.Anchor.Intent.OperationId))!.Phase);
            Assert.IsFalse(await reopened.TryRecordWorkerRequestAsync(workerOperation, instance, fingerprint));
            await reopened.CancelNamespacePermissionTreeCaptureAsync(open.ManifestId, SealTime(open));
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(27L, await command.ExecuteScalarAsync());
    }
}
