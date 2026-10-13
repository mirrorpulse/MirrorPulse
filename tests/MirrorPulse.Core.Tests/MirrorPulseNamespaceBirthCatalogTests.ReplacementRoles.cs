using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementRoleChainKeepsFirstExecutionFactsAcrossOwnersAndInterruptedHandoffs(bool copied)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution original;
        MirrorPulseNamespaceReplacementRoleAction first, second; MirrorPulseNamespaceReplacementRoleHandoff observed;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await PrepareReplacementRoleFixtureAsync(catalog, copied);
            first = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
            await catalog.PrepareNamespaceReplacementRoleActionAsync(first);
            observed = ReplacementRoleHandoff(first, original.Intent, true); await catalog.RecordNamespaceReplacementRoleHandoffAsync(observed);
            second = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", first);
            await catalog.PrepareNamespaceReplacementRoleActionAsync(second);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!;
        CollectionAssert.AreEqual(new[] { first, second }, actual.RoleActions.ToArray()); Assert.IsNull(actual.RoleHandoff);
        Assert.AreEqual(original.Action, actual.Action); Assert.AreEqual(original.Backup, actual.Backup);
        Assert.AreEqual(original.WriteStart, actual.WriteStart); Assert.AreEqual(original.ContentCompletion, actual.ContentCompletion);
        Assert.AreEqual(first, await reopened.PrepareNamespaceReplacementRoleActionAsync(first));
        Assert.AreEqual(observed, await reopened.RecordNamespaceReplacementRoleHandoffAsync(observed));
        var third = (await ReplacementRoleActionAsync(reopened, original, "S-1-5-5-90-100", second)) with { ExpectedPermissionActionId = original.Action!.ActionId, ExpectedOwnedDacl = original.Action.RoleOnlyDacl };
        await reopened.PrepareNamespaceReplacementRoleActionAsync(third);
        Assert.AreEqual(third, (await reopened.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.RoleActions[^1]);
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsNull(await reopened.ReadMutationAsync(original.Intent.OperationId)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("firstAction")]
    [DataRow("permissionAction")]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("predecessor")]
    [DataRow("role")]
    [DataRow("descriptor")]
    [DataRow("parent")]
    [DataRow("time")]
    public async Task ReplacementRoleChainRejectsBorrowedOrUnprovenAuthority(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        var action = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
        action = changed switch
        {
            "firstAction" => action with { FirstActionId = Guid.NewGuid() },
            "permissionAction" => action with { ExpectedPermissionActionId = Guid.NewGuid() },
            "binding" => action with { Action = action.Action with { LocalObject = action.Action.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "owner" => action with { Action = action.Action with { OwnerSid = "S-1-5-18" } },
            "predecessor" => action with { PreviousRecoveryId = Guid.NewGuid() },
            "role" => action with { Action = action.Action with { RoleSid = original.Action!.RoleSid } },
            "descriptor" => action with { ExpectedOwnedDacl = Dacl("D:P(A;;FA;;;SY)") },
            "parent" => action with { Parent = action.Parent with { Context = action.Parent.Context with { Location = action.Parent.Context.Location with { RelativePath = "other" } } } },
            "time" => action with { Action = action.Action with { PreparedAt = original.Action!.PreparedAt.AddTicks(-1) } },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(action));
        Assert.IsEmpty((await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.RoleActions);
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("descriptor")]
    [DataRow("identity")]
    [DataRow("time")]
    public async Task ReplacementRoleHandoffRequiresIndependentOriginalObjectAndRoleReadback(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        var action = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(action);
        var handoff = ReplacementRoleHandoff(action, original.Intent, true);
        handoff = changed switch
        {
            "binding" => handoff with { Verification = handoff.Verification with { LocalObject = handoff.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "owner" => handoff with { Verification = handoff.Verification with { OwnerSid = "S-1-5-18" } },
            "descriptor" => handoff with { Verification = handoff.Verification with { Dacl = original.Action!.RoleOnlyDacl } },
            "identity" => handoff with { PlaceholderIdentity = Convert.ToBase64String([9, 8, 7]) },
            "time" => handoff with { Verification = handoff.Verification with { ObservedAt = action.Action.PreparedAt.AddTicks(-1) } },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRoleHandoffAsync(handoff));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.RoleHandoff);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementRoleHistoryIsImmutableAndDamagedPredecessorsKeepRootFenced(bool placeholder)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, true);
        var first = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(first);
        var handoff = ReplacementRoleHandoff(first, original.Intent, placeholder); await catalog.RecordNamespaceReplacementRoleHandoffAsync(handoff);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRoleHandoffAsync(handoff with { Verification = handoff.Verification with { ObservedAt = handoff.Verification.ObservedAt.AddTicks(1) } }));
        var next = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", first); await catalog.PrepareNamespaceReplacementRoleActionAsync(next);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_replacement_role_actions SET fingerprint=zeroblob(32) WHERE sequence=(SELECT MIN(sequence) FROM namespace_replacement_role_actions);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ReplacementRoleMigrationKeepsFirstFactsWithoutInventingAuthority()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) original = await PrepareReplacementRoleFixtureAsync(first, true);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_replacement_role_handoffs; DROP TABLE namespace_replacement_role_actions; PRAGMA user_version=45;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await second.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!;
        Assert.AreEqual(original.Action, actual.Action); Assert.AreEqual(original.Backup, actual.Backup); Assert.AreEqual(original.ContentCompletion, actual.ContentCompletion);
        Assert.IsEmpty(actual.RoleActions); Assert.IsNull(actual.RoleHandoff);
    }

    [TestMethod]
    public async Task ReplacementRoleRecoveryCannotSupersedeAnUndrainedOriginalScope()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, true);
        var action = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
        var attempt = new MirrorPulseNamespaceReplacementRestoration(1, original.Intent.OperationId, Guid.NewGuid(), null,
            original.Action!.ActionId, action.Parent, original.ContentCompletion!.Verification.ObservedAt.AddSeconds(1));
        await catalog.PrepareNamespaceReplacementRestorationAsync(attempt);
        await catalog.RecordNamespaceReplacementRestorationResultAsync(ReplacementRestorationResult(attempt, original, "Canceled", false) with { Drained = false });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(action));
        Assert.IsEmpty((await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.RoleActions);
    }

    [TestMethod]
    public async Task ReplacementRoleRecoveryRejectsLateHandoffAndAdmissionBeforeAnObservedPredecessor()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        var first = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(first);
        var observed = ReplacementRoleHandoff(first, original.Intent, false); await catalog.RecordNamespaceReplacementRoleHandoffAsync(observed);
        var next = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", first);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(next with { Action = next.Action with { PreparedAt = observed.Verification.ObservedAt.AddTicks(-1) } }));
        await catalog.PrepareNamespaceReplacementRoleActionAsync(next);
        var third = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-90-100", next); await catalog.PrepareNamespaceReplacementRoleActionAsync(third);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(next, original.Intent, false)));
        Assert.AreEqual(third, (await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.RoleActions[^1]);
    }

    [TestMethod]
    public async Task ReplacementRoleRecoveryCannotBorrowWorkerOwnershipOrRewriteFirstAdmission()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        var action = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
        await catalog.TryRecordWorkerRequestAsync(action.RecoveryId, InstanceId.New(), System.Security.Cryptography.SHA256.HashData([1, 2, 3]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(action));
        Guid newId = Guid.NewGuid(); action = action with { RecoveryId = newId, Action = action.Action with { ActionId = newId } };
        await catalog.PrepareNamespaceReplacementRoleActionAsync(action);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(action with { Action = action.Action with { PreparedAt = action.Action.PreparedAt.AddTicks(1) } }));
        Assert.AreEqual(original.Action, (await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.Action);
    }

    [TestMethod]
    public async Task ReplacementRoleRecoveryRequiresItsRecordedFirstPermissionAction()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        var history = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        var action = await ReplacementRoleActionAsync(catalog, history with { Action = facts.Action }, "S-1-5-5-50-60");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRoleActionAsync(action));
        Assert.IsEmpty((await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.RoleActions);
    }

    private static async Task<MirrorPulseNamespaceReplacementExecution> PrepareReplacementRoleFixtureAsync(MirrorPulseProductCatalog catalog, bool copied)
    {
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        if (copied)
        {
            await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup); await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
            await catalog.RecordNamespaceReplacementContentCompletionAsync(ReplacementContentReceipt(intent, facts, true));
        }
        return (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
    }

    private static async Task<MirrorPulseNamespaceReplacementRoleAction> ReplacementRoleActionAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceReplacementExecution original,
        string roleSid, MirrorPulseNamespaceReplacementRoleAction? previous = null)
    {
        Guid id = Guid.NewGuid(); var first = original.Action!;
        var action = first with
        {
            ActionId = id,
            RoleSid = roleSid,
            RoleOnlyDacl = Dacl($"D:P(A;;FA;;;{roleSid})"),
            RestoreDacl = Dacl($"D:P(A;;FRFW;;;{first.OwnerSid})(A;;FA;;;{roleSid})"),
            PreparedAt = (previous?.Action.PreparedAt ?? first.PreparedAt).AddSeconds(10)
        };
        // The data-only recovery admits the existing closed parent; it does not require a
        // fictitious completed parent role rotation before recovering the pending child.
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Intent.Original.Context.Location.ParentMemberId!.Value))!;
        return new(1, original.Intent.OperationId, id, previous?.RecoveryId, first.ActionId, previous?.RecoveryId ?? first.ActionId, action,
            parent, previous?.Action.RoleOnlyDacl ?? first.RoleOnlyDacl);
    }

    private static MirrorPulseNamespaceReplacementRoleHandoff ReplacementRoleHandoff(MirrorPulseNamespaceReplacementRoleAction action,
        MirrorPulseNamespaceReplacementIntent intent, bool placeholder) => new(1, intent.OperationId, action.RecoveryId,
            new(action.Action.LocalObject, action.Action.OwnerSid, action.Action.RoleOnlyDacl, action.Action.PreparedAt.AddSeconds(1)),
            1, placeholder, placeholder ? intent.OriginalPlaceholderIdentity : null);
}
