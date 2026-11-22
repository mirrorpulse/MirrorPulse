using System.Security.Cryptography;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseLocalImportStagingStoreTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(2_097_409)]
    public async Task ExactInputSurvivesSourceRemovalAndStoreRestart(int length)
    {
        using var fixture = new Fixture();
        byte[] bytes = new byte[length];
        new Random(17).NextBytes(bytes);
        await File.WriteAllBytesAsync(fixture.Source, bytes);
        Guid operationId = Guid.NewGuid();
        var first = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var content = await first.StageAsync(operationId, fixture.Source);
        Assert.AreEqual((long)bytes.Length, content.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), content.Sha256);
        File.Delete(fixture.Source);
        var second = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        await using (var input = await second.OpenVerifiedAsync(operationId, content))
        {
            Assert.AreEqual(0L, input.Position);
            using var received = new MemoryStream();
            await input.CopyToAsync(received);
            CollectionAssert.AreEqual(bytes, received.ToArray());
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() => File.Delete(fixture.Input(operationId))));
            await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(fixture.Input(operationId), [9]));
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        second.RemoveCompleted(operationId);
        second.RemoveCompleted(operationId);
        Assert.IsFalse(File.Exists(fixture.Input(operationId)));
        Assert.HasCount(0, Directory.GetFiles(fixture.InputDirectory));
    }

    [TestMethod]
    public async Task RepeatedPreparationCannotOverwriteDifferentInputOrLeaveTemporaryBytes()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, [1, 2, 3]);
        Guid operationId = Guid.NewGuid();
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var original = await store.StageAsync(operationId, fixture.Source);
        Assert.AreEqual(original, await store.StageAsync(operationId, fixture.Source));
        await File.WriteAllBytesAsync(fixture.Source, [4, 5, 6]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.StageAsync(operationId, fixture.Source));
        await using var input = await store.OpenVerifiedAsync(operationId, original);
        Assert.AreEqual(1, input.ReadByte());
        Assert.HasCount(1, Directory.GetFiles(fixture.InputDirectory));
    }

    [TestMethod]
    public async Task TypedInputLeaseBindsItsOperationAndDigestToTheReadOnlyPrivateFile()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, [5, 8, 13]);
        Guid operationId = Guid.NewGuid();
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var content = await store.StageAsync(operationId, fixture.Source);
        File.Delete(fixture.Source);
        await using var input = await store.OpenLeaseAsync(operationId, content);
        Assert.AreEqual(operationId, input.OperationId);
        Assert.AreEqual(content, input.Content);
        Assert.IsFalse(input.Data.CanWrite);
        Assert.AreEqual(0L, input.Data.Position);
        Assert.AreEqual(5, input.Data.ReadByte());
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(fixture.Input(operationId), [1]));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangedLengthOrDigestRejectsInputBeforeItCanBeCopied(bool changeLength)
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, [1, 2, 3]);
        Guid operationId = Guid.NewGuid();
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var content = await store.StageAsync(operationId, fixture.Source);
        await File.WriteAllBytesAsync(fixture.Input(operationId), changeLength ? [1] : [3, 2, 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenVerifiedAsync(operationId, content));
        // Rejection must dispose its read handle so a caller can quarantine the damaged input.
        File.Move(fixture.Input(operationId), fixture.Input(operationId) + ".damaged");
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task CanceledMissingAndRelativeSourcesDoNotCommitAnInput()
    {
        using var fixture = new Fixture();
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        Guid operationId = Guid.NewGuid();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.StageAsync(operationId, fixture.Source, canceled.Token));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => store.StageAsync(operationId, fixture.Source));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.StageAsync(operationId, "relative.bin"));
        Assert.HasCount(0, Directory.GetFiles(fixture.InputDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task ConcurrentIdenticalPreparationKeepsOneImmutableInput()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, [1, 2, 3, 4]);
        Guid operationId = Guid.NewGuid();
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var results = await Task.WhenAll(store.StageAsync(operationId, fixture.Source), store.StageAsync(operationId, fixture.Source));
        Assert.AreEqual(results[0], results[1]);
        Assert.HasCount(1, Directory.GetFiles(fixture.InputDirectory));
        await using var input = await store.OpenVerifiedAsync(operationId, results[0]);
        Assert.AreEqual(4L, input.Length);
    }

    [TestMethod]
    public async Task UnrelatedOperationAndEmptyIdentityCannotAdoptRetainedInput()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, [7]);
        var store = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var content = await store.StageAsync(Guid.NewGuid(), fixture.Source);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => store.OpenVerifiedAsync(Guid.NewGuid(), content));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.StageAsync(Guid.Empty, fixture.Source));
        Assert.ThrowsExactly<ArgumentException>(() => store.RemoveCompleted(Guid.Empty));
        Assert.HasCount(1, Directory.GetFiles(fixture.InputDirectory));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-import-input-tests", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            Source = Path.Combine(_directory, "source.bin");
        }
        internal MirrorPulseStoragePaths Paths { get; }
        internal string Source { get; }
        internal string InputDirectory => Path.Combine(Paths.DataRootPath, "state", "import-inputs");
        internal string Input(Guid operationId) => Path.Combine(InputDirectory, operationId.ToString("N") + ".bin");
        public void Dispose()
        {
            string target = Path.GetFullPath(_directory);
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-import-input-tests")) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(target).LinkTarget is not null)
                throw new InvalidOperationException("Import input cleanup requires its own ordinary temporary fixture directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
