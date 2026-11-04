using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(true, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    public async Task BornExecutionContextRetainsEveryOriginalActionAndExactPendingOrHistoricalInputs(bool label, bool enabled, bool complete)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var action = await PrepareSelectionHistoryAsync(catalog, label, enabled, 4, complete);
        var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId);
        var completion = await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId);
        IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> lineage = complete ? []
            : await catalog.ReadNamespaceMemberRecoveryLineageAsync(action.InitializationOperationId, action.Child.Member.MemberId);
        var parent = complete ? null : await catalog.ReadNamespaceMemberProtectionSnapshotAsync(lineage[0].Location.ParentMemberId!.Value);
        var expected = new MirrorPulseBornMemberRecoveryExecutionContext(history, action, completion, lineage, parent);
        var actual = await MeasureRegisteredBirthReadAsync(catalog, () => catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(
            action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId));
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual.Result));
        var historical = await catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId,
            action.Child.Member.MemberId, history[0].ActionId);
        Assert.AreEqual(history[0], historical.SelectedAction);
        Assert.IsNotNull(historical.SelectedCompletion);
        Assert.HasCount(0, historical.Lineage); Assert.IsNull(historical.Parent);
        Console.WriteLine("BornExecutionContextSql: " + JsonSerializer.Serialize(new { label, enabled, complete, actual.Selects }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("initializer")]
    [DataRow("reservation")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    [DataRow("completion")]
    public async Task BornExecutionContextCannotReuseDamagedFactsFromItsPreviousTransaction(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var action = await PrepareSelectionHistoryAsync(catalog, true, false, 4, damage == "completion");
        _ = await catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "action" => $"UPDATE namespace_born_member_recovery_actions SET fingerprint=zeroblob(32) WHERE action_id='{action.ActionId:D}';",
            "initializer" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{action.InitializationOperationId:D}';",
            "reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{action.InitializationOperationId:D}';",
            "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Child.Member.MemberId:D}';",
            "parent-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Parent.Context.Member.MemberId:D}';",
            "completion" => $"UPDATE namespace_born_member_recovery_completions SET fingerprint=zeroblob(32) WHERE action_id='{action.ActionId:D}';",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(
            action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId));
    }

    [TestMethod]
    public async Task BornExecutionContextRechecksCompletionCancellationInitializerAndMissingActionAcrossReopen()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBornMemberRecoveryAction action;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            action = await PrepareSelectionHistoryAsync(catalog, true, false, 1, false);
            var pending = await catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId);
            Assert.IsNull(pending.SelectedCompletion); Assert.IsNotEmpty(pending.Lineage); Assert.IsNotNull(pending.Parent);
            var missing = await catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId, action.Child.Member.MemberId, Guid.NewGuid());
            Assert.IsNull(missing.SelectedAction); Assert.IsNull(missing.SelectedCompletion); Assert.IsNotEmpty(missing.Lineage);
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
            Assert.IsNotNull((await catalog.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId,
                action.Child.Member.MemberId, action.ActionId)).SelectedCompletion);
            Assert.IsNull(pending.SelectedCompletion);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await reopened.ReadNamespaceBornMemberRecoveryExecutionContextAsync(action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId);
        Assert.IsNotNull(completed.SelectedCompletion); Assert.HasCount(0, completed.Lineage); Assert.IsNull(completed.Parent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceBornMemberRecoveryExecutionContextAsync(
            Guid.NewGuid(), action.Child.Member.MemberId, action.ActionId));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reopened.ReadNamespaceBornMemberRecoveryExecutionContextAsync(
            action.InitializationOperationId, action.Child.Member.MemberId, action.ActionId, cancelled.Token));
    }
}
