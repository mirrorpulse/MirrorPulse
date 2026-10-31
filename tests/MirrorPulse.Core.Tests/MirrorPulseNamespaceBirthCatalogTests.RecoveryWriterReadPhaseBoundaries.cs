using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("NamespaceRecoveryWriterReadPhase")]
    [DataRow(true, false, "initializer")]
    [DataRow(false, false, "initializer")]
    [DataRow(true, true, "initializer")]
    [DataRow(false, true, "initializer")]
    [DataRow(true, false, "member")]
    [DataRow(false, false, "member")]
    [DataRow(true, true, "member")]
    [DataRow(false, true, "member")]
    [DataRow(true, false, "birth")]
    [DataRow(false, false, "birth")]
    [DataRow(true, true, "birth")]
    [DataRow(false, true, "birth")]
    public async Task RecoveryWriterRevalidatesDamagedFactsAndDiscardsFailedReadPhase(bool enabled, bool complete, string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var action = setup.Action;
        if (complete) await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        _ = await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, action.Child.Member.MemberId);
        var (table, key, id) = damage switch
        {
            "initializer" => ("namespace_initialization_intents", "operation_id", setup.Initializer),
            "member" => ("namespace_members", "member_id", action.Child.Member.MemberId),
            "birth" => ("namespace_birth_intents", "operation_id", action.Child.Member.OriginId),
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        string fingerprint;
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = $"SELECT hex(fingerprint) FROM {table} WHERE {key}='{id:D}';";
            fingerprint = (string)(await query.ExecuteScalarAsync())!;
        }
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32) WHERE {key}='{id:D}';");
        var failed = await ObserveRecoveryWriterAsync(catalog, complete ? "damaged-complete" : "damaged-prepare", enabled, 1,
            async () => await Assert.ThrowsExactlyAsync<InvalidDataException>(WriteAsync));
        Assert.AreEqual(0, failed.Writes);
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=x'{fingerprint}' WHERE {key}='{id:D}';");
        var recovered = await ObserveRecoveryWriterAsync(catalog, complete ? "repaired-complete" : "repaired-prepare", enabled, 1, WriteAsync);
        Assert.AreEqual(3, recovered.Writes);
        Assert.IsGreaterThan(0, recovered.ReadPhaseSelects);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));

        async Task WriteAsync()
        {
            if (complete) await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            else await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        }
    }

    [TestMethod]
    [TestCategory("NamespaceRecoveryWriterReadPhase")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task RecoveryWriterRollbackDropsReadFactsBeforePartialWritesAndPreservesOriginalOwnership(bool enabled, bool complete)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var action = setup.Action;
        if (complete) await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        string table = complete ? "namespace_born_member_recovery_completions" : "namespace_born_member_recovery_pending";
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER read_phase_fault BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'synthetic writer fault'); END;");
        var failed = await ObserveRecoveryWriterAsync(catalog, complete ? "rollback-complete" : "rollback-prepare", enabled, 1,
            async () => await Assert.ThrowsExactlyAsync<SqliteException>(WriteAsync));
        Assert.IsGreaterThan(0, failed.Writes);
        if (complete) Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.HasCount(complete ? 1 : 0, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER read_phase_fault;");
        await ObserveRecoveryWriterAsync(catalog, complete ? "retry-complete" : "retry-prepare", enabled, 1, WriteAsync);
        Assert.AreEqual(setup.Owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Tree.Move.OperationId));

        async Task WriteAsync()
        {
            if (complete) await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            else await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        }
    }

    [TestMethod]
    [TestCategory("NamespaceRecoveryWriterReadPhase")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task CancellationInsideWriterValidationReleasesCurrentFactsWithoutAnyMutation(bool enabled, bool complete)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var action = setup.Action;
        if (complete) await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        using var cancellation = new CancellationTokenSource();
        var failed = await ObserveRecoveryWriterAsync(catalog, complete ? "cancelled-complete" : "cancelled-prepare", enabled, 1,
            async () => await Assert.ThrowsAsync<OperationCanceledException>(() => WriteAsync(cancellation.Token)), cancellation.Cancel);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(0, failed.Writes);
        Assert.IsGreaterThan(0, failed.ReadPhaseSelects);
        await ObserveRecoveryWriterAsync(catalog, complete ? "uncancelled-complete" : "uncancelled-prepare", enabled, 1,
            () => WriteAsync(CancellationToken.None));

        async Task WriteAsync(CancellationToken token)
        {
            if (complete) await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2), token);
            else await catalog.PrepareNamespaceBornMemberRecoveryAsync(action, token);
        }
    }
}
