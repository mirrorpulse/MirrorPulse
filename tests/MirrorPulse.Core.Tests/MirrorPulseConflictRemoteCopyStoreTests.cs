using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseConflictRemoteCopyStoreTests
{
    [TestMethod]
    public async Task BoundedRangesCommitTheSelectedObjectVersionAndCompleteByteProof()
    {
        using var fixture = new Fixture();
        fixture.Worker.Bytes = new byte[2 * MirrorPulseConflictRemoteCopyStore.MaximumRangeLength + 13];
        Random.Shared.NextBytes(fixture.Worker.Bytes);
        var source = await fixture.CaptureAsync();
        string path = await fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source);
        var manifest = JsonSerializer.Deserialize<MirrorPulseConflictCopyManifest>(await File.ReadAllTextAsync(path + ".manifest.json"))!;
        Assert.AreEqual(2, manifest.SchemaVersion);
        Assert.AreEqual(source, manifest.RemoteSource);
        Assert.AreEqual(fixture.CommandId, manifest.RemoteDecisionId);
        Assert.AreEqual(source.Length, manifest.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(fixture.Worker.Bytes)), manifest.Sha256);
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(3, fixture.Worker.Ranges);
        foreach (var range in fixture.Worker.Ranges)
        {
            Assert.AreEqual(fixture.Conflict.InstanceId, range.InstanceId);
            Assert.AreEqual("root-1", range.RootKey);
            Assert.AreEqual("Docs/note.bin", range.NormalizedPath);
            Assert.IsLessThanOrEqualTo((long)MirrorPulseConflictRemoteCopyStore.MaximumRangeLength, range.Length);
        }
        CollectionAssert.AreEqual(new long[] { 0, 1048576, 2097152 }, fixture.Worker.Ranges.Select(range => range.Offset).ToArray());
    }

    [TestMethod]
    [DataRow("DataCommitted")]
    [DataRow("ManifestCommitted")]
    public async Task ReopenAfterACommittedBoundaryUsesTheOriginalCopyWithoutAnyWorkerAccess(string boundary)
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths, checkpoint =>
        {
            if (checkpoint.ToString() == boundary) throw new IOException("fixture durable copy interruption");
        });
        var reader = new MirrorPulseConflictRemoteCopyStore(copies, fixture.Worker, fixture.Worker, fixture.Worker);
        await Assert.ThrowsExactlyAsync<IOException>(() => reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        fixture.Worker.Offline = true;
        string path = await fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source);
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(path, await fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        Assert.IsFalse(File.Exists(path + ".pending.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId,
            fixture.Conflict, source with { RemoteId = "replacement" }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId,
            fixture.Conflict, source with { RootKey = "other-root" }));
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task AVersionChangeDuringReadingNeverCommitsTheCopy()
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        fixture.Worker.AfterRange = _ => fixture.Worker.Revision = "new-remote-version";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        fixture.RequireNoCommittedCopy();
    }

    [TestMethod]
    public async Task AnObjectReplacementWithTheSameVersionIsRejectedBeforeEofAuthorizesCommit()
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        fixture.Worker.AfterRange = _ => fixture.Worker.RemoteId = "replacement-object";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        fixture.RequireNoCommittedCopy();
    }

    [TestMethod]
    public async Task EmptyContentStillRequiresTheFinalObjectAndVersionCheck()
    {
        using var fixture = new Fixture();
        fixture.Worker.Bytes = [];
        var source = await fixture.CaptureAsync();
        fixture.Worker.BeforeMetadata = number =>
        {
            if (number == 3) fixture.Worker.RemoteId = "empty-replacement";
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        fixture.RequireNoCommittedCopy();
        Assert.IsEmpty(fixture.Worker.Ranges);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public async Task AShortOrOverlongRangeCannotProduceACompleteManifest(int difference)
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        fixture.Worker.RangeLengthDifference = difference;
        if (difference < 0)
            await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        fixture.RequireNoCommittedCopy();
    }

    [TestMethod]
    public async Task CancellationDuringTheOriginalReadRemainsCancellationAndLeavesNoCompleteCopy()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var source = await fixture.CaptureAsync();
        fixture.Worker.AfterRange = _ => cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Reader.PreserveAsync(
            fixture.CommandId, fixture.Conflict, source, cancellation.Token));
        fixture.RequireNoCommittedCopy();
    }

    [TestMethod]
    [DataRow("deleted")]
    [DataRow("directory")]
    [DataRow("unknown-length")]
    [DataRow("metadata-version")]
    [DataRow("stat-version")]
    public async Task CaptureRequiresTheOriginalObservedFileVersion(string failure)
    {
        using var fixture = new Fixture();
        fixture.Worker.Failure = failure;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CaptureAsync());
        Assert.IsEmpty(fixture.Worker.Ranges);
        fixture.RequireNoCommittedCopy();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MetadataCursorsCannotLoopOrExceedTheirBoundedSearch(bool uniqueCursors)
    {
        using var fixture = new Fixture();
        fixture.Worker.PaginateWithoutTarget = true;
        fixture.Worker.UniqueCursors = uniqueCursors;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CaptureAsync());
        Assert.AreEqual(uniqueCursors ? MirrorPulseConflictRemoteCopyStore.MaximumDirectoryPages : 2, fixture.Worker.MetadataReads);
        Assert.IsEmpty(fixture.Worker.Ranges);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedMetadataResponsesCannotGrowThePreservationSearch(bool oversizedCursor)
    {
        using var fixture = new Fixture();
        fixture.Worker.Failure = oversizedCursor ? "oversized-cursor" : "oversized-page";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CaptureAsync());
        Assert.AreEqual(1, fixture.Worker.MetadataReads);
        Assert.IsEmpty(fixture.Worker.Ranges);
    }

    [TestMethod]
    public async Task AnUnboundLegacyCopyCannotAcquireHistoricalRemoteProvenance()
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        string legacy = await new MirrorPulseConflictCopyStore(fixture.Paths).PreserveDecisionAsync(
            fixture.CommandId, fixture.Conflict, MirrorPulseConflictPreservedSide.Remote,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(fixture.Worker.Bytes)));
        fixture.Worker.Offline = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source));
        var manifest = JsonSerializer.Deserialize<MirrorPulseConflictCopyManifest>(await File.ReadAllTextAsync(legacy + ".manifest.json"))!;
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.IsNull(manifest.RemoteSource);
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(legacy));
    }

    [TestMethod]
    public async Task ARemoteManifestFromAnotherCommandCannotAuthorizeThisDecision()
    {
        using var fixture = new Fixture();
        var source = await fixture.CaptureAsync();
        string original = await fixture.Reader.PreserveAsync(fixture.CommandId, fixture.Conflict, source);
        string other = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(original))!,
            Guid.NewGuid().ToString("D"), Path.GetFileName(original));
        Guid otherCommand = Guid.Parse(Path.GetFileName(Path.GetDirectoryName(other))!);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.Copy(original, other);
        File.Copy(original + ".manifest.json", other + ".manifest.json");
        fixture.Worker.Offline = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.PreserveAsync(otherCommand, fixture.Conflict, source));
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(original));
        CollectionAssert.AreEqual(fixture.Worker.Bytes, await File.ReadAllBytesAsync(other));
    }

    [TestMethod]
    [DataRow("../outside")]
    [DataRow("Docs/../outside")]
    [DataRow("/absolute")]
    [DataRow("Docs\\note.bin")]
    [DataRow("Docs//note.bin")]
    public async Task CaptureRejectsNoncanonicalWorkerPathsBeforeAccess(string path)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader.CaptureSourceAsync(fixture.Conflict, "root-1", path));
        Assert.AreEqual(0, fixture.Worker.StatReads);
        Assert.AreEqual(0, fixture.Worker.MetadataReads);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-remote-copy"));
        private readonly string _directory;
        internal MirrorPulseStoragePaths Paths { get; }
        internal MirrorPulseConflictRecord Conflict { get; } = MirrorPulseConflictCopyStoreTests.Create();
        internal Guid CommandId { get; } = Guid.NewGuid();
        internal Worker Worker { get; } = new();
        internal MirrorPulseConflictRemoteCopyStore Reader { get; }
        internal Fixture()
        {
            _directory = Path.Combine(_base, Guid.NewGuid().ToString("N"));
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            Directory.CreateDirectory(Paths.DataRootPath);
            Reader = new(new MirrorPulseConflictCopyStore(Paths), Worker, Worker, Worker);
        }
        internal Task<MirrorPulseConflictRemoteSource> CaptureAsync() => Reader.CaptureSourceAsync(Conflict, "root-1", "Docs/note.bin");
        internal void RequireNoCommittedCopy()
        {
            Assert.IsEmpty(Directory.GetFiles(Paths.DataRootPath, "*.manifest.json", SearchOption.AllDirectories));
            string destination = Path.Combine(MirrorPulseConflictDirectory.GetPath(Paths, Conflict.InstanceId),
                "remote", CommandId.ToString("D"), MirrorPulseConflictFileName.Create(Conflict));
            Assert.IsFalse(File.Exists(destination));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(_directory).StartsWith(_base + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The fixture escaped its temporary root.");
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Worker : IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        internal byte[] Bytes { get; set; } = "original remote bytes"u8.ToArray();
        internal string Revision { get; set; } = "remote";
        internal string RemoteId { get; set; } = "stable-remote-object";
        internal bool Offline { get; set; }
        internal string? Failure { get; set; }
        internal int RangeLengthDifference { get; set; }
        internal bool PaginateWithoutTarget { get; set; }
        internal bool UniqueCursors { get; set; }
        internal int MetadataReads { get; private set; }
        internal int StatReads { get; private set; }
        internal List<MirrorPulseWorkerReadRangeRequest> Ranges { get; } = [];
        internal Action<int>? AfterRange { get; set; }
        internal Action<int>? BeforeMetadata { get; set; }

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            RequireOnline(cancellationToken); StatReads++;
            return ValueTask.FromResult<string?>(Failure == "stat-version" ? "other-version" : Revision);
        }

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
        {
            RequireOnline(cancellationToken); MetadataReads++; BeforeMetadata?.Invoke(MetadataReads);
            Assert.AreEqual("Docs", request.NormalizedPath);
            Assert.AreEqual("root-1", request.RootKey);
            Assert.AreEqual(256, request.PageSize);
            if (Failure == "oversized-cursor")
                return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([], new byte[4097], false));
            if (Failure == "oversized-page")
                return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(Enumerable.Range(0, 257).Select(index =>
                    new MirrorPulseWorkerDirectoryEntry("other", Revision, "File", $"Docs/{index}.bin", 0, null, null, false)).ToArray(),
                    ReadOnlyMemory<byte>.Empty, true));
            if (PaginateWithoutTarget)
                return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([], BitConverter.GetBytes(UniqueCursors ? MetadataReads : 1), false));
            var metadata = new MirrorPulseWorkerDirectoryEntry(RemoteId,
                Failure == "metadata-version" ? "other-version" : Revision, Failure == "directory" ? "Directory" : "File",
                "Docs/note.bin", Failure == "unknown-length" ? null : Bytes.LongLength, null, null, Failure == "deleted");
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([metadata], ReadOnlyMemory<byte>.Empty, true));
        }

        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            RequireOnline(cancellationToken); Ranges.Add(request);
            byte[] returned = new byte[checked((int)request.Length + RangeLengthDifference)];
            Bytes.AsSpan((int)request.Offset, Math.Min(returned.Length, (int)request.Length)).CopyTo(returned);
            AfterRange?.Invoke(Ranges.Count);
            return ValueTask.FromResult<Stream>(new MemoryStream(returned));
        }

        private void RequireOnline(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new AssertFailedException("A committed original copy must not contact the Worker.");
        }
    }
}
