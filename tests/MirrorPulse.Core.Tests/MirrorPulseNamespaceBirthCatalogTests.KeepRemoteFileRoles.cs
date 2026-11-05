using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteRoleHandoffRejectsStalePhaseReturnsAndRetainsTheOriginalWrite()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog);
        var command = setup.Action.CommandId;
        await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        var template = new MirrorPulseConflictKeepRemoteNativeStart(1, command, setup.Action.ActionId, Guid.NewGuid(),
            "BAUG", setup.Write.OriginalPlaceholderIdentity, setup.Completion.Child, setup.Completion.Parent,
            setup.Completion.VerifiedAt.AddSeconds(1));
        var firstHandoff = (await NewKeepRemoteRoleStartAsync(catalog, new(template, NativeResult(template, setup.Completion.Copy.Length)),
            "S-1-5-5-123-461")) with
        { PlaceholderIdentity = template.ObservedPlaceholderIdentity };
        await catalog.RecordConflictKeepRemoteRoleStartAsync(firstHandoff);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion));
        var firstResult = await catalog.RecordConflictKeepRemoteRoleResultAsync(KeepRemoteRoleResult(firstHandoff));
        var at = firstResult.RecordedAt.AddSeconds(1);
        var completion = setup.Completion with
        {
            RoleRecoveryId = firstResult.RecoveryId,
            Child = firstResult.Child with { ObservedAt = at },
            Parent = firstResult.Parent with { ObservedAt = at },
            VerifiedAt = at
        };
        await catalog.RecordConflictKeepRemoteFileContentCompletionAsync(completion);
        at = at.AddSeconds(1);
        var started = template with
        {
            RoleRecoveryId = firstResult.RecoveryId,
            Child = completion.Child with { ObservedAt = at },
            Parent = completion.Parent with { ObservedAt = at },
            StartedAt = at
        };
        await catalog.RecordConflictKeepRemoteNativeStartAsync(started);
        var stale = NativeResult(started, completion.Copy.Length);
        at = stale.RecordedAt.AddSeconds(1);
        var nextHandoff = (await NewKeepRemoteRoleStartAsync(catalog, new(started, stale), "S-1-5-5-123-462", firstHandoff)) with
        {
            PlaceholderIdentity = template.ObservedPlaceholderIdentity,
            Child = started.Child with { ObservedAt = at },
            Parent = started.Parent with { ObservedAt = at }
        };
        nextHandoff = nextHandoff with { NextAction = nextHandoff.NextAction with { PreparedAt = at } };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRoleStartAsync(nextHandoff with
        {
            NextAction = nextHandoff.NextAction with { PreparedAt = started.StartedAt },
            Child = started.Child,
            Parent = started.Parent
        }));
        await catalog.RecordConflictKeepRemoteRoleStartAsync(nextHandoff);
        var current = await catalog.RecordConflictKeepRemoteRoleResultAsync(KeepRemoteRoleResult(nextHandoff));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeResultAsync(stale));
        at = current.RecordedAt.AddSeconds(1);
        var resumed = NextNativeStart(started, at) with { RoleRecoveryId = current.RecoveryId, Child = current.Child with { ObservedAt = at } };
        await catalog.RecordConflictKeepRemoteNativeStartAsync(resumed);
        var confirmed = await catalog.RecordConflictKeepRemoteNativeResultAsync(NativeResult(resumed, completion.Copy.Length));
        await catalog.DisposeAsync();
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Write, (await reopened.ReadConflictKeepRemoteFileContentAsync(command))!.WriteStart);
        Assert.AreEqual(completion, await reopened.RecordConflictKeepRemoteFileContentCompletionAsync(completion));
        CollectionAssert.AreEqual(new[] { new MirrorPulseConflictKeepRemoteNativeAttempt(started, null), new(resumed, confirmed) },
            (await reopened.ReadConflictKeepRemoteNativeAttemptsAsync(command)).ToArray());
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(command))!.State);
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

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

    [TestMethod]
    public async Task KeepRemoteRoleHandoffBindsRestorationAndRejectsTheOldOwnersLostReturn()
    {
        using var fixture = new Fixture();
        MirrorPulseConflictKeepRemoteRestoreStart original, recovered;
        MirrorPulseConflictKeepRemoteRestoreResult verified;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var restore = await PrepareKeepRemoteRestoreAsync(catalog);
            original = await catalog.RecordConflictKeepRemoteRestoreStartAsync(restore.Start);
            var native = (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(original.CommandId)).Single();
            var role = await NewKeepRemoteRoleStartAsync(catalog, new(native.Start, native.Result!), "S-1-5-5-123-461");
            await catalog.RecordConflictKeepRemoteRoleStartAsync(role);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(restore.Result));
            var handed = await catalog.RecordConflictKeepRemoteRoleResultAsync(KeepRemoteRoleResult(role));
            var at = handed.RecordedAt.AddSeconds(1);
            recovered = NextRestoreStart(original, at) with
            {
                RoleRecoveryId = handed.RecoveryId,
                Child = handed.Child with { ObservedAt = at },
                Parent = handed.Parent with { ObservedAt = at },
                ObservedInSync = false,
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(recovered with { RoleRecoveryId = Guid.NewGuid() }));
            await catalog.RecordConflictKeepRemoteRestoreStartAsync(recovered);
            var result = RestoreResult(recovered, role.NextAction.RestoreDacl);
            var first = (await catalog.ReadConflictKeepRemoteFileActionAsync(original.CommandId))!;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(
                result with { Child = result.Child! with { Dacl = first.RestoreDacl } }));
            verified = await catalog.RecordConflictKeepRemoteRestoreResultAsync(result);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { new MirrorPulseConflictKeepRemoteRestoreAttempt(original, null), new(recovered, verified) },
            (await reopened.ReadConflictKeepRemoteRestoreAttemptsAsync(original.CommandId)).ToArray());
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(original.CommandId))!.State);
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }
}
