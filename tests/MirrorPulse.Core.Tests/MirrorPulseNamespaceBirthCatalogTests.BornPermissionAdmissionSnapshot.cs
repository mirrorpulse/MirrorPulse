using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("BornPermissionAdmissionSnapshot")]
    [DataRow(true, true, 1)]
    [DataRow(true, false, 1)]
    [DataRow(true, true, 4)]
    [DataRow(true, false, 4)]
    [DataRow(false, true, 1)]
    [DataRow(false, true, 4)]
    public async Task BothFreshPermissionAdmissionsRetainOriginalActionInitializerAndLineageWithFewerProductionQueries(bool label, bool enabled, int generations)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        MirrorPulseNamespaceBornMemberRecoveryAction action;
        if (label) action = (await PrepareLabelOwnedBornActionAsync(catalog, enabled)).Action;
        else
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5));
        }
        for (int generation = 1; generation <= generations; generation++)
        {
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            if (generation == generations) break;
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            action = action with
            {
                ActionId = Guid.NewGuid(),
                PreviousActionId = action.ActionId,
                Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(action.InitializationOperationId, action.Child.Member.MemberId))[0],
                Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!,
                PreparedAt = action.PreparedAt.AddSeconds(3)
            };
        }
        var legacy = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var initial = await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId);
            var initializer = await catalog.ReadNamespaceInitializationStatusAsync(initial.InitializationOperationId);
            var current = await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId);
            var lineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initial.InitializationOperationId, initial.Child.Member.MemberId);
            Assert.AreEqual(SerializeBornOwnerTest(initial), SerializeBornOwnerTest(current));
            return new MirrorPulseBornMemberRecoveryAdmission(current, initializer, lineage);
        });
        var combined = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var before = await catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId);
            var after = await catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId);
            Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after));
            return after;
        });
        Assert.AreEqual(SerializeBornOwnerTest(legacy.Result), SerializeBornOwnerTest(combined.Result));
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(combined.Result.Action));
        Assert.IsNull(combined.Result.Initialization.Completion);
        Assert.AreEqual(action.Child.Member.MemberId, combined.Result.Lineage[0].Member.MemberId);
        Assert.IsNull(combined.Result.Lineage[^1].Member.RootId);
        Assert.IsLessThan(legacy.Selects, combined.Selects, "Both original permission admission checks must reduce actual production queries.");
        Console.WriteLine("BornPermissionAdmissionSql: " + JsonSerializer.Serialize(new
        {
            label,
            enabled,
            generations,
            legacySelects = legacy.Selects,
            combinedSelects = combined.Selects,
            freshAdmissionTransactions = 2,
            currentActionChecks = 2,
            permissionGateOrNativeExecuted = false,
            wallClockSpeedupEstablished = false
        }));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("BornPermissionAdmissionSnapshot")]
    [DataRow("completion")]
    [DataRow("successor")]
    [DataRow("action-fingerprint")]
    [DataRow("initializer-fingerprint")]
    [DataRow("reservation")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    public async Task SecondPermissionAdmissionCannotReuseAnEarlierActionInitializerOrLineageAfterMutation(string change)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, false); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var before = await catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId);
        string historical = SerializeBornOwnerTest(before);
        if (change is "completion" or "successor")
        {
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            if (change == "successor")
                await catalog.PrepareNamespaceBornMemberRecoveryAsync(action with
                {
                    ActionId = Guid.NewGuid(),
                    PreviousActionId = action.ActionId,
                    Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, action.Child.Member.MemberId))[0],
                    Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!,
                    PreparedAt = action.PreparedAt.AddSeconds(3)
                });
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId));
        }
        else
        {
            await ExecuteSqlAsync(fixture.Paths, change switch
            {
                "action-fingerprint" => $"UPDATE namespace_born_member_recovery_actions SET fingerprint=zeroblob(32) WHERE action_id='{action.ActionId:D}';",
                "initializer-fingerprint" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Initializer:D}';",
                "reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{setup.Initializer:D}';",
                "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Child.Member.MemberId:D}';",
                "parent-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Parent.Context.Member.MemberId:D}';",
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId));
        }
        Assert.AreEqual(historical, SerializeBornOwnerTest(before), "An old detached fact cannot be refreshed into new admission.");
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [TestCategory("BornPermissionAdmissionSnapshot")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PermissionAdmissionCancellationAndCatalogReopenRetainExactOriginalFacts(bool enabled)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceBornMemberRecoveryAction action; string expected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            action = (await PrepareLabelOwnedBornActionAsync(catalog, enabled)).Action;
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            expected = SerializeBornOwnerTest(await catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId, cancellation.Token));
            Assert.AreEqual(expected, SerializeBornOwnerTest(await catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId)));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, SerializeBornOwnerTest(await reopened.ReadNamespaceBornMemberRecoveryAdmissionAsync(action.ActionId)));
        Assert.IsNull(await reopened.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
    }

    [TestMethod]
    [TestCategory("BornPermissionAdmissionSnapshot")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PermissionAdmissionRejectsEmptyOrUnknownActionWithoutCreatingProvenance(bool empty)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (empty) await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(Guid.Empty));
        else await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceBornMemberRecoveryAdmissionAsync(Guid.NewGuid()));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
