using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("LabelRecoveryReadbackAdmission")]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(true, 4)]
    [DataRow(false, 4)]
    public async Task BothFreshLabelReadbacksRetainBothOriginalAdmissionsAndEveryFactWithFewerQueries(bool enabled, int generations)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var action = setup.Action;
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
                Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, action.Child.Member.MemberId))[0],
                Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!,
                PreparedAt = action.PreparedAt.AddSeconds(3)
            };
        }
        var legacy = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
            var before = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
            var after = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
            Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
            Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after)); return after;
        });
        var combined = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var before = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            var after = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after)); return after;
        });
        Assert.AreEqual(SerializeBornOwnerTest(legacy.Result), SerializeBornOwnerTest(combined.Result));
        Assert.IsLessThan(legacy.Selects, combined.Selects, "Both original current-action admissions and all readback facts must require fewer production queries.");
        Console.WriteLine("LabelRecoveryReadbackSql: " + JsonSerializer.Serialize(new
        {
            enabled,
            generations,
            legacySelects = legacy.Selects,
            combinedSelects = combined.Selects,
            freshSnapshots = 2,
            currentActionAdmissions = 2,
            nativeExecuted = false,
            wallClockSpeedupEstablished = false
        }));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("LabelRecoveryReadbackAdmission")]
    [DataRow("child")]
    [DataRow("parent")]
    [DataRow("prepared")]
    [DataRow("target")]
    [DataRow("version")]
    [DataRow("owner")]
    public async Task CombinedLabelReadbackCannotSubstituteAnotherCallerActionOrParent(string field)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var before = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
        var changed = field switch
        {
            "child" => action with { Child = action.Child with { Location = action.Child.Location with { RelativePath = "Local/foreign.bin" } } },
            "parent" => action with { Parent = action.Parent with { ExpectedDacl = action.Parent.ExpectedDacl + " " } },
            "prepared" => action with { PreparedAt = action.PreparedAt.AddTicks(1) },
            "target" => action with { TargetDacl = action.TargetDacl + " " },
            "version" => action with { Version = 1 },
            "owner" => action with { RootLabelRecoveryOwner = null },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadRootLabelBornRecoverySnapshotAsync(changed));
        Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(await catalog.ReadRootLabelBornRecoverySnapshotAsync(action)));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
    }

    [TestMethod]
    [TestCategory("LabelRecoveryReadbackAdmission")]
    [DataRow("completion")]
    [DataRow("successor")]
    [DataRow("action-fingerprint")]
    [DataRow("initializer-fingerprint")]
    [DataRow("reservation")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    public async Task EachFreshLabelReadbackRechecksTheOriginalActionAndCurrentCatalogState(string change)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, false); var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
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
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadRootLabelBornRecoverySnapshotAsync(action));
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
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelBornRecoverySnapshotAsync(action));
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [TestCategory("LabelRecoveryReadbackAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LabelReadbackCancellationAndCatalogReopenRetainTheOriginalUnfinishedAdmission(bool enabled)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceBornMemberRecoveryAction action; string expected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            action = (await PrepareLabelOwnedBornActionAsync(catalog, enabled)).Action;
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            expected = SerializeBornOwnerTest(await catalog.ReadRootLabelBornRecoverySnapshotAsync(action));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadRootLabelBornRecoverySnapshotAsync(action, cancellation.Token));
            Assert.AreEqual(expected, SerializeBornOwnerTest(await catalog.ReadRootLabelBornRecoverySnapshotAsync(action)));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, SerializeBornOwnerTest(await reopened.ReadRootLabelBornRecoverySnapshotAsync(action)));
        Assert.IsNull(await reopened.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
    }
}
