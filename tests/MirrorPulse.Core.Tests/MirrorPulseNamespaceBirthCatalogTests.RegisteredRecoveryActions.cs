using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredBornRecoveryAdmissionSurvivesRestartWithoutInventingOriginalOrNativeFacts(bool moved)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBornMemberRecoveryAction action;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first);
            if (moved) await CompleteMoveChainStepAsync(first, setup.Move);
            await RotateContextOriginalParentsAsync(first, setup, setup.Move.PreparedAt.AddSeconds(5));
            action = await RegisteredRecoveryActionAsync(first, setup.Member, setup.Move.PreparedAt.AddSeconds(10));
            await first.PrepareNamespaceBornMemberRecoveryAsync(action);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(action, (await reopened.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId)).Single());
        Assert.AreEqual(action, await reopened.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.AreEqual(action, await reopened.PrepareNamespaceBornMemberRecoveryAsync(action));
        Assert.IsNull(await reopened.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(action.Child.BirthProtection, await reopened.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.AreEqual(action.Child.Member, await reopened.ReadNamespaceMemberAsync(action.Child.Member.MemberId));
        Assert.AreEqual(action.Child.BirthObservation, await reopened.ReadNamespaceBirthObservationAsync(action.Child.Member.OriginId));
        Assert.IsNull(action.Child.Original);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceBornMemberRecoveryAsync(action with { TargetDacl = action.Child.BirthProtection!.Verification.Dacl }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RegisteredBornRecoveryHeadAndActionInsertRollbackTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_born_head BEFORE INSERT ON namespace_born_member_recovery_heads BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(action));
        Assert.IsEmpty(await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_born_head;");
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Assert.AreEqual(action, await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Move));
    }

    [TestMethod]
    public async Task RegisteredBornRecoveryRejectsStaleParentAndSupersededExecutionButRetainsHistoricalActions()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5));
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var successor = action with { ActionId = Guid.NewGuid(), PreviousActionId = action.ActionId, PreparedAt = action.PreparedAt.AddSeconds(1) };
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(successor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        await ProtectAsync(catalog, setup.Parent, RotateOriginal(setup.ParentProof, RelocatedRole, successor.PreparedAt.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(successor.ActionId));
        Assert.HasCount(2, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.Member.MemberId));
        Assert.AreEqual(action, await catalog.PrepareNamespaceBornMemberRecoveryAsync(action));
    }

    [TestMethod]
    public async Task RegisteredBornRecoveryCompletionAndLocatedProjectionAreAtomicAndReplayPreservesLaterOwnership()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await CompleteMoveChainStepAsync(catalog, setup.Move);
        await RotateContextOriginalParentsAsync(catalog, setup, setup.Move.PreparedAt.AddSeconds(5));
        var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(10));
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var parent = action.Parent.Context;
        var protection = action.Child.BirthProtection! with
        {
            Version = 3,
            ProtectionId = action.ActionId,
            PreviousProtectionId = action.Child.BirthProtection!.ProtectionId,
            RoleSid = parent.Protection.RoleSid,
            Verification = action.Child.BirthProtection.Verification with { Dacl = action.TargetDacl, ObservedAt = action.PreparedAt.AddSeconds(1) },
            Placement = new(action.Child.Member.MemberId, action.Child.Location.CurrentPositionOperationId,
                parent.Member.MemberId, parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId, action.Child.Location.RelativePath)
        }; // Synthetic post-scope facts exercise the transaction, not native success.
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, false, action.PreparedAt.AddSeconds(2)));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_born_completion BEFORE INSERT ON namespace_born_member_recovery_completions BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2)));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_born_completion;");
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_born_release BEFORE DELETE ON namespace_born_member_recovery_pending BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2)));
        Assert.AreEqual(action.Child.BirthProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(action, await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_born_release;");
        var completed = await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, action.PreparedAt.AddSeconds(2));
        Assert.AreEqual(protection, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        var next = await RegisteredRecoveryActionAsync(catalog, setup.Member, action.PreparedAt.AddSeconds(3), action.InitializationOperationId, action.ActionId);
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(next);
        Assert.AreEqual(completed, await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, completed.CompletedAt));
        Assert.AreEqual(next, await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(next.ActionId));
        CollectionAssert.AreEqual(new[] { setup.Move.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("head")]
    [DataRow("payload")]
    [DataRow("predecessor")]
    [DataRow("pending")]
    public async Task RegisteredBornRecoveryRejectsDamagedIndexesAndPayloadsAcrossRestart(string damage)
    {
        using var fixture = new Fixture(); Guid member;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); member = setup.Member.MemberId;
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5)));
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "head" => "DELETE FROM namespace_born_member_recovery_heads;",
            "payload" => "UPDATE namespace_born_member_recovery_actions SET payload=X'00';",
            "pending" => "DELETE FROM namespace_born_member_recovery_pending;",
            _ => $"PRAGMA foreign_keys=OFF; UPDATE namespace_born_member_recovery_actions SET previous_id='{Guid.NewGuid():D}';"
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceBornMemberRecoveryActionsAsync(member));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task RegisteredBornRecoveryMigrationCreatesNoActionOrProtectionForLegacyCatalogs()
    {
        using var fixture = new Fixture(); Guid member;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); member = setup.Member.MemberId;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_born_member_recovery_pending; DROP TABLE namespace_born_member_recovery_completions; DROP TABLE namespace_born_member_recovery_heads; DROP TABLE namespace_born_member_recovery_actions; PRAGMA user_version=50;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsEmpty(await reopened.ReadNamespaceBornMemberRecoveryActionsAsync(member));
        Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, (await reopened.ReadNamespaceMemberAsync(member))!.Origin);
    }

    private static async Task<MirrorPulseNamespaceBornMemberRecoveryAction> RegisteredRecoveryActionAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, DateTimeOffset preparedAt, Guid? initializationId = null, Guid? previous = null)
    {
        Guid initialization = initializationId ?? Guid.NewGuid();
        if (initializationId is null) await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, member.RootId!.Value));
        var child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, member.MemberId))[0];
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.Location.ParentMemberId!.Value))!;
        string target = child.BirthProtection!.Verification.Dacl.Replace(child.BirthProtection.RoleSid, parent.Context.Protection.RoleSid, StringComparison.Ordinal);
        return new(1, Guid.NewGuid(), initialization, previous, child, parent, target, preparedAt);
    }
}
