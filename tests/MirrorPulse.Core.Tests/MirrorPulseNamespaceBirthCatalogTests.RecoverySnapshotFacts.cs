using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("NamespaceRecoverySnapshotFacts")]
    [DataRow(false, "remote-index")]
    [DataRow(true, "remote-index")]
    [DataRow(false, "transfer-index")]
    [DataRow(true, "transfer-index")]
    [DataRow(false, "initializer-fingerprint")]
    [DataRow(true, "initializer-fingerprint")]
    [DataRow(false, "initializer-reservation")]
    [DataRow(true, "initializer-reservation")]
    [DataRow(false, "birth-fingerprint")]
    [DataRow(true, "birth-fingerprint")]
    [DataRow(false, "observation-fingerprint")]
    [DataRow(true, "observation-fingerprint")]
    public async Task CompletedRecoverySnapshotCannotHideChangedOwnerOrBirthProofInTheNextRead(bool admission, string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        async Task<string> ReadAsync() => admission
            ? SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId))
            : SerializeBornOwnerTest(await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, action.Child.Member.MemberId));
        string original = await ReadAsync();
        Assert.AreEqual(original, await ReadAsync());
        Guid birth = action.Child.Member.OriginId;
        string sql = damage switch
        {
            "remote-index" => $"INSERT INTO namespace_remote_birth_entries VALUES('{birth:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}','{new string('A', 64)}',1,0,'{Guid.NewGuid():D}','{Guid.NewGuid():D}');",
            "transfer-index" => $"INSERT INTO namespace_transfer_target_births VALUES('{Guid.NewGuid():D}','{birth:D}','{Guid.NewGuid():D}',x'7B7D',zeroblob(32));",
            "initializer-fingerprint" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Initializer:D}';",
            "initializer-reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{setup.Initializer:D}';",
            "birth-fingerprint" => $"UPDATE namespace_birth_intents SET fingerprint=zeroblob(32) WHERE operation_id='{birth:D}';",
            "observation-fingerprint" => $"UPDATE namespace_birth_observations SET fingerprint=zeroblob(32) WHERE operation_id='{birth:D}';",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + sql);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(ReadAsync);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceRecoverySnapshotFacts")]
    [DataRow("namespace_remote_birth_entries")]
    [DataRow("namespace_transfer_target_births")]
    public async Task FailedRecoveryProofCannotPoisonTheNextSuccessfulRead(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Guid birth = action.Child.Member.OriginId;
        string insert = table == "namespace_remote_birth_entries"
            ? $"INSERT INTO namespace_remote_birth_entries VALUES('{birth:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}','{new string('A', 64)}',1,0,'{Guid.NewGuid():D}','{Guid.NewGuid():D}');"
            : $"INSERT INTO namespace_transfer_target_births VALUES('{Guid.NewGuid():D}','{birth:D}','{Guid.NewGuid():D}',x'7B7D',zeroblob(32));";
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + insert);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        string key = table == "namespace_remote_birth_entries" ? "operation_id" : "birth_id";
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM {table} WHERE {key}='{birth:D}';");
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceRecoverySnapshotFacts")]
    public async Task EarlierValidatedHistoryCannotAuthorizeAChangedParentRole()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var previous = (await catalog.ReadNamespacePermissionChangeAsync(action.Parent.PermissionOperationId))!;
        var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(previous.Intent.EvidenceId))!;
        const string nextRole = "S-1-5-5-123-459";
        await ProtectAsync(catalog, baseline, previous.Intent with
        {
            OperationId = Guid.NewGuid(),
            RoleSid = nextRole,
            ExpectedDacl = previous.Verification!.Dacl,
            TargetDacl = previous.Intent.TargetDacl.Replace(previous.Intent.RoleSid, nextRole, StringComparison.Ordinal),
            PreparedAt = action.PreparedAt.AddSeconds(1)
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.HasCount(1, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceRecoverySnapshotFacts")]
    public async Task SummarySelectionRetainsTheWholeChainAndRefusesEarlierCompletedExecution()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var first = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(first);
        await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(first.ActionId);
        var completed = await catalog.CompleteNamespaceBornMemberRecoveryAsync(first.ActionId,
            LabelOwnedBornProtection(first, first.PreparedAt.AddSeconds(1)), true, first.PreparedAt.AddSeconds(2));
        var next = first with
        {
            ActionId = Guid.NewGuid(),
            PreviousActionId = first.ActionId,
            Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, first.Child.Member.MemberId))[0],
            Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(first.Parent.Context.Member.MemberId))!,
            PreparedAt = first.PreparedAt.AddSeconds(3)
        };
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(next);
        Assert.AreEqual(SerializeBornOwnerTest(next), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(next.ActionId)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(first.ActionId));
        Assert.AreEqual(SerializeBornOwnerTest(next), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(next.ActionId)));
        var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(first.Child.Member.MemberId);
        Assert.HasCount(2, history);
        Assert.AreEqual(SerializeBornOwnerTest(first), SerializeBornOwnerTest(history[0]));
        Assert.AreEqual(SerializeBornOwnerTest(next), SerializeBornOwnerTest(history[1]));
        Assert.AreEqual(completed, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(first.ActionId));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(next.ActionId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
