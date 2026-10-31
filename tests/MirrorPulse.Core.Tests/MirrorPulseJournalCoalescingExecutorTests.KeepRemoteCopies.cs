using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task KeepRemoteHoldsBothSelectedCopiesAcrossAllEffectsWithoutApplicationOrAcknowledgement(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths); var worker = new KeepRemoteCopyWorker(prepared.Intent.RemoteSource);
        var preserver = new MirrorPulseConflictKeepRemotePreserver(catalog, copies, new(copies, worker, worker, worker));
        int localReads = 0;
        await using (var retained = await preserver.RetainAsync(prepared.Intent.CommandId, (intent, stop) =>
        {
            stop.ThrowIfCancellationRequested(); Assert.AreEqual(prepared.Intent.CommandId, intent.CommandId); localReads++;
            return ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes, writable: false));
        }))
        {
            Assert.AreEqual(prepared.Intent.LocalContent is null ? 0 : 1, localReads);
            CollectionAssert.AreEqual(SelectedRemoteBytes, await ReadKeepRemoteCopyAsync(retained.Remote.Data));
            Assert.ThrowsExactly<IOException>(() => { using var forbidden = File.Open(retained.Remote.Path, FileMode.Open, FileAccess.Write, FileShare.Read); });
            if (retained.Local is { } local)
            {
                CollectionAssert.AreEqual(FinalBytes, await ReadKeepRemoteCopyAsync(local.Data));
                Assert.ThrowsExactly<IOException>(() => { using var forbidden = File.Open(local.Path, FileMode.Open, FileAccess.Write, FileShare.Read); });
            }
            Assert.AreEqual(retained.Remote.Proof, retained.Preparation.RemoteCopy);
            Assert.AreEqual(retained.Local?.Proof, retained.Preparation.LocalPreservation!.Copy);
            CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), retained.Preparation.Intent.OriginalOperationIds.ToArray());
        }
        int calls = worker.Calls; worker.Offline = true;
        await using var replay = await preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => throw new AssertFailedException("Replay must use committed local bytes, not a later live edit."));
        CollectionAssert.AreEqual(SelectedRemoteBytes, await ReadKeepRemoteCopyAsync(replay.Remote.Data));
        Assert.AreEqual(calls, worker.Calls);
        Assert.HasCount(prepared.Intent.OriginalOperationIds.Count, await catalog.ReadUploadConflictsAsync());
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(prepared.Intent.CommandId))!.State);
        Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync()); Assert.IsEmpty(rejected.Writes);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("local", "DataCommitted")]
    [DataRow("local", "ManifestCommitted")]
    [DataRow("remote", "DataCommitted")]
    [DataRow("remote", "ManifestCommitted")]
    public async Task KeepRemoteRecoversCommittedCopyBeforeItsCatalogReceiptWithoutReplacingEitherSource(string side, string boundary)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var worker = new KeepRemoteCopyWorker(prepared.Intent.RemoteSource);
        var interrupted = new MirrorPulseConflictCopyStore(fixture.Paths, point =>
        {
            if (point.ToString() == boundary && (side == "remote") == (worker.Calls > 0)) throw new IOException("fixture durable copy interruption");
        });
        var first = new MirrorPulseConflictKeepRemotePreserver(catalog, interrupted, new(interrupted, worker, worker, worker));
        await Assert.ThrowsExactlyAsync<IOException>(() => first.RetainAsync(prepared.Intent.CommandId,
            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes, writable: false))));
        int before = worker.Calls; worker.Offline = side == "remote";
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths);
        var reopened = new MirrorPulseConflictKeepRemotePreserver(catalog, copies, new(copies, worker, worker, worker));
        await using var retained = await reopened.RetainAsync(prepared.Intent.CommandId,
            (_, _) => throw new AssertFailedException("A committed original local copy must recover without opening the changed live file."));
        CollectionAssert.AreEqual(FinalBytes, await ReadKeepRemoteCopyAsync(retained.Local!.Data));
        CollectionAssert.AreEqual(SelectedRemoteBytes, await ReadKeepRemoteCopyAsync(retained.Remote.Data));
        if (side == "remote") Assert.AreEqual(before, worker.Calls);
        Assert.HasCount(prepared.Intent.ConflictIds.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(rejected.Writes);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(prepared.Intent.CommandId))!.State);
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("remote")]
    public async Task ARecordedKeepRemoteCopyCannotBeRecreatedFromLiveSourcesAfterBothPrivateFilesDisappear(string side)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths); var worker = new KeepRemoteCopyWorker(prepared.Intent.RemoteSource);
        var preserver = new MirrorPulseConflictKeepRemotePreserver(catalog, copies, new(copies, worker, worker, worker));
        string path;
        await using (var retained = await preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes, writable: false))))
            path = side == "local" ? retained.Local!.Path : retained.Remote.Path;
        File.Delete(path); File.Delete(path + ".manifest.json");
        int calls = worker.Calls; worker.Offline = true;
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => throw new AssertFailedException("A recorded copy must not be recreated from live local content.")));
        Assert.AreEqual(calls, worker.Calls); Assert.IsFalse(File.Exists(path));
        Assert.HasCount(prepared.Intent.ConflictIds.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(rejected.Writes);
    }

    [TestMethod]
    public async Task ChangedLocalBytesCannotAuthorizeRemoteStaging()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths); var worker = new KeepRemoteCopyWorker(prepared.Intent.RemoteSource);
        var preserver = new MirrorPulseConflictKeepRemotePreserver(catalog, copies, new(copies, worker, worker, worker));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemoteBytes, writable: false))));
        Assert.AreEqual(0, worker.Calls);
        var current = (await catalog.ReadConflictKeepRemoteAsync(prepared.Intent.CommandId))!;
        Assert.IsNull(current.LocalPreservation); Assert.IsNull(current.RemoteCopy);
        Assert.HasCount(prepared.Intent.ConflictIds.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(rejected.Writes);
    }

    [TestMethod]
    public async Task RemoteReadFailureReleasesTheVerifiedLocalLeaseAndRetainsOnlyItsOriginalReceipt()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var conflict = (await catalog.ReadUploadConflictAsync(prepared.Intent.ConflictId))!;
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths);
        var worker = new KeepRemoteCopyWorker(prepared.Intent.RemoteSource) { FailReads = true };
        var preserver = new MirrorPulseConflictKeepRemotePreserver(catalog, copies, new(copies, worker, worker, worker));
        await Assert.ThrowsExactlyAsync<IOException>(() => preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes, writable: false))));
        var interrupted = (await catalog.ReadConflictKeepRemoteAsync(prepared.Intent.CommandId))!;
        Assert.IsNotNull(interrupted.LocalPreservation); Assert.IsNull(interrupted.RemoteCopy);
        string localPath = await copies.PreserveDecisionAsync(prepared.Intent.CommandId, conflict, MirrorPulseConflictPreservedSide.Local,
            _ => throw new AssertFailedException("The committed local copy already exists."));
        using (var exclusive = File.Open(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(FinalBytes.LongLength, exclusive.Length);
        worker.FailReads = false;
        await using var retained = await preserver.RetainAsync(prepared.Intent.CommandId,
            (_, _) => throw new AssertFailedException("Retry must retain the first local copy."));
        CollectionAssert.AreEqual(FinalBytes, await ReadKeepRemoteCopyAsync(retained.Local!.Data));
        Assert.HasCount(prepared.Intent.ConflictIds.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(rejected.Writes);
    }

    [TestMethod]
    public async Task LocalCopyLeaseSupportsEmptyContentAndRefusesAnotherDecisionAndChangedPrivateBytes()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var rejected = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, rejected);
        var conflict = (await catalog.ReadUploadConflictAsync(prepared.Intent.ConflictId))!;
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths);
        string path = await copies.PreserveDecisionAsync(prepared.Intent.CommandId, conflict, MirrorPulseConflictPreservedSide.Local,
            _ => ValueTask.FromResult<Stream>(new MemoryStream()));
        var empty = new MirrorPulseConflictKeepBothCopy(0, Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)));
        await using (var retained = await copies.OpenLocalDecisionAsync(prepared.Intent.CommandId, conflict, empty))
            Assert.AreEqual(0L, retained.Data.Length);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => copies.OpenLocalDecisionAsync(Guid.NewGuid(), conflict, empty));
        await File.WriteAllBytesAsync(path, SelectedRemoteBytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => copies.OpenLocalDecisionAsync(prepared.Intent.CommandId, conflict, empty));
    }

    private static async Task<byte[]> ReadKeepRemoteCopyAsync(Stream stream)
    {
        stream.Position = 0; using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray();
    }

    private sealed class KeepRemoteCopyWorker(MirrorPulseConflictRemoteSource source) :
        IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        internal int Calls { get; private set; }
        internal bool Offline { get; set; }
        internal bool FailReads { get; set; }
        private void Observe(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new AssertFailedException("Committed private copies must not access any Worker transport.");
            Calls++;
            if (FailReads) throw new IOException("fixture Worker failure before a source read");
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Observe(cancellationToken); Assert.AreEqual(source.RelativePath, request.NormalizedPath); Assert.AreEqual(source.RootKey, request.RootKey);
            Assert.AreEqual(source.InstanceId, request.InstanceId.ToString()); return ValueTask.FromResult<string?>(source.RemoteRevision);
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
        {
            Observe(cancellationToken); Assert.AreEqual(source.RootKey, request.RootKey);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new(source.RemoteId, source.RemoteRevision, "File",
                source.RelativePath, source.Length, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Observe(cancellationToken); Assert.AreEqual(source.RelativePath, request.NormalizedPath); Assert.AreEqual(source.RootKey, request.RootKey);
            return ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemoteBytes, checked((int)request.Offset), checked((int)request.Length), false));
        }
    }
}
