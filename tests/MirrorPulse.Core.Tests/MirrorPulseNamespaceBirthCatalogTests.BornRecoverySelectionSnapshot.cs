using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("BornRecoverySelectionSnapshot")]
    [DataRow(true, true, 1, false)]
    [DataRow(true, false, 1, false)]
    [DataRow(true, true, 4, false)]
    [DataRow(true, false, 4, false)]
    [DataRow(true, true, 1, true)]
    [DataRow(true, false, 1, true)]
    [DataRow(true, true, 4, true)]
    [DataRow(true, false, 4, true)]
    [DataRow(false, true, 1, false)]
    [DataRow(false, true, 4, false)]
    [DataRow(false, true, 1, true)]
    [DataRow(false, true, 4, true)]
    public async Task FreshBornSelectionRetainsCompleteProductionPlanningFactsWithFewerQueries(bool label, bool enabled, int generations, bool complete)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var action = await PrepareSelectionHistoryAsync(catalog, label, enabled, generations, complete);
        var legacy = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var lineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(action.InitializationOperationId, action.Child.Member.MemberId);
            var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId);
            var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(lineage[0].Location.ParentMemberId!.Value))!;
            var latest = history.Count == 0 ? null : history[^1];
            var completion = latest is null ? null : await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(latest.ActionId);
            return new MirrorPulseBornMemberRecoverySelection(lineage, parent, latest, completion);
        });
        var combined = await MeasureRegisteredBirthReadAsync(catalog, () => catalog.ReadNamespaceBornMemberRecoverySelectionAsync(
            action.InitializationOperationId, action.Child.Member.MemberId));
        Assert.AreEqual(SerializeBornOwnerTest(legacy.Result), SerializeBornOwnerTest(combined.Result));
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(combined.Result.LatestAction));
        Assert.AreEqual(complete, combined.Result.LatestCompletion is not null);
        Assert.IsLessThan(legacy.Selects, combined.Selects, "The complete actual production selection must reduce queries, with every original fact retained.");
        Assert.AreEqual(SerializeBornOwnerTest(combined.Result), SerializeBornOwnerTest(await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(
            action.InitializationOperationId, action.Child.Member.MemberId)), "A later selection performs its own complete fresh validation.");
        Console.WriteLine("BornRecoverySelectionSql: " + JsonSerializer.Serialize(new
        {
            label,
            enabled,
            generations,
            complete,
            legacySelects = legacy.Selects,
            combinedSelects = combined.Selects,
            freshReadTransactions = 1,
            nativeOrPermissionGateExecuted = false,
            wallClockSpeedupEstablished = false
        }));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("BornRecoverySelectionSnapshot")]
    [DataRow("completion")]
    [DataRow("successor")]
    [DataRow("action-fingerprint")]
    [DataRow("initializer-fingerprint")]
    [DataRow("reservation")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    public async Task LaterBornSelectionRevalidatesCompletionSuccessorsAndEveryOriginalPlanningDependency(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var action = await PrepareSelectionHistoryAsync(catalog, true, false, 1, false);
        var before = await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId);
        string original = SerializeBornOwnerTest(before);
        if (change is "completion" or "successor")
        {
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            var completed = await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId);
            Assert.IsNotNull(completed.LatestCompletion); Assert.AreEqual(action.ActionId, completed.LatestCompletion.ActionId);
            if (change == "successor")
            {
                var successor = action with
                {
                    ActionId = Guid.NewGuid(),
                    PreviousActionId = action.ActionId,
                    Child = completed.Lineage[0],
                    Parent = completed.Parent,
                    PreparedAt = action.PreparedAt.AddSeconds(3)
                };
                await catalog.PrepareNamespaceBornMemberRecoveryAsync(successor);
                var current = await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId);
                Assert.AreEqual(SerializeBornOwnerTest(successor), SerializeBornOwnerTest(current.LatestAction)); Assert.IsNull(current.LatestCompletion);
            }
        }
        else
        {
            await ExecuteSqlAsync(fixture.Paths, change switch
            {
                "action-fingerprint" => $"UPDATE namespace_born_member_recovery_actions SET fingerprint=zeroblob(32) WHERE action_id='{action.ActionId:D}';",
                "initializer-fingerprint" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{action.InitializationOperationId:D}';",
                "reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{action.InitializationOperationId:D}';",
                "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Child.Member.MemberId:D}';",
                "parent-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Parent.Context.Member.MemberId:D}';",
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId));
        }
        Assert.AreEqual(original, SerializeBornOwnerTest(before)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [TestCategory("BornRecoverySelectionSnapshot")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BornSelectionCancellationAndReopenCannotChangeOrInventItsOriginalFacts(bool enabled)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBornMemberRecoveryAction action; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            action = await PrepareSelectionHistoryAsync(catalog, true, enabled, 1, true);
            original = SerializeBornOwnerTest(await catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId, cancelled.Token));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, SerializeBornOwnerTest(await reopened.ReadNamespaceBornMemberRecoverySelectionAsync(action.InitializationOperationId, action.Child.Member.MemberId)));
    }

    [TestMethod]
    [TestCategory("BornRecoverySelectionSnapshot")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BornSelectionRefusesInvalidOrUnknownInitializationWithoutCreatingHistory(bool empty)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (empty) await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceBornMemberRecoverySelectionAsync(Guid.Empty, Guid.NewGuid()));
        else await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceBornMemberRecoverySelectionAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task<MirrorPulseNamespaceBornMemberRecoveryAction> PrepareSelectionHistoryAsync(MirrorPulseProductCatalog catalog,
        bool label, bool enabled, int generations, bool complete)
    {
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
            if (generation == generations && !complete) break;
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
                LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            if (generation == generations) break;
            action = action with
            {
                ActionId = Guid.NewGuid(),
                PreviousActionId = action.ActionId,
                Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(action.InitializationOperationId, action.Child.Member.MemberId))[0],
                Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!,
                PreparedAt = action.PreparedAt.AddSeconds(3)
            };
        }
        return action;
    }
}
