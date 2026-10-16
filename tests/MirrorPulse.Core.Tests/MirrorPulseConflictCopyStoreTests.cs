using System.Text;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseConflictCopyStoreTests
{
    [TestMethod]
    [DataRow("DataCommitted")]
    [DataRow("ManifestCommitted")]
    public async Task RestartAfterCopyCommitUsesVerifiedBytesWithoutReopeningChangedSource(string boundary)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-copy-replay", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        var conflict = Create();
        try
        {
            var interrupted = new MirrorPulseConflictCopyStore(paths, point =>
            {
                if (point.ToString() == boundary) throw new IOException("fixture after durable copy boundary");
            });
            await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Remote,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("original remote bytes"u8.ToArray()))));
            var reopened = new MirrorPulseConflictCopyStore(paths);
            string destination = await reopened.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Remote,
                _ => throw new InvalidOperationException("The source may have changed or disappeared after preserving it."));
            Assert.AreEqual("original remote bytes", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(destination, await reopened.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Remote,
                _ => throw new InvalidOperationException("A second replay must use the same verified copy.")));
            Assert.IsFalse(File.Exists(destination + ".pending.json"));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public async Task TamperedBytesOrManifestNeverOverwriteAnotherConflict()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-copy-verification", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        var conflict = Create();
        var store = new MirrorPulseConflictCopyStore(paths);
        try
        {
            string destination = await store.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Local,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("first"u8.ToArray())));
            await File.WriteAllTextAsync(destination, "other");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Local,
                _ => throw new InvalidOperationException("No source overwrite is permitted.")));
            Assert.AreEqual("other", await File.ReadAllTextAsync(destination));
            await File.WriteAllTextAsync(destination, "first");
            string manifestPath = destination + ".manifest.json";
            var manifest = JsonSerializer.Deserialize<MirrorPulseConflictCopyManifest>(await File.ReadAllTextAsync(manifestPath))!;
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { ConflictId = Guid.NewGuid() }));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Local,
                _ => throw new InvalidOperationException("Another conflict cannot be overwritten.")));
            Assert.AreEqual("first", await File.ReadAllTextAsync(destination));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public async Task CommittedCopyIsVerifiedAndPartialCopyNeverAuthorizesDestruction()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-copy-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        string source = Path.Combine(paths.SyncRootPath, "note.txt");
        await File.WriteAllTextAsync(source, "original content");
        var store = new MirrorPulseConflictCopyStore(paths);
        MirrorPulseConflictRecord conflict = Create();
        try
        {
            string path = await store.PreserveAsync(conflict, MirrorPulseConflictPreservedSide.Local, token => store.OpenLocalAsync(conflict, token));
            MirrorPulseConflictCopyManifest manifest = JsonSerializer.Deserialize<MirrorPulseConflictCopyManifest>(await File.ReadAllTextAsync(path + ".manifest.json"))!;
            Assert.AreEqual("complete", manifest.State);
            Assert.AreEqual(new FileInfo(path).Length, manifest.Length);
            Assert.AreEqual(64, manifest.Sha256!.Length);
            Assert.IsFalse(File.Exists(path + ".pending.json"));
            Assert.AreEqual("original content", await File.ReadAllTextAsync(source));
            MirrorPulseConflictRecord failing = Create();
            await Assert.ThrowsExactlyAsync<IOException>(() => store.PreserveAsync(failing, MirrorPulseConflictPreservedSide.Remote,
                _ => ValueTask.FromResult<Stream>(new FailingSource())));
            string directory = Path.Combine(MirrorPulseConflictDirectory.GetPath(paths, failing.InstanceId), "remote");
            string failedPath = Path.Combine(directory, MirrorPulseConflictFileName.Create(failing));
            Assert.IsFalse(File.Exists(failedPath));
            Assert.IsFalse(File.Exists(failedPath + ".manifest.json"));
            Assert.IsTrue(File.Exists(failedPath + ".pending.json"));
            Assert.AreEqual("original content", await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public async Task DecisionCopiesRemainIndependentWhileAReadGuardBlocksWritesAndAllowsRetirement()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-deletion-copy", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        Directory.CreateDirectory(paths.SyncRootPath); Directory.CreateDirectory(paths.DataRootPath);
        string source = Path.Combine(paths.SyncRootPath, "latest.bin"); var conflict = Create();
        var copies = new MirrorPulseConflictCopyStore(paths); Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        try
        {
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            string original = await copies.PreserveDecisionAsync(first, conflict, MirrorPulseConflictPreservedSide.Local,
                _ => ValueTask.FromResult<Stream>(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)));
            await File.WriteAllBytesAsync(source, [4, 5, 6, 7]);
            string latest;
            await using (var guard = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Assert.ThrowsExactly<IOException>(() => { using var writer = File.OpenWrite(source); });
                latest = await copies.PreserveDecisionAsync(second, conflict, MirrorPulseConflictPreservedSide.Local,
                    _ => ValueTask.FromResult<Stream>(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)));
                File.Delete(source);
                Assert.IsFalse(File.Exists(source));
                Assert.AreEqual(4L, guard.Length);
            }
            Assert.AreNotEqual(original, latest);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(latest));
            Assert.AreEqual(latest, await copies.PreserveDecisionAsync(second, conflict, MirrorPulseConflictPreservedSide.Local,
                _ => throw new AssertFailedException("The retired source cannot be reopened during replay.")));
        }
        finally { Directory.Delete(fixture, true); }
    }

    private sealed class FailingSource : MemoryStream
    {
        private bool _read;
        public FailingSource() : base(Encoding.UTF8.GetBytes("partial content")) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("fixture source failure after partial bytes");
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
    }

    internal static MirrorPulseConflictRecord Create() => new(Guid.NewGuid(), InstanceId.New(), "change", "note.txt",
        MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, "base", "remote", DateTimeOffset.UtcNow);
}
