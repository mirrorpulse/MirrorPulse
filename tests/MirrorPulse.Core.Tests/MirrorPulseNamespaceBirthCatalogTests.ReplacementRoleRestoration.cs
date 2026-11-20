using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementNewRoleRestoresWithoutRewritingAnEarlierVerifiedOrInterruptedScope(bool previouslyVerified)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution expected;
        MirrorPulseNamespaceReplacementRestorationResult? priorResult = null;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var first = await PrepareReplacementRestorationFixtureAsync(catalog);
            var original = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
            if (previouslyVerified)
            {
                priorResult = ReplacementRestorationResult(first, original, "Completed", true);
                await catalog.RecordNamespaceReplacementRestorationResultAsync(priorResult);
            }
            var role = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
            await catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(role, original.Intent, false));
            var next = first with
            {
                RestorationId = Guid.NewGuid(),
                PreviousRestorationId = first.RestorationId,
                ActionId = role.RecoveryId,
                PreparedAt = role.Action.PreparedAt.AddSeconds(2)
            };
            await catalog.PrepareNamespaceReplacementRestorationAsync(next);
            var result = ReplacementRestorationResult(next, original with { Action = role.Action }, "Completed", true);
            await catalog.RecordNamespaceReplacementRestorationResultAsync(result);
            expected = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
            Assert.AreEqual(original.Action, expected.Action); Assert.AreEqual(original.Backup, expected.Backup);
            Assert.AreEqual(original.ContentCompletion, expected.ContentCompletion); Assert.AreEqual(next, expected.Restoration);
            Assert.AreEqual(result, expected.RestorationResult);
            Assert.AreEqual(original.Intent.Original.Context.Protection.RoleSid, next.Parent.Context.Protection.RoleSid);
            Assert.AreNotEqual(role.Action.RoleSid, next.Parent.Context.Protection.RoleSid);
            if (priorResult is not null) Assert.AreEqual(priorResult, await catalog.RecordNamespaceReplacementRestorationResultAsync(priorResult));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceReplacementExecutionAsync(expected.Intent.OperationId))!;
        Assert.AreEqual(expected.Restoration, actual.Restoration); Assert.AreEqual(expected.RestorationResult, actual.RestorationResult);
        Assert.AreEqual(expected.Action, actual.Action); Assert.AreEqual(expected.Backup, actual.Backup);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceReplacementRestorationAsync(expected.Restoration! with
        { RestorationId = Guid.NewGuid(), PreviousRestorationId = expected.Restoration.RestorationId, PreparedAt = expected.RestorationResult!.RecordedAt.AddSeconds(1) }));
        CollectionAssert.AreEqual(new[] { expected.Intent.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("unobserved")]
    [DataRow("old-role")]
    [DataRow("late-result")]
    public async Task ReplacementRestorationRejectsUnobservedRolesAndLateSupersededScopeResults(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = await PrepareReplacementRestorationFixtureAsync(catalog);
        var original = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        var role = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
        if (changed != "unobserved") await catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(role, original.Intent, false));
        var next = first with
        {
            RestorationId = Guid.NewGuid(),
            PreviousRestorationId = first.RestorationId,
            ActionId = changed == "old-role" ? first.ActionId : role.RecoveryId,
            PreparedAt = role.Action.PreparedAt.AddSeconds(2)
        };
        if (changed == "late-result")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRestorationResultAsync(
                ReplacementRestorationResult(first, original, "Completed", true) with { RecordedAt = next.PreparedAt }));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRestorationAsync(next));
        var actual = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        Assert.AreEqual(first, actual.Restoration); Assert.IsNull(actual.RestorationResult);
    }
}
