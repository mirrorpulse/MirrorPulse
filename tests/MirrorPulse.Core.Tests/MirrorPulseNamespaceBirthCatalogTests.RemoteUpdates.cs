using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task OriginalRemoteUpdateRetainsItsEntryAndSameObjectAcrossOwners(bool directory, bool conflict, bool reconciled)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRemoteUpdatePlan plan;
        MirrorPulseNamespaceMemberRegistration member;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first, directory); member = setup.Member;
            plan = await RemoteUpdatePlanAsync(first, member);
            var retained = await first.PrepareNamespaceRemoteUpdateAsync(plan);
            Assert.AreEqual(retained, await first.PrepareNamespaceRemoteUpdateAsync(plan));
            Assert.IsTrue(await first.CanResumeNamespaceRemoteUpdateAsync(plan));
            Assert.HasCount(1, await first.ReadPendingNamespaceRemoteUpdatesAsync(plan.InstanceId, plan.BatchId));
            CollectionAssert.Contains((await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), member.RootId!.Value);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PrepareNamespaceMemberMoveAsync(setup.Move));
            await first.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        }
        MirrorPulseNamespaceRemoteUpdate terminal;
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var pending = (await second.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal))!;
            Assert.AreEqual(plan, pending.Plan); Assert.IsNotNull(pending.StartedAt); Assert.IsNull(pending.Outcome);
            var outcome = RemoteUpdateOutcome(plan, conflict, reconciled);
            terminal = await second.SettleNamespaceRemoteUpdateAsync(outcome);
            Assert.AreEqual(terminal, await second.SettleNamespaceRemoteUpdateAsync(outcome));
            Assert.IsFalse(await second.CanResumeNamespaceRemoteUpdateAsync(plan));
            Assert.IsEmpty(await second.ReadPendingNamespaceRemoteUpdatesAsync(plan.InstanceId, plan.BatchId));
            Assert.IsEmpty(await second.ReadNamespaceDispatchFencedRootsAsync());
            Assert.AreEqual(member, await second.ReadNamespaceMemberAsync(member.MemberId));
            Assert.AreEqual(member, await second.ReadNamespaceMemberChildAsync(plan.Parent.Context.Member.MemberId, "new.txt"));
        }
        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(terminal, await third.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal));
        Assert.AreEqual(terminal, await third.PrepareNamespaceRemoteUpdateAsync(plan));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.PrepareNamespaceRemoteUpdateAsync(plan with { RemoteRevision = "different-entry" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.SettleNamespaceRemoteUpdateAsync(
            terminal.Outcome! with { VerifiedAt = terminal.Outcome!.VerifiedAt.AddSeconds(1) }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task CompletedRemoteUpdateHistorySurvivesLaterOriginalParentRoleRotation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan); await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
        var terminal = await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, false, false));
        await RotateContextOriginalParentsAsync(catalog, setup, terminal.Outcome!.VerifiedAt.AddSeconds(1));
        Assert.AreEqual(terminal, await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal));
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteUpdateAsync(plan));
        Assert.AreEqual(Role, terminal.Plan.Parent.Context.Protection.RoleSid);
        Assert.AreEqual(RelocatedRole, (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.ParentMember.MemberId))!.Context.Protection.RoleSid);
    }

    [TestMethod]
    public async Task Schema78MigrationPreservesMembersAndCreatesNoRemoteUpdateHistory()
    {
        using var fixture = new Fixture(); MoveFixture setup; string before;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first); before = await ReadMemberMigrationBytesAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_update_completions; DROP TABLE namespace_remote_update_pending; DROP TABLE namespace_remote_updates; PRAGMA user_version=78;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(before, await ReadMemberMigrationBytesAsync(fixture.Paths));
        var instance = InstanceId.New(); Assert.IsNull(await reopened.ReadNamespaceRemoteUpdateAsync(instance, instance + "/original", 0));
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(82L, await version.ExecuteScalarAsync());
    }

    private static async Task<MirrorPulseNamespaceRemoteUpdatePlan> RemoteUpdatePlanAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member)
    {
        var child = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!;
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.Context.Location.ParentMemberId!.Value))!;
        DateTimeOffset prepared = new[] { DateTimeOffset.UtcNow, child.Context.Verification.ObservedAt, parent.Context.Verification.ObservedAt }.Max().AddSeconds(1);
        var instance = InstanceId.New();
        return new(1, Guid.NewGuid(), instance, instance + "/original-update", new string('A', 64), 3, 1, "versioned-upsert", new string('B', 64),
            child.Identity.ItemId!.Value, child.Identity.RemoteId!, "v1", "v2", "v1", "v1", true, child, parent,
            child.Context.Verification with { ObservedAt = prepared }, parent.Context.Verification with { ObservedAt = prepared },
            "0.1.0-preview.7", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9", new(true, true, TimeSpan.FromMinutes(5).Ticks, 2), prepared);
    }

    private static MirrorPulseNamespaceRemoteUpdateOutcome RemoteUpdateOutcome(MirrorPulseNamespaceRemoteUpdatePlan plan, bool conflict, bool reconciled)
    {
        DateTimeOffset observed = plan.PreparedAt.AddSeconds(3);
        return new(1, plan.OperationId, conflict ? MirrorPulseNamespaceRemoteUpdateDisposition.Conflict : MirrorPulseNamespaceRemoteUpdateDisposition.Applied,
            reconciled ? MirrorPulseNamespaceRemoteUpdateEvidenceSource.RetainedSdkProgress : MirrorPulseNamespaceRemoteUpdateEvidenceSource.SdkReturn,
            conflict ? Guid.NewGuid() : null, conflict ? plan.PreparedAt.AddSeconds(2) : null,
            conflict ? plan.NativeRemoteRevision : plan.RemoteRevision, conflict ? plan.OfficialRemoteRevision : plan.RemoteRevision,
            plan.NativeIsPlaceholder, !conflict, plan.Child.Context.Verification with { ObservedAt = observed },
            plan.Parent.Context.Verification with { ObservedAt = observed }, observed.AddSeconds(1));
    }
}
