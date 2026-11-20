using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("Prepared", false)]
    [DataRow("InvocationMayHaveBegun", false)]
    [DataRow("Applied", false)]
    [DataRow("Applied", true)]
    [DataRow("Conflict", false)]
    [DataRow("Conflict", true)]
    public async Task RemoteUpdateStatusRetainsOriginalOwnershipAndUnknownOutcomeAfterReopen(string stage, bool reconciled)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRemoteUpdatePlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
            await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
            if (stage != "Prepared") await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
            if (stage is "Applied" or "Conflict") await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, stage == "Conflict", reconciled));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceRemoteUpdateStatusAsync(plan.OperationId))!;
        Assert.AreEqual(stage is "Applied" or "Conflict" ? "ProductSettled" : stage, actual.Stage);
        Assert.AreEqual(plan.OperationId, actual.OperationId); Assert.AreEqual(plan.Child.Context.Member.RootId, actual.RootId);
        Assert.AreEqual(plan.InstanceId, actual.InstanceId); Assert.AreEqual(plan.Child.Context.Member.MemberId, actual.MemberId);
        Assert.AreEqual(plan.BatchId, actual.BatchId); Assert.AreEqual(plan.Ordinal, actual.Ordinal); Assert.AreEqual(plan.ChangeId, actual.ChangeId);
        Assert.AreEqual(plan.Child.Context.Location.RelativePath, actual.RelativePath);
        Assert.AreEqual(plan.PreparedAt, actual.PreparedAt); Assert.AreEqual(stage != "Prepared", actual.StartedAt is not null);
        Assert.AreEqual(plan.PackageVersion, actual.PackageVersion); Assert.AreEqual(plan.PackageSource, actual.PackageSource);
        if (stage is "Applied" or "Conflict")
        {
            Assert.AreEqual(stage, actual.Outcome); Assert.IsNotNull(actual.OutcomeObservedAt);
            Assert.AreEqual(reconciled ? "RetainedSdkProgress" : "SdkReturn", actual.EvidenceSource);
            Assert.IsEmpty(await reopened.ReadPendingNamespaceRemoteUpdatesAsync(actual.RootId));
        }
        else
        {
            Assert.IsNull(actual.Outcome); Assert.IsNull(actual.EvidenceSource); Assert.IsNull(actual.OutcomeObservedAt);
            Assert.AreEqual(plan, (await reopened.ReadPendingNamespaceRemoteUpdatesAsync(actual.RootId)).Single().Plan);
            CollectionAssert.Contains((await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), actual.RootId);
        }
        Assert.AreEqual(plan, (await reopened.ReadNamespaceRemoteUpdateAsync(plan.OperationId))!.Plan);
        Assert.IsNull(await reopened.ReadNamespaceRemoteUpdateStatusAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reopened.ReadNamespaceRemoteUpdateStatusAsync(Guid.Empty));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RemoteUpdateStatusRemainsHistoricalAfterLegitimateParentRoleRotation()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRemoteUpdateStatus expected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
            await catalog.PrepareNamespaceRemoteUpdateAsync(plan); await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
            var terminal = await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, true, true));
            expected = (await catalog.ReadNamespaceRemoteUpdateStatusAsync(plan.OperationId))!;
            await RotateContextOriginalParentsAsync(catalog, setup, terminal.Outcome!.VerifiedAt.AddSeconds(1));
            Assert.AreEqual(expected, await catalog.ReadNamespaceRemoteUpdateStatusAsync(plan.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, await reopened.ReadNamespaceRemoteUpdateStatusAsync(expected.OperationId));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceRemoteUpdatesAsync(expected.RootId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("fence")]
    [DataRow("completion")]
    public async Task RemoteUpdateStatusRejectsCorruptedOwnershipInsteadOfReportingCompletion(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
        if (damaged == "completion")
        {
            await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
            await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, false, false));
        }
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "fingerprint" => "UPDATE namespace_remote_updates SET fingerprint=zeroblob(32);",
            "fence" => "DELETE FROM namespace_remote_update_pending;",
            _ => "DELETE FROM namespace_remote_update_completions;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteUpdateStatusAsync(plan.OperationId));
    }
}
