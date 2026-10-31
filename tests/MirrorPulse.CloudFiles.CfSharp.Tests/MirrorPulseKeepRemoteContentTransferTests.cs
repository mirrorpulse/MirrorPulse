using System.Security.Cryptography;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseKeepRemoteContentTransferTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(4095)]
    [DataRow(4096)]
    [DataRow(65536)]
    [DataRow(65537)]
    [DataRow(131073)]
    public async Task OriginalPrivateBytesUseBoundedAlignedRangesAndWholeReadbackWithoutSharingStreamPosition(int length)
    {
        await using var fixture = await Fixture.OpenAsync(length);
        long position = fixture.Source.Position;
        Assert.AreEqual(fixture.Proof.Sha256, await fixture.CopyAsync());
        Assert.AreEqual(position, fixture.Source.Position);
        CollectionAssert.AreEqual(fixture.Bytes, fixture.Native);
        Assert.HasCount((length + 65535) / 65536, fixture.Writes);
        CollectionAssert.AreEqual(fixture.Writes.ToArray(), fixture.Reads.ToArray());
        foreach (var (offset, count) in fixture.Writes)
        {
            Assert.AreEqual(0L, offset % 4096);
            Assert.IsTrue(count is > 0 and <= 65536);
            Assert.IsTrue(count % 4096 == 0 || offset + count == length);
        }
        Assert.IsTrue(fixture.Source.CanRead);
        Assert.IsTrue(fixture.Source.CanWrite, "The byte boundary must not dispose the caller's lease.");
    }

    [TestMethod]
    [DataRow("write-first")]
    [DataRow("write-last")]
    [DataRow("read-first")]
    [DataRow("corrupt-read")]
    [DataRow("short-read")]
    [DataRow("excess-read")]
    [DataRow("zero-read")]
    public async Task NativeFailureOrIncompleteReadbackCannotReturnAWholeFileResult(string fault)
    {
        await using var fixture = await Fixture.OpenAsync(65537);
        fixture.Fault = fault;
        if (fault is "write-first" or "write-last" or "read-first")
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => fixture.CopyAsync());
            Assert.AreEqual("synthetic native transfer fault", error.Message);
        }
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CopyAsync());
        Assert.IsTrue(fixture.Source.CanRead);
        CollectionAssert.AreEqual(fixture.Bytes, await fixture.ReadSourceAsync());
        if (fault.StartsWith("write-", StringComparison.Ordinal)) Assert.HasCount(0, fixture.Reads);
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("lowercase")]
    [DataRow("malformed")]
    public async Task InvalidOrChangedPrivateProofRefusesBeforeAnyNativeRange(string damage)
    {
        await using var fixture = await Fixture.OpenAsync(65537);
        var proof = damage switch
        {
            "length" => fixture.Proof with { Length = fixture.Proof.Length + 1 },
            "hash" => fixture.Proof with { Sha256 = new string('A', 64) },
            "lowercase" => fixture.Proof with { Sha256 = fixture.Proof.Sha256.ToLowerInvariant() },
            "malformed" => fixture.Proof with { Sha256 = new string('Z', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CopyAsync(proof));
        Assert.HasCount(0, fixture.Writes); Assert.HasCount(0, fixture.Reads);
        Assert.IsTrue(fixture.Source.CanRead);
    }

    [TestMethod]
    [DataRow("before")]
    [DataRow("write")]
    [DataRow("read")]
    public async Task CancellationCannotTurnPartialNativeWorkIntoAReadbackOrReleaseTheOriginalSource(string stage)
    {
        await using var fixture = await Fixture.OpenAsync(65537);
        using var cancellation = new CancellationTokenSource();
        fixture.DuringWrite = stage == "write" ? cancellation.Cancel : null;
        fixture.DuringRead = stage == "read" ? cancellation.Cancel : null;
        if (stage == "before") cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.CopyAsync(token: cancellation.Token));
        Assert.IsTrue(fixture.Source.CanRead);
        if (stage == "before") Assert.HasCount(0, fixture.Writes);
        if (stage != "read") Assert.HasCount(0, fixture.Reads);
        CollectionAssert.AreEqual(fixture.Bytes, await fixture.ReadSourceAsync());
    }

    [TestMethod]
    [DataRow("write")]
    [DataRow("read")]
    [DataRow("length")]
    public async Task RetainedSourceChangesDuringNativeWorkCannotYieldASelectedVersionResult(string stage)
    {
        await using var fixture = await Fixture.OpenAsync(65537);
        void Change()
        {
            if (stage == "length") fixture.Source.SetLength(fixture.Bytes.Length + 1);
            else
            {
                fixture.Source.Position = 0; fixture.Source.WriteByte((byte)(fixture.Bytes[0] ^ 255));
                fixture.Source.Flush(flushToDisk: true);
            }
        }
        fixture.DuringWrite = stage == "read" ? null : Change;
        fixture.DuringRead = stage == "read" ? Change : null;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.CopyAsync());
        Assert.IsTrue(fixture.Source.CanRead);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MirrorPulse-keep-remote-transfer", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "selected-private.bin");
        internal FileStream Source { get; private set; } = null!;
        internal byte[] Bytes { get; private init; } = [];
        internal byte[] Native { get; private init; } = [];
        internal MirrorPulseConflictKeepBothCopy Proof => new(Bytes.LongLength, Convert.ToHexString(SHA256.HashData(Bytes)));
        internal List<(long Offset, int Count)> Writes { get; } = [];
        internal List<(long Offset, int Count)> Reads { get; } = [];
        internal string? Fault { get; set; }
        internal Action? DuringWrite { get; set; }
        internal Action? DuringRead { get; set; }

        internal static async Task<Fixture> OpenAsync(int length)
        {
            var fixture = new Fixture { Bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray(), Native = new byte[length] };
            Directory.CreateDirectory(fixture._directory);
            await File.WriteAllBytesAsync(fixture.Path, fixture.Bytes);
            fixture.Source = new(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            fixture.Source.Position = Math.Min(17, length); return fixture;
        }

        internal Task<string> CopyAsync(MirrorPulseConflictKeepBothCopy? proof = null, CancellationToken token = default) =>
            MirrorPulseConflictKeepRemoteContentTransfer.TransferRetainedAsync(Source, proof ?? Proof, TransferAsync, RetrieveAsync, token);

        internal async Task<byte[]> ReadSourceAsync()
        {
            byte[] result = new byte[Bytes.Length]; int received = 0;
            while (received < result.Length)
            {
                int count = await RandomAccess.ReadAsync(Source.SafeFileHandle, result.AsMemory(received), received);
                if (count == 0) throw new EndOfStreamException("The original fixture source was truncated.");
                received += count;
            }
            Assert.AreEqual(Bytes.LongLength, Source.Length); return result;
        }

        private ValueTask TransferAsync(long offset, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.HasCount(0, Reads, "All original ranges must transfer before whole native readback starts.");
            Writes.Add((offset, bytes.Length));
            if (Fault == "write-first" || Fault == "write-last" && offset != 0) throw new IOException("synthetic native transfer fault");
            bytes.CopyTo(Native.AsMemory(checked((int)offset), bytes.Length)); DuringWrite?.Invoke(); return ValueTask.CompletedTask;
        }

        private ValueTask<int> RetrieveAsync(long offset, Memory<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads.Add((offset, bytes.Length));
            if (Fault == "read-first") throw new IOException("synthetic native transfer fault");
            Native.AsMemory(checked((int)offset), bytes.Length).CopyTo(bytes);
            if (Fault == "corrupt-read") bytes.Span[0] ^= 255;
            DuringRead?.Invoke();
            return ValueTask.FromResult(Fault switch { "short-read" => bytes.Length - 1, "excess-read" => bytes.Length + 1, "zero-read" => 0, _ => bytes.Length });
        }

        public async ValueTask DisposeAsync()
        {
            if (Source is not null) await Source.DisposeAsync();
            string path = System.IO.Path.GetFullPath(_directory);
            string prefix = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MirrorPulse-keep-remote-transfer")) + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its private temporary directory.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
