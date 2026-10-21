using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseTransferSourceDataLeaseTests
{
    [TestMethod]
    public async Task SourceDataGuardVerifiesOriginalBytesAndKeepsPublicDeletionPossibleUntilItDrains()
    {
        using var fixture = new SourceFixture();
        await using var guard = MirrorPulseTransferSourceDataLease.Open(fixture.Path);
        await guard.VerifyAsync(fixture.Bytes.LongLength, fixture.Hash, CancellationToken.None);
        File.Delete(fixture.Path);
        Assert.IsFalse(File.Exists(fixture.Path));
        await guard.VerifyAsync(fixture.Bytes.LongLength, fixture.Hash, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceDataGuardRejectsExistingWriterOrWritableMappingWithItsInputStreamClosed(bool mapping)
    {
        using var fixture = new SourceFixture();
        using var input = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var mapped = mapping ? MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true) : null;
        using var view = mapped?.CreateViewAccessor();
        if (mapping) input.Dispose();
        Assert.ThrowsExactly<IOException>(() => MirrorPulseTransferSourceDataLease.Open(fixture.Path));
        if (view is not null) { view.Write(0, (byte)91); view.Flush(); }
        Assert.IsTrue(File.Exists(fixture.Path));
    }

    [TestMethod]
    public async Task SourceDataGuardBlocksLateWritersAndCancellationCannotReleaseItsProtection()
    {
        using var fixture = new SourceFixture();
        var guard = MirrorPulseTransferSourceDataLease.Open(fixture.Path);
        try
        {
            Assert.ThrowsExactly<IOException>(() => { using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => guard.VerifyAsync(fixture.Bytes.LongLength, fixture.Hash, cancelled.Token));
            Assert.ThrowsExactly<IOException>(() => { using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
        }
        finally { await guard.DisposeAsync(); }
        await File.WriteAllBytesAsync(fixture.Path, [8, 9]);
        CollectionAssert.AreEqual(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(fixture.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceDataGuardCannotVerifyChangedLengthOrWholeHash(bool length)
    {
        using var fixture = new SourceFixture();
        await using (var guard = MirrorPulseTransferSourceDataLease.Open(fixture.Path))
            await Assert.ThrowsExactlyAsync<IOException>(() => guard.VerifyAsync(length ? fixture.Bytes.LongLength + 1 : fixture.Bytes.LongLength,
                length ? fixture.Hash : new string('F', 64), CancellationToken.None));
        CollectionAssert.AreEqual(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Path));
    }

    private sealed class SourceFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MirrorPulse-source-data-tests", Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal byte[] Bytes { get; } = Enumerable.Range(0, 8193).Select(value => (byte)(value * 17)).ToArray();
        internal string Hash => Convert.ToHexString(SHA256.HashData(Bytes));
        internal SourceFixture()
        {
            Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "original.bin"); File.WriteAllBytes(Path, Bytes);
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
