using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteNativeAttemptsRetainFailuresLostReturnsAndSuccessAcrossOwners()
    {
        using var fixture = new Fixture(); NativeSetup setup; MirrorPulseConflictKeepRemoteNativeResult success;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteNativeAsync(catalog);
            await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
            var failed = setup.Result with
            {
                Receipt = setup.Result.Receipt with
                { Outcome = MirrorPulseContentConfirmationOutcome.ContentMismatch, NativeConfirmationVerified = false, DurableProjectionCommitted = false }
            };
            await catalog.RecordConflictKeepRemoteNativeResultAsync(failed);
            var lost = NextNativeStart(setup.Start, setup.Result.RecordedAt.AddSeconds(1));
            await catalog.RecordConflictKeepRemoteNativeStartAsync(lost);
            var recovered = NextNativeStart(lost, lost.StartedAt.AddSeconds(1)) with { ObservedPlaceholderIdentity = lost.SelectedPlaceholderIdentity };
            await catalog.RecordConflictKeepRemoteNativeStartAsync(recovered);
            success = NativeResult(recovered, setup.Result.Receipt.BytesVerified);
            await catalog.RecordConflictKeepRemoteNativeResultAsync(success);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeStartAsync(
                NextNativeStart(recovered, success.RecordedAt.AddSeconds(1))));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = await reopened.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId);
        Assert.HasCount(3, actual); Assert.IsFalse(actual[0].Result!.Receipt.MayAcknowledge);
        Assert.IsNull(actual[1].Result); Assert.AreEqual(success, actual[2].Result);
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(setup.Start.CommandId))!.State);
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("file")]
    [DataRow("parent")]
    [DataRow("seal")]
    [DataRow("identity")]
    [DataRow("time")]
    [DataRow("attempt")]
    public async Task KeepRemoteNativeStartRequiresCompletedOriginalContentAndCurrentSeal(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteNativeAsync(catalog);
        var value = changed switch
        {
            "action" => setup.Start with { ActionId = Guid.NewGuid() },
            "file" => setup.Start with { Child = setup.Start.Child with { LocalObject = setup.Start.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent" => setup.Start with { Parent = setup.Start.Parent with { LocalObject = setup.Start.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "seal" => setup.Start with { Child = setup.Start.Child with { Dacl = "D:P(A;;FA;;;SY)" } },
            "identity" => setup.Start with { ObservedPlaceholderIdentity = setup.Start.SelectedPlaceholderIdentity },
            "attempt" => setup.Start with { AttemptId = setup.Start.CommandId },
            _ => setup.Start with { StartedAt = DateTimeOffset.UnixEpoch },
        };
        if (changed == "attempt")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordConflictKeepRemoteNativeStartAsync(value));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeStartAsync(value));
        Assert.HasCount(0, await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId));
    }

    [TestMethod]
    [DataRow("bytes")]
    [DataRow("identity")]
    [DataRow("sync")]
    [DataRow("stage")]
    [DataRow("attempt")]
    [DataRow("time")]
    [DataRow("file")]
    [DataRow("parent")]
    public async Task KeepRemoteNativeResultCannotReplaceItsActualRequestOrSelectedProof(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteNativeAsync(catalog); await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
        var value = changed switch
        {
            "bytes" => setup.Result with { Receipt = setup.Result.Receipt with { BytesVerified = setup.Result.Receipt.BytesVerified - 1 } },
            "identity" => setup.Result with { Receipt = setup.Result.Receipt with { ObservedPlaceholderIdentity = setup.Start.ObservedPlaceholderIdentity } },
            "sync" => setup.Result with { Receipt = setup.Result.Receipt with { ObservedInSync = false } },
            "stage" => setup.Result with { Receipt = setup.Result.Receipt with { Stage = MirrorPulseContentConfirmationStage.Mark } },
            "attempt" => setup.Result with { AttemptId = Guid.NewGuid() },
            "time" => setup.Result with { RecordedAt = setup.Start.StartedAt.AddTicks(-1) },
            "file" => setup.Result with { Child = setup.Result.Child with { LocalObject = setup.Result.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            _ => setup.Result with { Parent = setup.Result.Parent with { LocalObject = setup.Result.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeResultAsync(value));
        Assert.IsNull((await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId)).Single().Result);
    }

    [TestMethod]
    public async Task KeepRemoteNativeBoundariesAreImmutableAndCannotSkipTheirContentOrAttempt()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteNativeAsync(catalog);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result));
        await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
        Assert.AreEqual(setup.Start, await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start with { StartedAt = setup.Start.StartedAt.AddTicks(1) }));
        await catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result);
        Assert.AreEqual(setup.Result, await catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result with { RecordedAt = setup.Result.RecordedAt.AddTicks(1) }));
    }

    [TestMethod]
    [DataRow(1, "facts")]
    [DataRow(1, "owners")]
    [DataRow(2, "facts")]
    [DataRow(2, "owners")]
    public async Task KeepRemoteNativeInsertInterruptionRollsBackTheFactAndOriginalOwner(int kind, string table)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteNativeAsync(catalog);
        if (kind == 2) await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
        string before = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId));
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_native BEFORE INSERT ON conflict_keep_remote_native_{table} BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        Task RecordAsync() => kind == 1 ? catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start) : catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result);
        await Assert.ThrowsExactlyAsync<SqliteException>(RecordAsync);
        Assert.AreEqual(before, JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId)));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_native;"); await RecordAsync();
    }

    [TestMethod]
    [DataRow("facts")]
    [DataRow("owners")]
    [DataRow("fingerprint")]
    [DataRow("order")]
    [DataRow("owner")]
    public async Task KeepRemoteNativeReadRefusesMissingCorruptOrReorderedOriginalFacts(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteNativeAsync(catalog); await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
        await catalog.RecordConflictKeepRemoteNativeResultAsync(setup.Result);
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "facts" => "DELETE FROM conflict_keep_remote_native_facts WHERE ordinal=1;",
            "owners" => "DELETE FROM conflict_keep_remote_native_owners WHERE ordinal=2;",
            "fingerprint" => "UPDATE conflict_keep_remote_native_facts SET fingerprint=zeroblob(32) WHERE ordinal=2;",
            "order" => "UPDATE conflict_keep_remote_native_owners SET ordinal=3 WHERE ordinal=2; UPDATE conflict_keep_remote_native_facts SET ordinal=3 WHERE ordinal=2;",
            _ => "UPDATE conflict_keep_remote_native_owners SET attempt_id='00000000-0000-0000-0000-000000000001' WHERE ordinal=2;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId));
    }

    [TestMethod]
    public async Task Schema98PreservesCompleteResidentBytesWithoutInventingNativeWork()
    {
        using var fixture = new Fixture(); NativeSetup setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) setup = await PrepareKeepRemoteNativeAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_native_owners; DROP TABLE conflict_keep_remote_native_facts; PRAGMA user_version=98;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.HasCount(0, await reopened.ReadConflictKeepRemoteNativeAttemptsAsync(setup.Start.CommandId));
        Assert.IsNotNull((await reopened.ReadConflictKeepRemoteFileContentAsync(setup.Start.CommandId))!.Completion);
    }

    [TestMethod]
    [DataRow("facts")]
    [DataRow("owners")]
    [DataRow("definition")]
    [DataRow("downgraded")]
    public async Task Schema99RefusesMissingChangedOrDowngradedNativeHistory(string changed)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteNativeAsync(catalog); await catalog.RecordConflictKeepRemoteNativeStartAsync(setup.Start);
        }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "facts" => "DROP TABLE conflict_keep_remote_native_facts;",
            "owners" => "DROP TABLE conflict_keep_remote_native_owners;",
            "definition" => "ALTER TABLE conflict_keep_remote_native_facts ADD COLUMN invented INTEGER;",
            _ => "PRAGMA user_version=98;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var refused = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    private sealed record NativeSetup(MirrorPulseConflictKeepRemoteNativeStart Start, MirrorPulseConflictKeepRemoteNativeResult Result);

    private static async Task<NativeSetup> PrepareKeepRemoteNativeAsync(MirrorPulseProductCatalog catalog)
    {
        var content = await PrepareKeepRemoteContentAsync(catalog);
        await catalog.RecordConflictKeepRemoteFileWriteStartAsync(content.Write);
        await catalog.RecordConflictKeepRemoteFileContentCompletionAsync(content.Completion);
        var start = new MirrorPulseConflictKeepRemoteNativeStart(1, content.Action.CommandId, content.Action.ActionId, Guid.NewGuid(),
            "BAUG", content.Completion.OriginalPlaceholderIdentity,
            content.Completion.Child with { ObservedAt = content.Completion.VerifiedAt.AddSeconds(1) },
            content.Completion.Parent with { ObservedAt = content.Completion.VerifiedAt.AddSeconds(1) }, content.Completion.VerifiedAt.AddSeconds(2));
        return new(start, NativeResult(start, content.Completion.Copy.Length));
    }

    private static MirrorPulseConflictKeepRemoteNativeStart NextNativeStart(MirrorPulseConflictKeepRemoteNativeStart start, DateTimeOffset at) =>
        start with { AttemptId = Guid.NewGuid(), Child = start.Child with { ObservedAt = at }, Parent = start.Parent with { ObservedAt = at }, StartedAt = at };

    private static MirrorPulseConflictKeepRemoteNativeResult NativeResult(MirrorPulseConflictKeepRemoteNativeStart start, long length)
    {
        var at = start.StartedAt.AddSeconds(1);
        return new(1, start.CommandId, start.ActionId, start.AttemptId,
            new(MirrorPulseContentConfirmationOutcome.Confirmed, MirrorPulseContentConfirmationStage.Complete,
                MirrorPulseContentConfirmationStage.Mark, true, true, true, true, length, 1, 0, 0, null, null,
                true, start.SelectedPlaceholderIdentity, at), start.Child with { ObservedAt = at }, start.Parent with { ObservedAt = at }, at);
    }
}
