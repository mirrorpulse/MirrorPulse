using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task RegisteredBirthReadbackRetainsAllOriginalFactsAcrossFreshSnapshotsWithoutCatalogWrites(bool directory, bool moved)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory);
        if (moved) await CompleteMoveChainStepAsync(catalog, setup.Move);
        await CheckAsync(setup.Member.MemberId, moved ? "moved" : "original");
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));

        async Task CheckAsync(Guid memberId, string boundary)
        {
            var legacy = await MeasureRegisteredBirthReadAsync(catalog,
                () => ReadLegacyRegisteredBirthReadbackAsync(catalog, memberId, null));
            var coherent = await MeasureRegisteredBirthReadAsync(catalog, async () =>
            {
                var before = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId);
                var after = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId);
                Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after));
                return after;
            });
            Assert.AreEqual(SerializeBornOwnerTest(legacy.Result), SerializeBornOwnerTest(coherent.Result));
            Assert.IsLessThan(legacy.Selects, coherent.Selects, "Both fresh snapshots must retain every original fact with fewer catalog queries.");
            Console.WriteLine("RegisteredBirthReadSql: " + JsonSerializer.Serialize(new
            {
                boundary,
                directory,
                legacySelects = legacy.Selects,
                snapshotSelects = coherent.Selects,
                freshSnapshots = 2,
                nativeExecuted = false,
                wallClockSpeedupEstablished = false
            }));
        }
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task RegisteredBirthSnapshotRetainsAnAncestorDirectorySettlementAndBornParent()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var tree = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        await catalog.CompleteNamespaceDirectoryMoveAsync(tree.Completion);
        foreach (var member in tree.Members.Skip(1))
        {
            var legacy = await ReadLegacyRegisteredBirthReadbackAsync(catalog, member.MemberId, null);
            var coherent = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(member.MemberId);
            Assert.AreEqual(SerializeBornOwnerTest(legacy), SerializeBornOwnerTest(coherent));
            Assert.AreEqual(member, coherent.Member);
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task LabelOwnedRegisteredReadbackRetainsFullRecoveryParentAndBothIndependentAdmissions()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
            await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var legacy = await MeasureRegisteredBirthReadAsync(catalog,
            () => ReadLegacyRegisteredBirthReadbackAsync(catalog, action.Child.Member.MemberId, action.Parent));
        var coherent = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var before = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
            var after = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
            Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after));
            return after;
        });
        Assert.AreEqual(SerializeBornOwnerTest(legacy.Result), SerializeBornOwnerTest(coherent.Result));
        Assert.IsLessThan(legacy.Selects, coherent.Selects);
        Console.WriteLine("RegisteredBirthReadSql: " + JsonSerializer.Serialize(new
        {
            boundary = "label-recovery",
            legacySelects = legacy.Selects,
            snapshotSelects = coherent.Selects,
            freshSnapshots = 2,
            nativeExecuted = false,
            wallClockSpeedupEstablished = false
        }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId));
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
            await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)).Completion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    [DataRow("context")]
    [DataRow("identity")]
    [DataRow("dacl")]
    [DataRow("permission")]
    [DataRow("born-parent")]
    [DataRow("observation")]
    public async Task RegisteredRecoveryRequiresEveryFieldOfItsAdmittedParentSnapshot(string change)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var parent = setup.Action.Parent;
        Guid memberId = setup.Action.Child.Member.MemberId;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(setup.Action);
        var original = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId, parent);
        var wrong = change switch
        {
            "context" => parent with { Context = parent.Context with { Verification = parent.Context.Verification with { ObservedAt = parent.Context.Verification.ObservedAt.AddTicks(1) } } },
            "identity" => parent with { Identity = parent.Identity with { ItemId = Guid.NewGuid() } },
            "dacl" => parent with { ExpectedDacl = parent.ExpectedDacl + "changed" },
            "permission" => parent with { PermissionOperationId = Guid.NewGuid() },
            "born-parent" => parent with { BornParent = new(Guid.NewGuid(), Guid.NewGuid()) },
            "observation" => parent with { Observation = setup.Action.Child.BirthObservation },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.AreNotEqual(parent, wrong);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId, wrong));
        Assert.AreEqual(SerializeBornOwnerTest(original), SerializeBornOwnerTest(await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId, parent)));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(setup.Action.ActionId));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    [DataRow("child-member")]
    [DataRow("parent-member")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    [DataRow("birth")]
    [DataRow("observation")]
    [DataRow("protection")]
    public async Task RegisteredBirthSnapshotCannotHideChangedCatalogFactsInALaterRead(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Guid member = setup.Member.MemberId, parent = setup.ParentMember.MemberId, birth = setup.Member.OriginId;
        await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(member);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "child-member" => $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{member:D}';",
            "parent-member" => $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{parent:D}';",
            "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{member:D}';",
            "parent-name" => $"DELETE FROM namespace_member_names WHERE member_id='{parent:D}';",
            "birth" => $"UPDATE namespace_birth_intents SET fingerprint=zeroblob(32) WHERE operation_id='{birth:D}';",
            "observation" => $"UPDATE namespace_birth_observations SET fingerprint=zeroblob(32) WHERE operation_id='{birth:D}';",
            "protection" => $"UPDATE namespace_birth_protections SET fingerprint=zeroblob(32) WHERE protection_id='{setup.Born.Protection.ProtectionId:D}';",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(member));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task RegisteredRecoveryCannotReuseItsEarlierParentEpochOrReplaceExecutionAdmission()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
        foreach (var member in new[] { setup.Tree.Anchor, setup.Tree.Root })
        {
            var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(member.OriginId))!;
            var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(member.OriginId))!;
            await ProtectAsync(catalog, baseline, RotateOriginal(previous.Intent, "S-1-5-5-123-459", action.PreparedAt.AddSeconds(1)));
        }
        var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!;
        Assert.AreNotEqual(action.Parent, current);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent));
        Assert.AreEqual(current.Context, (await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, current)).Parent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task RegisteredBirthSnapshotsObserveNewPositionsAndProtectionEpochsAfterReopen()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseRegisteredNamespaceBirthSnapshot first;
        MirrorPulseNamespaceBirthProtection next;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            first = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId);
            await CompleteMoveChainStepAsync(catalog, setup.Move);
            next = await LocatedBornEpochAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(10));
            await catalog.RecordNamespaceBirthProtectionAsync(next);
            var after = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId);
            Assert.AreNotEqual(first.Location, after.Location);
            Assert.AreEqual(next, after.LatestProtection);
            Assert.AreEqual(first.Member, after.Member);
            Assert.AreEqual(first.Original, after.Original);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(next, (await reopened.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId)).LatestProtection);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task RegisteredBirthSnapshotCannotReturnARetiredMemberFromAnEarlierRead()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId);
        var retirement = Retire(setup, Guid.NewGuid());
        await catalog.PrepareNamespaceRetirementAsync(retirement);
        await catalog.RecordNamespaceRetirementStartAsync(retirement.OperationId, retirement.PreparedAt);
        await catalog.RecordNamespaceRetirementNativeDeletedAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(1));
        await catalog.CompleteNamespaceRetirementAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(2));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RegisteredBirthReadSnapshot")]
    public async Task RegisteredBirthSnapshotsRefuseMissingOriginalAndCancelledRequestsWithoutPoisoningTheNextRead()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(Guid.Empty));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.ParentMember.MemberId));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId, cancellationToken: cancelled.Token));
        Assert.AreEqual(setup.Member, (await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(setup.Member.MemberId)).Member);
    }

    private static async Task<MirrorPulseRegisteredNamespaceBirthSnapshot> ReadLegacyRegisteredBirthReadbackAsync(
        MirrorPulseProductCatalog catalog, Guid memberId, MirrorPulseNamespaceMemberProtectionSnapshot? recoveryParent)
    {
        var member = (await catalog.ReadNamespaceMemberAsync(memberId))!;
        var birth = (await catalog.ReadNamespaceBirthAsync(member.OriginId))!;
        var original = (await catalog.ReadNamespaceBirthObservationAsync(member.OriginId))!;
        var location = await catalog.ReadNamespaceMemberLocationAsync(memberId);
        var parent = await ParentAsync();
        var identity = await catalog.ReadNamespaceMemberIdentityAsync(memberId);
        var parentIdentity = await catalog.ReadNamespaceMemberIdentityAsync(parent.Member.MemberId);
        Assert.AreEqual(location, await catalog.ReadNamespaceMemberLocationAsync(memberId));
        Assert.AreEqual(identity, await catalog.ReadNamespaceMemberIdentityAsync(memberId));
        Assert.AreEqual(parentIdentity, await catalog.ReadNamespaceMemberIdentityAsync(parent.Member.MemberId));
        Assert.AreEqual(parent, await ParentAsync());
        var latest = (await catalog.ReadLatestNamespaceBirthProtectionAsync(member.OriginId))!;
        return new(member, birth, original, location, parent, identity, parentIdentity, latest);

        async Task<MirrorPulseNamespaceMemberProtectionContext> ParentAsync()
        {
            if (recoveryParent is null) return (await catalog.ReadNamespaceMemberProtectionLineageAsync(location.ParentMemberId!.Value))[0];
            var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(location.ParentMemberId!.Value))!;
            Assert.AreEqual(recoveryParent, current);
            return current.Context;
        }
    }

    private static async Task<(T Result, int Selects)> MeasureRegisteredBirthReadAsync<T>(MirrorPulseProductCatalog catalog, Func<Task<T>> read)
    {
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        long changes = TotalChanges();
        int selects = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) => { if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selects++; };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        T result;
        try { result = await read(); }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        Assert.AreEqual(changes, TotalChanges(), "Registered verification reads must not modify catalog state.");
        return (result, selects);

        long TotalChanges()
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT total_changes();";
            return (long)command.ExecuteScalar()!;
        }
    }
}
