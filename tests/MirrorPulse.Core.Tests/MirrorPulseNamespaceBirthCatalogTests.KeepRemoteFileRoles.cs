using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteRoleHandoffRetainsLostReadbackAndOriginalNativeAuthorityAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseConflictKeepRemoteRoleStart lost, recovered;
        MirrorPulseConflictKeepRemoteRoleResult result;
        NativeSetup native;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            native = await PrepareKeepRemoteNativeAsync(catalog);
            await catalog.RecordConflictKeepRemoteNativeStartAsync(native.Start);
            await catalog.RecordConflictKeepRemoteNativeResultAsync(native.Result);
            lost = await NewKeepRemoteRoleStartAsync(catalog, native, "S-1-5-5-123-461");
            Assert.AreEqual(lost, await catalog.RecordConflictKeepRemoteRoleStartAsync(lost));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(lost, (await reopened.ReadConflictKeepRemoteRoleAttemptsAsync(lost.CommandId)).Single().Start);
            Assert.IsNull((await reopened.ReadConflictKeepRemoteRoleAttemptsAsync(lost.CommandId)).Single().Result);
            recovered = await NewKeepRemoteRoleStartAsync(reopened, native, "S-1-5-5-123-462", lost);
            await reopened.RecordConflictKeepRemoteRoleStartAsync(recovered);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RecordConflictKeepRemoteRoleResultAsync(KeepRemoteRoleResult(lost)));
            result = await reopened.RecordConflictKeepRemoteRoleResultAsync(KeepRemoteRoleResult(recovered));
        }
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { new MirrorPulseConflictKeepRemoteRoleAttempt(lost, null), new(recovered, result) },
            (await current.ReadConflictKeepRemoteRoleAttemptsAsync(lost.CommandId)).ToArray());
        Assert.AreEqual(result, await current.RecordConflictKeepRemoteRoleResultAsync(result));
        Assert.AreEqual(native.Result, (await current.ReadConflictKeepRemoteNativeAttemptsAsync(lost.CommandId)).Single().Result);
        Assert.HasCount(1, await current.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("pending", (await current.ReadUserCommandAsync(lost.CommandId))!.State);
    }

    [TestMethod]
    [DataRow("original-identity")]
    [DataRow("foreign-child")]
    [DataRow("unowned-dacl")]
    [DataRow("restored-before-attempt")]
    [DataRow("restoration-complete")]
    [DataRow("stale-role")]
    public async Task KeepRemoteRoleHandoffRefusesUnknownStateAndCompletedAccess(string scenario)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var native = await PrepareKeepRemoteNativeAsync(catalog);
        await catalog.RecordConflictKeepRemoteNativeStartAsync(native.Start);
        await catalog.RecordConflictKeepRemoteNativeResultAsync(native.Result);
        var role = await NewKeepRemoteRoleStartAsync(catalog, native, "S-1-5-5-123-461");
        var first = (await catalog.ReadConflictKeepRemoteFileActionAsync(role.CommandId))!;
        if (scenario == "restoration-complete")
        {
            var at = native.Result.RecordedAt.AddMilliseconds(100);
            var restore = new MirrorPulseConflictKeepRemoteRestoreStart(1, role.CommandId, role.FirstActionId, Guid.NewGuid(),
                native.Start.AttemptId, native.Start.SelectedPlaceholderIdentity, true, native.Result.Child with { ObservedAt = at },
                native.Result.Parent with { ObservedAt = at }, at);
            await catalog.RecordConflictKeepRemoteRestoreStartAsync(restore);
            await catalog.RecordConflictKeepRemoteRestoreResultAsync(RestoreResult(restore, first.RestoreDacl));
        }
        var invalid = scenario switch
        {
            "original-identity" => role with { PlaceholderIdentity = native.Start.ObservedPlaceholderIdentity },
            "foreign-child" => role with { Child = role.Child with { LocalObject = role.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "unowned-dacl" => role with { Child = role.Child with { Dacl = role.NextAction.RoleOnlyDacl } },
            "restored-before-attempt" => role with { Child = role.Child with { Dacl = first.RestoreDacl } },
            "stale-role" => role with { NextAction = role.NextAction with { RoleSid = first.RoleSid } },
            _ => role,
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRoleStartAsync(invalid));
        Assert.HasCount(0, await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(role.CommandId));
        Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("owner-row")]
    [DataRow("downgraded")]
    public async Task KeepRemoteRoleHistoryRejectsLostOwnershipOrAnInventedOldSchema(string scenario)
    {
        using var fixture = new Fixture();
        Guid command;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var native = await PrepareKeepRemoteNativeAsync(catalog);
            await catalog.RecordConflictKeepRemoteNativeStartAsync(native.Start);
            var role = await NewKeepRemoteRoleStartAsync(catalog, native, "S-1-5-5-123-461");
            command = role.CommandId;
            await catalog.RecordConflictKeepRemoteRoleStartAsync(role);
        }
        if (scenario == "downgraded")
        {
            await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=100;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var refused = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
            return;
        }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM conflict_keep_remote_role_owners;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadConflictKeepRemoteRoleAttemptsAsync(command));
    }

    private static async Task<MirrorPulseConflictKeepRemoteRoleStart> NewKeepRemoteRoleStartAsync(MirrorPulseProductCatalog catalog,
        NativeSetup native, string roleSid, MirrorPulseConflictKeepRemoteRoleStart? previous = null)
    {
        var first = (await catalog.ReadConflictKeepRemoteFileActionAsync(native.Start.CommandId))!;
        var at = (previous?.NextAction.PreparedAt ?? native.Result.RecordedAt).AddSeconds(2);
        var next = first with
        {
            ActionId = Guid.NewGuid(),
            RoleSid = roleSid,
            PreparedAt = at,
            RoleOnlyDacl = Dacl($"D:P(A;;FA;;;{roleSid})"),
            RestoreDacl = Dacl($"D:P(A;;FR;;;{first.OwnerSid})(A;;FA;;;{roleSid})")
        };
        return new(1, first.CommandId, first.ActionId, previous?.NextAction.ActionId, next, native.Start.SelectedPlaceholderIdentity,
            native.Result.Child with { Dacl = previous?.NextAction.RoleOnlyDacl ?? first.RoleOnlyDacl, ObservedAt = at },
            native.Result.Parent with { ObservedAt = at }, 1);
    }

    private static MirrorPulseConflictKeepRemoteRoleResult KeepRemoteRoleResult(MirrorPulseConflictKeepRemoteRoleStart start)
    {
        var at = start.NextAction.PreparedAt.AddSeconds(1);
        return new(1, start.CommandId, start.FirstActionId, start.NextAction.ActionId, start.PlaceholderIdentity,
            start.Child with { Dacl = start.NextAction.RoleOnlyDacl, ObservedAt = at }, start.Parent with { ObservedAt = at }, 1, at);
    }
}
