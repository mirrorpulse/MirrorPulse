using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnfinishedLabelOwnedBornActionResumesItsExactPendingOwnerAcrossRestart(bool enabled)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBornMemberRecoveryAction action;
        RootSubtreeFixture tree; Guid initializer;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
            action = setup.Action; tree = setup.Tree; initializer = setup.Initializer;
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await reopened.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var protection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
        var completed = await reopened.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2));
        Assert.AreEqual(completed, await reopened.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsNull((await reopened.ReadNamespaceInitializationStatusAsync(initializer)).Completion);
        Assert.AreEqual(tree.Rename, await reopened.ReadManagedRootRenameHistoryAsync(tree.Move.OperationId));
        Assert.IsNull(await reopened.ReadUnsealedRootLabelCancellationAsync(tree.Move.OperationId));
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(tree.Move.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    public async Task LabelOwnedBornActionAndCompletionRollbackKeepTheirOriginalPendingOwnership()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_label_born_pending BEFORE INSERT ON namespace_born_member_recovery_pending BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(action));
        Assert.HasCount(0, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_label_born_pending;");
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var protection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
        foreach (string failure in new[] { "completion", "release" })
        {
            string trigger = failure == "completion"
                ? "CREATE TRIGGER fail_label_born_completion BEFORE INSERT ON namespace_born_member_recovery_completions BEGIN SELECT RAISE(ABORT,'injected'); END;"
                : "CREATE TRIGGER fail_label_born_release BEFORE DELETE ON namespace_born_member_recovery_pending BEGIN SELECT RAISE(ABORT,'injected'); END;";
            await ExecuteSqlAsync(fixture.Paths, trigger);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2)));
            Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
            Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
            Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
            Assert.AreEqual(setup.Owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Tree.Move.OperationId));
            await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_label_born_" + failure + ";");
        }
        var completed = await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2));
        Assert.AreEqual(completed, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)).Completion);
        Assert.AreEqual(setup.Tree.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Tree.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    public async Task LabelWitnessDoesNotAuthorizeAStaleParentAtNativeAdmissionOrCompletion()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var previous = (await catalog.ReadNamespacePermissionChangeAsync(action.Parent.PermissionOperationId))!;
        var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(previous.Intent.EvidenceId))!;
        const string newerRole = "S-1-5-5-123-459";
        var next = previous.Intent with
        {
            OperationId = Guid.NewGuid(),
            RoleSid = newerRole,
            ExpectedDacl = previous.Verification!.Dacl,
            TargetDacl = previous.Intent.TargetDacl.Replace(previous.Intent.RoleSid, newerRole, StringComparison.Ordinal),
            PreparedAt = action.PreparedAt.AddSeconds(1)
        };
        await ProtectAsync(catalog, baseline, next);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
            LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(5)), true, action.PreparedAt.AddSeconds(6)));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.HasCount(1, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId));
        Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Tree.Move.RootId));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    public async Task LabelOwnedBornActionCannotContinueAfterTheOriginalLabelWasObservedAtItsTarget()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        await catalog.ObserveManagedRootRenameAsync(setup.Tree.Move.OperationId,
            new(setup.Tree.Root.LocalObject, setup.Tree.Rename.Proof!.PlaceholderIdentity,
                setup.Tree.Move.Target.RelativePath, action.PreparedAt.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OriginalBornHistoryRemainsReadableAfterLaterLabelProgressWithoutGrantingExecution(bool completed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var protection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
        MirrorPulseNamespaceBornMemberRecoveryCompletion? receipt = completed
            ? await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2))
            : null;
        await catalog.ObserveManagedRootRenameAsync(setup.Tree.Move.OperationId,
            new(setup.Tree.Root.LocalObject, setup.Tree.Rename.Proof!.PlaceholderIdentity,
                setup.Tree.Move.Target.RelativePath, action.PreparedAt.AddSeconds(3)));
        var labelBefore = await catalog.ReadManagedRootRenameHistoryAsync(setup.Tree.Move.OperationId);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest((await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId)).Single()));
        Assert.AreEqual(receipt, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.AreEqual(labelBefore, await catalog.ReadManagedRootRenameHistoryAsync(setup.Tree.Move.OperationId));
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)).Completion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow("fingerprint")]
    [DataRow("reservation")]
    [DataRow("current-label")]
    public async Task LabelWitnessCannotHideDamagedOrReplacedCurrentOwnership(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Initializer:D}';",
            "reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{setup.Initializer:D}';",
            _ => "DELETE FROM managed_root_renames;"
        });
        if (damage == "current-label")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
