using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ExplicitDeleteAdmissionOwnsItsInventoryAcrossCallerMutationAndReopen()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
            var callerEntries = original.Entries.ToArray();
            var retained = await catalog.PrepareRemoteDeleteResolutionAsync(original with { Entries = callerEntries });
            callerEntries[1] = callerEntries[1] with { OfficialRemoteRevision = "substituted-after-admission" };
            CollectionAssert.AreEqual(original.Entries.ToArray(), retained.Plan.Entries.ToArray());
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<MirrorPulseNamespaceRemoteRetirementTreeEntry>)retained.Plan.Entries)[0] = callerEntries[1]);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replayed = await reopened.PrepareRemoteDeleteResolutionAsync(original);
        CollectionAssert.AreEqual(original.Entries.ToArray(), replayed.Plan.Entries.ToArray());
        Assert.IsNull(replayed.StartedAt);
    }

    [TestMethod]
    public async Task ReopeningTheDecisionSchemaNeverLowersTheCatalogVersionBetweenSteps()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.IsNotNull(catalog);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        var step = typeof(MirrorPulseProductCatalog).GetMethod("EnsureRemoteConflictActionSchemaAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)step.Invoke(null, [connection, CancellationToken.None])!;
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(93L, (long)(await version.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task ANewExplicitDeleteCannotBorrowPreviewSixRecoveryAuthority()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
        plan = plan with
        {
            Decision = plan.Decision with
            {
                CommandId = Guid.NewGuid(),
                PackageVersion = "0.1.0-preview.6",
                PackageSource = "3caa382e341d260543d3afc05e25fa034fb32e16"
            }
        };
        _ = await catalog.PrepareRemoteConflictActionAsync(plan.Decision);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareRemoteDeleteResolutionAsync(plan));
        Assert.IsNull(await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task AnExplicitRemoteDeleteSealsAllMembersAndRecoversPagesWithoutRewritingItsOriginalMembership()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 65);
            var prepared = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            Assert.AreEqual(65, prepared.NextOrdinal); Assert.IsNull(prepared.StartedAt); Assert.IsNull(prepared.Outcome);
            CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), plan.Entries[0].Original.Context.Member.RootId!.Value);
            _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
            var page = plan.Entries.Reverse().Take(64).Select(entry => DecisionMember(plan, entry)).ToArray();
            var partial = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, page);
            Assert.AreEqual(1, partial.NextOrdinal);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteRemoteDeleteResolutionAsync(
                plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11)));
            Assert.HasCount(2, await catalog.ReadNamespaceMemberTreeAsync(plan.Entries[0].Original.Context.Member.MemberId));
            foreach (var entry in plan.Entries)
                Assert.AreEqual(entry.Original.Context.Member, await catalog.ReadNamespaceMemberAsync(entry.Original.Context.Member.MemberId));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var retained = (await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!;
            Assert.AreEqual(1, retained.NextOrdinal);
            CollectionAssert.AreEqual(plan.Entries.ToArray(), retained.Plan.Entries.ToArray());
            _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId,
                plan.Entries.Take(2).Reverse().Select(entry => DecisionMember(plan, entry)).ToArray());
            var completed = await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId,
                DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
            Assert.IsNotNull(completed.CompletedAt); Assert.AreEqual(-1, completed.NextOrdinal);
            Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(plan.Entries[0].Original.Context.Member.RootId!.Value));
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
            await catalog.RecordRemoteConflictActionReceiptAsync(new(plan.Decision, MirrorPulseRemoteConflictObservedStatus.Applied, null, plan.PreparedAt.AddSeconds(12)));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var completed = (await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!;
            Assert.IsNotNull(completed.CompletedAt); Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
            Assert.AreEqual(MirrorPulseRemoteConflictObservedStatus.Applied, (await catalog.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId))!.Status);
            foreach (var entry in plan.Entries)
            {
                Assert.AreEqual(entry.Original.Context.Member, await catalog.ReadNamespaceMemberAsync(entry.Original.Context.Member.MemberId));
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberLocationAsync(entry.Original.Context.Member.MemberId));
            }
            Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("position")]
    [DataRow("identity")]
    [DataRow("role")]
    [DataRow("parent")]
    public async Task AnExplicitRemoteDeleteCannotAdmitAnIncompleteOrSubstitutedTree(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 2); var entries = plan.Entries.ToArray(); var entry = entries[1];
        if (invalid == "missing") entries = entries[..^1];
        if (invalid == "duplicate") entries[2] = entry with { Ordinal = 2 };
        if (invalid == "position") entries[1] = entry with
        {
            Original = entry.Original with
            { Context = entry.Original.Context with { Location = entry.Original.Context.Location with { RelativePath = "Docs/foreign" } } }
        };
        if (invalid == "identity") entries[1] = entry with { Original = entry.Original with { Identity = entry.Original.Identity with { ItemId = Guid.NewGuid() } } };
        if (invalid == "role") entries[1] = entry with
        {
            Original = entry.Original with
            { Context = entry.Original.Context with { Protection = entry.Original.Context.Protection with { RoleSid = "S-1-5-5-987-654" } } }
        };
        if (invalid == "parent") plan = plan with { Parent = plan.Parent with { Identity = plan.Parent.Identity with { ItemId = Guid.NewGuid() } } };
        async Task AdmitAsync() => _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan with { Entries = entries });
        if (invalid is "duplicate" or "role") await Assert.ThrowsExactlyAsync<ArgumentException>(AdmitAsync);
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(AdmitAsync);
        Assert.IsNull(await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
    }

    [TestMethod]
    public async Task AnExplicitRemoteDeleteCannotUseControlledDeletionOrResolveBeforeItsMembersSettle()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
        _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        var batch = (await catalog.ReadNamespaceRetirementBatchAsync(plan.Decision.CommandId))!;
        Guid member = batch.MemberOperationId(plan.Entries[^1].Original.Context.Member.MemberId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceRetirementExecutionAsync(member));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRetirementStartAsync(member, plan.PreparedAt.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRetirementNativeDeletedAsync(member, plan.PreparedAt.AddSeconds(2)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceRetirementAsync(member, plan.PreparedAt.AddSeconds(3)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceRetirementBatchAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordRemoteConflictActionReceiptAsync(
            new(plan.Decision, MirrorPulseRemoteConflictObservedStatus.Applied, null, plan.PreparedAt.AddSeconds(4))));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[^1])]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan)));
        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Prepared, (await catalog.ReadNamespaceRetirementAsync(member))!.Phase);
    }

    [TestMethod]
    [DataRow("parent-first")]
    [DataRow("native-present")]
    [DataRow("wrong-revision")]
    public async Task ExplicitDeleteSettlementCannotSkipChildrenOrInventTheirNativeAndSdkFacts(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
        _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
        var member = DecisionMember(plan, plan.Entries[^1]);
        member = invalid switch
        {
            "parent-first" => DecisionMember(plan, plan.Entries[0]),
            "native-present" => member with { NativeMissing = false },
            _ => member with { OfficialRemoteRevision = "substituted" },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [member]));
        Assert.AreEqual(1, (await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!.NextOrdinal);
        Assert.HasCount(2, await catalog.ReadNamespaceMemberTreeAsync(plan.Entries[0].Original.Context.Member.MemberId));
    }

    [TestMethod]
    public async Task AFailedMemberSettlementRollsBackItsNameRetirementAndDecisionProgressTogether()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER lose_settlement BEFORE INSERT ON remote_delete_resolution_members BEGIN SELECT RAISE(FAIL,'Synthetic member observation write loss'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[^1])]));
            Assert.AreEqual(1, (await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!.NextOrdinal);
            Assert.HasCount(2, await catalog.ReadNamespaceMemberTreeAsync(plan.Entries[0].Original.Context.Member.MemberId));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER lose_settlement;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        _ = await reopened.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[^1])]);
        Assert.AreEqual(0, (await reopened.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!.NextOrdinal);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("member")]
    public async Task CorruptDecisionEvidenceCannotReleaseItsFenceAfterReopen(string damaged)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
            _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[^1])]);
        }
        await ExecuteSqlAsync(fixture.Paths, damaged == "owner" ? "UPDATE remote_delete_resolutions SET fingerprint=zeroblob(32);"
            : "UPDATE remote_delete_resolution_members SET fingerprint=zeroblob(32);");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (damaged == "owner") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        else
        {
            var batch = (await reopened.ReadNamespaceRetirementBatchAsync(plan.Decision.CommandId))!;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceRetirementAsync(
                batch.MemberOperationId(plan.Entries[^1].Original.Context.Member.MemberId)));
        }
        if (damaged == "owner") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.CompleteRemoteDeleteResolutionAsync(
            plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11)));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.CompleteRemoteDeleteResolutionAsync(
            plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11)));
    }

    [TestMethod]
    public async Task AnExplicitDecisionCannotRewriteTheOriginalRejectedPollOutcomeOrItsPackagePolicy()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRetirementIntent original; MirrorPulseNamespaceRemoteRetirementOutcome rejected;
        MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteTreeAsync(catalog, 0); original = setup.Intent;
            _ = await catalog.PrepareNamespaceRetirementAsync(original);
            _ = await catalog.RecordNamespaceRetirementStartAsync(original.OperationId, original.PreparedAt,
                original.RemoteEntry!.ItemId);
            rejected = RemoteRetirementOutcome(original, true);
            _ = await catalog.SettleNamespaceRemoteRetirementAsync(rejected);
            var conflict = new MirrorPulseConflictRecord(rejected.ConflictId!.Value, original.RemoteEntry.InstanceId,
                original.RemoteEntry.ChangeId, original.RelativePath, MirrorPulseConflictReason.Delete, MirrorPulseVersionComparison.Unknown,
                null, original.RemoteEntry.RemoteRevision, rejected.ConflictDetectedAt!.Value, source: MirrorPulseConflictSource.CfSharpRemote);
            var decision = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepRemote,
                null, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
            _ = await catalog.PrepareRemoteConflictActionAsync(decision);
            plan = new(1, decision, original.RemoteEntry.BatchFingerprint, original.RemoteEntry.Parent, setup.Entries, rejected.VerifiedAt.AddSeconds(1));
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            _ = await catalog.StartRemoteDeleteResolutionAsync(decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(decision.CommandId, DecisionOutcome(plan));
            _ = await catalog.SettleRemoteDeleteResolutionPageAsync(decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
            _ = await catalog.CompleteRemoteDeleteResolutionAsync(decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var historical = (await reopened.ReadNamespaceRemoteRetirementAsync(original.RemoteEntry!.InstanceId, original.RemoteEntry.BatchId, original.RemoteEntry.Ordinal))!;
        Assert.AreEqual(original, historical.Intent); Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.RemoteRejected, historical.Phase);
        Assert.AreEqual(rejected, await reopened.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId));
        Assert.AreEqual("0.1.0-preview.6", historical.Intent.RemoteEntry!.PackageVersion);
        Assert.AreEqual(MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, (await reopened.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!.Plan.Decision.PackageVersion);
        Assert.AreEqual(original.RemoteEntry.Child.Context.Member, await reopened.ReadNamespaceMemberAsync(original.MemberId));
    }

    [TestMethod]
    [DataRow("owner-deleted")]
    [DataRow("observation-deleted")]
    [DataRow("retirement-deleted")]
    public async Task AnExplicitDecisionCannotLoseItsOwnerOrMemberObservationAndBecomeControlledDeletion(string damaged)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan; Guid child;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
            _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[^1])]);
            child = (await catalog.ReadNamespaceRetirementBatchAsync(plan.Decision.CommandId))!.MemberOperationId(plan.Entries[^1].Original.Context.Member.MemberId);
        }
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "owner-deleted" => "PRAGMA foreign_keys=OFF; DELETE FROM remote_delete_resolutions;",
            "observation-deleted" => "DELETE FROM remote_delete_resolution_members;",
            _ => "DELETE FROM namespace_member_retirements WHERE phase=2;",
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (damaged == "retirement-deleted")
        {
            _ = await reopened.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.CompleteRemoteDeleteResolutionAsync(
                plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11)));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ValidateNamespaceRetirementExecutionAsync(child));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        }
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
        Assert.IsNull(await reopened.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
    }

    [TestMethod]
    [DataRow(MirrorPulseConflictAction.KeepLocal)]
    [DataRow(MirrorPulseConflictAction.KeepRemote)]
    public async Task AnAdmittedDeleteOwnsItsSdkConflictAcrossOtherCommandChoices(MirrorPulseConflictAction action)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var competing = plan.Decision with { CommandId = Guid.NewGuid(), Action = action };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareRemoteConflictActionAsync(competing));
        Assert.IsNull(await reopened.ReadUserCommandAsync(competing.CommandId));
        Assert.IsNull(await reopened.PrepareRemoteConflictActionAsync(plan.Decision));
        CollectionAssert.Contains((await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), plan.Entries[0].Original.Context.Member.RootId!.Value);
    }

    [TestMethod]
    public async Task DeletingTheResolutionBatchCannotHideItsOwnedRootFence()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_retirement_batches;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.RecordRemoteConflictActionReceiptAsync(
            new(plan.Decision, MirrorPulseRemoteConflictObservedStatus.Applied, null, plan.PreparedAt.AddSeconds(10))));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
    }

    [TestMethod]
    public async Task AGenericResolvedWriteCannotBypassTheProtectedDeleteDecision()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
        _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveUserCommandAsync(
            new(plan.Decision.CommandId, "keepremote", plan.Decision.Conflict.ConflictId.ToString("D"), "resolved")));
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
    }

    private static async Task<MirrorPulseRemoteDeleteResolutionPlan> PrepareDeleteDecisionFixtureAsync(MirrorPulseProductCatalog catalog, int children)
    {
        var setup = await PrepareRemoteTreeAsync(catalog, children); var remote = setup.Intent.RemoteEntry!;
        var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), remote.InstanceId, remote.ChangeId,
            remote.Child.Context.Location.RelativePath, MirrorPulseConflictReason.Delete, MirrorPulseVersionComparison.Unknown,
            null, remote.RemoteRevision, setup.Intent.PreparedAt, source: MirrorPulseConflictSource.CfSharpRemote);
        var decision = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepRemote, null,
            MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
        _ = await catalog.PrepareRemoteConflictActionAsync(decision);
        return new(1, decision, remote.BatchFingerprint, remote.Parent, setup.Entries, setup.Intent.PreparedAt.AddSeconds(1));
    }

    private static MirrorPulseNamespacePermissionVerification DecisionParent(MirrorPulseRemoteDeleteResolutionPlan plan, int seconds) =>
        plan.Parent.Context.Verification with { ObservedAt = plan.PreparedAt.AddSeconds(seconds) };
    private static MirrorPulseRemoteDeleteResolutionOutcome DecisionOutcome(MirrorPulseRemoteDeleteResolutionPlan plan) =>
        new(MirrorPulseRemoteDeleteResolutionStatus.Applied, plan.PreparedAt.AddSeconds(4), DecisionParent(plan, 3));
    private static MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement DecisionMember(MirrorPulseRemoteDeleteResolutionPlan plan,
        MirrorPulseNamespaceRemoteRetirementTreeEntry entry) => new(1, plan.Decision.CommandId, entry.Ordinal,
        entry.Original.Context.Member.MemberId, true, true, entry.Ordinal == 0 ? plan.Decision.Conflict.RemoteRevision : entry.OfficialRemoteRevision,
        plan.PreparedAt.AddSeconds(7), DecisionParent(plan, 6), null);
}
