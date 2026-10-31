using CfSharp;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class MirrorPulseConflictKeepRemoteJournalTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task EveryEffectRetainsExactOriginalJournalRowsAndDecodedPlanningFactsWithoutMutationOrAcknowledgement(MirrorPulseCoalescedEffect effect)
    {
        await using var fixture = await Fixture.OpenAsync(effect);
        string before = await fixture.RowsAsync(); int calls = fixture.Source.Calls;
        var guard = fixture.Guard(); var snapshot = await guard.CaptureAsync(fixture.Copies);
        await guard.RevalidateAsync(snapshot, fixture.Copies);
        Assert.AreEqual(before, await fixture.RowsAsync()); Assert.AreEqual(calls, fixture.Source.Calls);
        CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), snapshot.Originals.Select(row => row.OperationId).ToArray());
        Assert.IsTrue(snapshot.Originals.All(row => row.AttemptCount == 4 && row.RetryAfter is not null));
        Assert.AreEqual(2, fixture.PublicReads);
        Assert.HasCount(fixture.Plan.Members.Count, await fixture.Catalog.ReadUploadConflictsAsync());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(snapshot.CommandId))!.State);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("kind")]
    [DataRow("created")]
    [DataRow("attempt")]
    [DataRow("retry")]
    [DataRow("item")]
    [DataRow("missing")]
    [DataRow("sequence")]
    [DataRow("successor")]
    public async Task AnEarlierKeepRemoteSnapshotCannotAuthorizeChangedOriginalsOrANewerTargetOperation(string change)
    {
        await using var fixture = await Fixture.OpenAsync(); var guard = fixture.Guard();
        var snapshot = await guard.CaptureAsync(fixture.Copies);
        await fixture.ChangeRowAsync(change); string changed = await fixture.RowsAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => guard.RevalidateAsync(snapshot, fixture.Copies));
        Assert.AreEqual(changed, await fixture.RowsAsync());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(snapshot.CommandId))!.State);
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("successor")]
    [DataRow("missing")]
    public async Task JournalChangesDuringPublicDecodingCannotBecomeANativeApplicationSnapshot(string change)
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.DuringRead = () => fixture.ChangeRowAsync(change);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().CaptureAsync(fixture.Copies));
        Assert.AreEqual(1, fixture.PublicReads);
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("kind")]
    [DataRow("time")]
    [DataRow("sequence")]
    [DataRow("item")]
    [DataRow("directory")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    public async Task TypedOriginalsMustStillMatchTheExactRoutedPlanningFingerprints(string change)
    {
        await using var fixture = await Fixture.OpenAsync(); string before = await fixture.RowsAsync();
        fixture.ChangeObservations = rows => change switch
        {
            "missing" => rows.Skip(1).ToArray(),
            "duplicate" => [rows[0], rows[0]],
            _ => rows.Select((row, index) => index != 0 ? row : change switch
            {
                "path" => row with { RelativePath = "Docs/other.bin" },
                "kind" => row with { Kind = CloudLocalChangeKind.Delete },
                "time" => row with { ObservedAt = row.ObservedAt.AddTicks(1) },
                "sequence" => row with { Sequence = row.Sequence + 10 },
                "item" => row with { ItemId = Guid.NewGuid() },
                "directory" => row with { IsDirectory = true },
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            }).ToArray()
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().CaptureAsync(fixture.Copies));
        Assert.AreEqual(before, await fixture.RowsAsync());
    }

    [TestMethod]
    public async Task UnrelatedJournalBacklogDoesNotInvalidateOrExtendTheOriginalTargetDecision()
    {
        await using var fixture = await Fixture.OpenAsync(); var guard = fixture.Guard();
        var snapshot = await guard.CaptureAsync(fixture.Copies);
        await using (var transaction = await fixture.Store.BeginTransactionAsync())
        {
            Guid unrelated = Guid.NewGuid();
            await transaction.Items.UpsertAsync(new(unrelated, "unrelated-fixture", "Docs/unrelated.bin",
                CloudItemKind.File, "old", null, false, DateTimeOffset.UtcNow));
            for (int index = 0; index < 256; index++)
                await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, unrelated, new byte[] { 1 }, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        await guard.RevalidateAsync(snapshot, fixture.Copies);
        Assert.HasCount(2, snapshot.Originals);
    }

    [TestMethod]
    public async Task DisposedPrivateCopiesAndCancellationCannotYieldAnApplicationSnapshot()
    {
        await using var fixture = await Fixture.OpenAsync(); var guard = fixture.Guard(); string before = await fixture.RowsAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => guard.CaptureAsync(fixture.Copies, cancelled.Token));
        Assert.AreEqual(0, fixture.PublicReads);
        await fixture.Copies.DisposeAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => guard.CaptureAsync(fixture.Copies));
        Assert.AreEqual(before, await fixture.RowsAsync());
    }
}
