using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalAcknowledgedBaselineTests
{
    [TestMethod]
    public async Task PreparationReadsOnlyTheRetainedBaselineWhileOrdinaryDispatchStillRejectsAChangedRemote()
    {
        await using var fixture = await Fixture.OpenAsync();
        Assert.AreEqual("accepted", await MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(
            fixture.Store, fixture.Command, "Docs/item.txt"));
        var remote = new ChangedRemote();
        var conflict = await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(() =>
            MirrorPulseJournalUploadRevisionGuard.ResolveAsync(fixture.Store, remote, fixture.Command, "Docs/item.txt").AsTask());
        Assert.AreEqual("accepted", conflict.ExpectedRevision); Assert.AreEqual("changed", conflict.ActualRevision);
        Assert.AreEqual(1, remote.Calls);
        await using var reading = await fixture.Store.BeginTransactionAsync();
        var original = (await reading.Operations.GetAsync(fixture.Command.OperationId))!;
        Assert.IsNotNull(original); Assert.AreEqual(0, original.AttemptCount);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, original.Payload.ToArray());
        await reading.RollbackAsync();
    }

    [TestMethod]
    public async Task EarlierIdentityProjectionUsesTheOriginalOfficialOperationReferenceWithoutAdoptingAPath()
    {
        await using var fixture = await Fixture.OpenAsync();
        var priorObservation = fixture.Command with { ItemId = Guid.NewGuid() };
        Assert.AreEqual("accepted", await MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(
            fixture.Store, priorObservation, "Docs/unrelated.txt"));
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(fixture.Store,
                priorObservation with { Sequence = fixture.Command.Sequence + 1 }, "Docs/item.txt").AsTask());
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(fixture.Store,
                priorObservation with { Kind = MirrorPulseWorkerChangeKind.Move }, "Docs/item.txt").AsTask());
    }

    [TestMethod]
    public async Task TombstoneRequiresExplicitDeletionPreparationAndKeepsItsHistoricalRevision()
    {
        await using var fixture = await Fixture.OpenAsync(tombstone: true);
        await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(() =>
            MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(fixture.Store, fixture.Command, "Docs/item.txt").AsTask());
        Assert.AreEqual("accepted", await MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(
            fixture.Store, fixture.Command, "Docs/item.txt", allowTombstone: true));
    }

    private sealed class ChangedRemote : IMirrorPulseWorkerStatTransport
    {
        public int Calls { get; private set; }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult<string?>("changed"); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-journal-baseline", Guid.NewGuid().ToString("N"));
        public ICloudStateStore Store { get; private set; } = null!;
        public MirrorPulseWorkerChangeCommand Command { get; private set; } = null!;

        public static async Task<Fixture> OpenAsync(bool tombstone = false)
        {
            var fixture = new Fixture();
            var paths = new MirrorPulseStoragePaths(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
            Directory.CreateDirectory(paths.SyncRootPath);
            fixture.Store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new CloudStateStoreContext(paths.SyncRootPath));
            Guid item = Guid.NewGuid(), operation = Guid.NewGuid();
            await using var transaction = await fixture.Store.BeginTransactionAsync();
            await transaction.Items.UpsertAsync(new(item, "remote-item", "Docs/item.txt", CloudItemKind.File,
                "accepted", null, tombstone, DateTimeOffset.UtcNow));
            await transaction.Operations.EnqueueAsync(new(operation, CloudStateOperationKind.ContentUpdate, item, new byte[] { 1, 2 }, DateTimeOffset.UtcNow));
            var original = (await transaction.Operations.GetAsync(operation))!;
            fixture.Command = new(operation, original.Sequence, InstanceId.New(), "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                "item.txt", null, null, false, item, DateTimeOffset.UtcNow);
            await transaction.CommitAsync(); return fixture;
        }

        public async ValueTask DisposeAsync()
        { await Store.DisposeAsync(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
