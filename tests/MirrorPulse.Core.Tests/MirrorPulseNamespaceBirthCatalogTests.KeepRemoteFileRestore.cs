using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteRestorationRetainsFailureLostReturnAndVerifiedAccessAcrossCatalogOwners()
    {
        using var fixture = new Fixture(); RestoreSetup setup; MirrorPulseConflictKeepRemoteRestoreResult restored;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteRestoreAsync(catalog);
            await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
            await catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result with
            {
                Receipt = setup.Result.Receipt with
                {
                    Outcome = "Busy",
                    CallbackStarted = false,
                    CallbackCompleted = false,
                    AccessDescriptorApplied = false,
                    AccessDescriptorReadBack = false
                },
                Child = null,
                Parent = null,
                LinkCount = null,
                ObservedPlaceholderIdentity = null,
                ObservedInSync = null,
                ProductVerified = false,
            });
            var lost = NextRestoreStart(setup.Start, setup.Result.RecordedAt.AddSeconds(1));
            await catalog.RecordConflictKeepRemoteRestoreStartAsync(lost);
            // The lost scope may have restored access and admitted a later ordinary edit.
            // Its next access-only attempt cannot rewrite or reconfirm the historical bytes.
            var recovered = NextRestoreStart(lost, lost.StartedAt.AddSeconds(1)) with
            { Child = lost.Child with { Dacl = setup.Result.Child!.Dacl, ObservedAt = lost.StartedAt.AddSeconds(1) }, ObservedInSync = false };
            await catalog.RecordConflictKeepRemoteRestoreStartAsync(recovered);
            restored = RestoreResult(recovered, setup.Result.Child!.Dacl) with { ObservedInSync = false };
            await catalog.RecordConflictKeepRemoteRestoreResultAsync(restored);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = await reopened.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId);
        Assert.HasCount(3, actual); Assert.IsFalse(actual[0].Result!.ProductVerified); Assert.IsNull(actual[1].Result);
        Assert.AreEqual(restored, actual[2].Result);
        Assert.IsFalse(actual[2].Result!.ObservedInSync!.Value);
        Assert.IsTrue((await reopened.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId)).Single().Result!.Receipt.ObservedInSync);
        Assert.IsNotNull((await reopened.ReadConflictKeepRemoteFileContentAsync(setup.Start.CommandId))!.Completion);
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(setup.Start.CommandId))!.State);
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("lost")]
    [DataRow("failed")]
    public async Task KeepRemoteRestoreRequiresTheOriginalSuccessfulNativeConfirmation(string native)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog, native);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start));
        Assert.HasCount(0, await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("native")]
    [DataRow("file")]
    [DataRow("parent")]
    [DataRow("unowned-restore")]
    [DataRow("identity")]
    [DataRow("sync")]
    [DataRow("time")]
    [DataRow("attempt")]
    [DataRow("missing-child")]
    [DataRow("missing-parent")]
    public async Task KeepRemoteRestoreStartRequiresOriginalNativeFileParentAndFirstSeal(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog);
        var value = changed switch
        {
            "action" => setup.Start with { ActionId = Guid.NewGuid() },
            "native" => setup.Start with { NativeAttemptId = Guid.NewGuid() },
            "file" => setup.Start with { Child = setup.Start.Child with { LocalObject = setup.Start.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent" => setup.Start with { Parent = setup.Start.Parent with { LocalObject = setup.Start.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "unowned-restore" => setup.Start with { Child = setup.Start.Child with { Dacl = setup.Result.Child!.Dacl } },
            "identity" => setup.Start with { ObservedPlaceholderIdentity = "AQID" },
            "sync" => setup.Start with { ObservedInSync = false },
            "attempt" => setup.Start with { AttemptId = setup.Start.NativeAttemptId },
            "missing-child" => setup.Start with { Child = null! },
            "missing-parent" => setup.Start with { Parent = null! },
            _ => setup.Start with { StartedAt = DateTimeOffset.UnixEpoch },
        };
        if (changed is "missing-child" or "missing-parent")
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(value));
        else if (changed == "attempt")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(value));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(value));
        Assert.HasCount(0, await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId));
    }

    [TestMethod]
    [DataRow("native")]
    [DataRow("attempt")]
    [DataRow("expected-object")]
    [DataRow("outcome")]
    [DataRow("stage")]
    [DataRow("callback")]
    [DataRow("apply")]
    [DataRow("readback")]
    [DataRow("drained")]
    [DataRow("converted")]
    [DataRow("projection")]
    [DataRow("identity")]
    [DataRow("links")]
    [DataRow("child")]
    [DataRow("parent")]
    [DataRow("missing-child")]
    [DataRow("missing-parent")]
    [DataRow("observation-time")]
    [DataRow("receipt-time")]
    [DataRow("unknown-outcome")]
    [DataRow("unknown-stage")]
    [DataRow("missing-sync")]
    [DataRow("error")]
    public async Task KeepRemoteVerifiedRestoreRequiresActualDrainedScopeAndIndependentObservations(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog); await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        var value = changed switch
        {
            "native" => setup.Result with { NativeAttemptId = Guid.NewGuid() },
            "attempt" => setup.Result with { AttemptId = Guid.NewGuid() },
            "expected-object" => setup.Result with { Receipt = setup.Result.Receipt with { ExpectedObject = setup.Result.Receipt.ExpectedObject with { LocalFileId = Guid.NewGuid() } } },
            "outcome" => setup.Result with { Receipt = setup.Result.Receipt with { Outcome = "CallbackFailed" } },
            "stage" => setup.Result with { Receipt = setup.Result.Receipt with { Stage = "AccessDescriptor" } },
            "callback" => setup.Result with { Receipt = setup.Result.Receipt with { CallbackCompleted = false } },
            "apply" => setup.Result with { Receipt = setup.Result.Receipt with { AccessDescriptorApplied = false } },
            "readback" => setup.Result with { Receipt = setup.Result.Receipt with { AccessDescriptorReadBack = false } },
            "drained" => setup.Result with { Receipt = setup.Result.Receipt with { Drained = false } },
            "converted" => setup.Result with { Receipt = setup.Result.Receipt with { NativeConverted = true } },
            "projection" => setup.Result with { Receipt = setup.Result.Receipt with { DurableProjectionCommitted = true } },
            "identity" => setup.Result with { ObservedPlaceholderIdentity = "AQID" },
            "links" => setup.Result with { LinkCount = 2 },
            "child" => setup.Result with { Child = setup.Result.Child! with { Dacl = setup.Start.Child.Dacl } },
            "parent" => setup.Result with { Parent = setup.Result.Parent! with { LocalObject = setup.Start.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "missing-child" => setup.Result with { Child = null },
            "missing-parent" => setup.Result with { Parent = null },
            "observation-time" => setup.Result with { Child = setup.Result.Child! with { ObservedAt = setup.Start.StartedAt.AddTicks(-1) } },
            "receipt-time" => setup.Result with { Receipt = setup.Result.Receipt with { ObservedAt = setup.Start.StartedAt.AddTicks(-1) } },
            "unknown-outcome" => setup.Result with { Receipt = setup.Result.Receipt with { Outcome = "invented" } },
            "unknown-stage" => setup.Result with { Receipt = setup.Result.Receipt with { Stage = "invented" } },
            "missing-sync" => setup.Result with { ObservedInSync = null },
            _ => setup.Result with { Receipt = setup.Result.Receipt with { ErrorHResult = unchecked((int)0x80070005) } },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(value));
        Assert.IsNull((await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId)).Single().Result);
    }

    [TestMethod]
    public async Task KeepRemoteRestoreCannotRewriteOriginalAttemptsOrRecordLateResultsOrRestartVerifiedWork()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result));
        await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        Assert.AreEqual(setup.Start, await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start with { StartedAt = setup.Start.StartedAt.AddTicks(1) }));
        var next = NextRestoreStart(setup.Start, setup.Result.RecordedAt.AddSeconds(1));
        await catalog.RecordConflictKeepRemoteRestoreStartAsync(next);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result));
        var result = RestoreResult(next, setup.Result.Child!.Dacl);
        await catalog.RecordConflictKeepRemoteRestoreResultAsync(result);
        Assert.AreEqual(result, await catalog.RecordConflictKeepRemoteRestoreResultAsync(result));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreResultAsync(result with { RecordedAt = result.RecordedAt.AddTicks(1) }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(NextRestoreStart(next, result.RecordedAt.AddSeconds(1))));
    }

    [TestMethod]
    public async Task KeepRemoteRestoreDoesNotOverlapAnUndrainedRecordedScope()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog);
        await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        await catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result with
        { Receipt = setup.Result.Receipt with { Outcome = "Failed", Drained = false }, ProductVerified = false });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRestoreStartAsync(
            NextRestoreStart(setup.Start, setup.Result.RecordedAt.AddSeconds(1))));
    }

    [TestMethod]
    [DataRow(1, "facts")]
    [DataRow(1, "owners")]
    [DataRow(2, "facts")]
    [DataRow(2, "owners")]
    public async Task KeepRemoteRestoreInsertInterruptionRollsBackTheFactAndOriginalOwners(int kind, string table)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog);
        if (kind == 2) await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        string before = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId));
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_restore BEFORE INSERT ON conflict_keep_remote_restore_{table} BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        Task RecordAsync() => kind == 1 ? catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start) : catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result);
        await Assert.ThrowsExactlyAsync<SqliteException>(RecordAsync);
        Assert.AreEqual(before, JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId)));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_restore;"); await RecordAsync();
    }

    [TestMethod]
    [DataRow("facts")]
    [DataRow("owners")]
    [DataRow("fingerprint")]
    [DataRow("order")]
    [DataRow("native-owner")]
    [DataRow("action-owner")]
    [DataRow("native-result")]
    public async Task KeepRemoteRestoreReadRejectsMissingChangedAndForeignAuthority(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteRestoreAsync(catalog); await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        await catalog.RecordConflictKeepRemoteRestoreResultAsync(setup.Result);
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "facts" => "DELETE FROM conflict_keep_remote_restore_facts WHERE ordinal=1;",
            "owners" => "DELETE FROM conflict_keep_remote_restore_owners WHERE ordinal=2;",
            "fingerprint" => "UPDATE conflict_keep_remote_restore_facts SET fingerprint=zeroblob(32) WHERE ordinal=2;",
            "order" => "UPDATE conflict_keep_remote_restore_facts SET ordinal=3 WHERE ordinal=2; UPDATE conflict_keep_remote_restore_owners SET ordinal=3 WHERE ordinal=2;",
            "native-owner" => "UPDATE conflict_keep_remote_restore_owners SET native_attempt_id='00000000-0000-0000-0000-000000000001' WHERE ordinal=2;",
            // This private fixture deliberately damages stored ownership to exercise readback,
            // independently of the product connection's enabled foreign-key constraints.
            "action-owner" => "PRAGMA foreign_keys=OFF; UPDATE conflict_keep_remote_restore_owners SET action_id='00000000-0000-0000-0000-000000000001' WHERE ordinal=2;",
            _ => "DELETE FROM conflict_keep_remote_native_owners WHERE kind=2; DELETE FROM conflict_keep_remote_native_facts WHERE kind=2;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId));
    }

    [TestMethod]
    public async Task Schema99PreservesNativeConfirmationWithoutInventingRestoration()
    {
        using var fixture = new Fixture(); RestoreSetup setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) setup = await PrepareKeepRemoteRestoreAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_restore_owners; DROP TABLE conflict_keep_remote_restore_facts; PRAGMA user_version=99;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.HasCount(0, await reopened.ReadConflictKeepRemoteRestoreAttemptsAsync(setup.Start.CommandId));
        Assert.IsTrue((await reopened.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId)).Single().Result!.Receipt.MayAcknowledge);
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("facts")]
    [DataRow("owners")]
    [DataRow("definition")]
    [DataRow("downgraded")]
    public async Task Schema100RefusesMissingChangedOrDowngradedRestorationHistory(string changed)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteRestoreAsync(catalog); await catalog.RecordConflictKeepRemoteRestoreStartAsync(setup.Start);
        }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "facts" => "DROP TABLE conflict_keep_remote_restore_facts;",
            "owners" => "DROP TABLE conflict_keep_remote_restore_owners;",
            "definition" => "ALTER TABLE conflict_keep_remote_restore_facts ADD COLUMN invented INTEGER;",
            _ => "PRAGMA user_version=99;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var refused = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    private sealed record RestoreSetup(MirrorPulseConflictKeepRemoteRestoreStart Start, MirrorPulseConflictKeepRemoteRestoreResult Result);

    private static async Task<RestoreSetup> PrepareKeepRemoteRestoreAsync(MirrorPulseProductCatalog catalog, string nativeState = "complete")
    {
        var native = await PrepareKeepRemoteNativeAsync(catalog);
        if (nativeState != "none") await catalog.RecordConflictKeepRemoteNativeStartAsync(native.Start);
        if (nativeState is "complete" or "failed")
            await catalog.RecordConflictKeepRemoteNativeResultAsync(nativeState == "complete" ? native.Result : native.Result with
            { Receipt = native.Result.Receipt with { Outcome = MirrorPulseContentConfirmationOutcome.ContentMismatch, NativeConfirmationVerified = false } });
        var content = await catalog.ReadConflictKeepRemoteFileContentAsync(native.Start.CommandId);
        var at = native.Result.RecordedAt.AddSeconds(1);
        var start = new MirrorPulseConflictKeepRemoteRestoreStart(1, native.Start.CommandId, native.Start.ActionId, Guid.NewGuid(),
            native.Start.AttemptId, native.Start.SelectedPlaceholderIdentity, true,
            native.Result.Child with { ObservedAt = at }, native.Result.Parent with { ObservedAt = at }, at);
        return new(start, RestoreResult(start, content!.Action.RestoreDacl));
    }

    private static MirrorPulseConflictKeepRemoteRestoreStart NextRestoreStart(MirrorPulseConflictKeepRemoteRestoreStart start, DateTimeOffset at) =>
        start with { AttemptId = Guid.NewGuid(), StartedAt = at, Child = start.Child with { ObservedAt = at }, Parent = start.Parent with { ObservedAt = at } };

    private static MirrorPulseConflictKeepRemoteRestoreResult RestoreResult(MirrorPulseConflictKeepRemoteRestoreStart start, string descriptor)
    {
        var at = start.StartedAt.AddSeconds(1);
        return new(1, start.CommandId, start.ActionId, start.AttemptId, start.NativeAttemptId,
            new("Completed", "Released", start.Child.LocalObject, true, true, true, true, true, false, false, false, null, null, null, false, false, at),
            start.Child with { Dacl = descriptor, ObservedAt = at }, start.Parent with { ObservedAt = at }, 1,
            start.ObservedPlaceholderIdentity, true, true, at);
    }
}
