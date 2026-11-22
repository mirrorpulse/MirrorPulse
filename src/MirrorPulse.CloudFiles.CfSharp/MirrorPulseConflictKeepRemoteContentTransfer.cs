using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Verifies retained private bytes, delivers bounded ranges and checks complete readback.</summary>
/// <remarks>
/// Delegates supply byte transport only. This helper provides no native object, identity,
/// metadata, journal or durable application authority. Native integration must separately prove
/// same-object admission and readback without reentering a path-bound facade while retaining
/// its proactive transfer lease. An exception leaves the caller responsible for partial work.
/// </remarks>
internal static class MirrorPulseConflictKeepRemoteContentTransfer
{
    internal const int BufferSize = 64 * 1024;

    /// <summary>Bounded byte boundary. The injected delegates do not establish any native or journal authority.</summary>
    internal static Task<string> TransferRetainedAsync(FileStream source, MirrorPulseConflictKeepBothCopy proof,
        Func<long, ReadOnlyMemory<byte>, CancellationToken, ValueTask> transfer,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> retrieve, CancellationToken token) =>
        TransferCoreAsync(source, proof, transfer, retrieve, null, null, token);

    /// <summary>Copies the private version onto an already admitted and sealed resident file handle.</summary>
    /// <remarks>
    /// The caller independently proves that the source and destination are distinct retained objects,
    /// the destination is fully resident, and the original content and permission action are durable.
    /// The callback persists the before-write fact. It runs after private authentication and before
    /// truncation, including for empty files. Failure leaves recovery and access sealed by the caller.
    /// This byte result alone does not authorize a placeholder update, access restoration or ACK.
    /// </remarks>
    internal static async Task<string> CopyResidentAsync(FileStream source, MirrorPulseConflictKeepBothCopy proof,
        FileStream destination, Func<CancellationToken, ValueTask> retainWriteStart, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(retainWriteStart);
        if (ReferenceEquals(source, destination) || !destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("The resident copy requires a distinct retained read/write destination.", nameof(destination));
        SafeFileHandle? handle = null;
        string result = await TransferCoreAsync(source, proof,
            (offset, bytes, stop) => RandomAccess.WriteAsync(handle!, bytes, offset, stop), ReadResidentAsync,
            BeginResidentWriteAsync, FlushResident, token).ConfigureAwait(false);
        if (destination.Length != proof.Length) throw new InvalidDataException("The resident destination changed length after complete readback.");
        return result;

        async ValueTask BeginResidentWriteAsync(CancellationToken stop)
        {
            await retainWriteStart(stop).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            destination.SetLength(0);
            handle = destination.SafeFileHandle;
        }

        ValueTask FlushResident(CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            if (destination.Length != proof.Length) throw new InvalidDataException("The resident destination differs from its selected complete length.");
            destination.Flush(flushToDisk: true);
            return ValueTask.CompletedTask;
        }

        async ValueTask<int> ReadResidentAsync(long offset, Memory<byte> bytes, CancellationToken stop)
        {
            int received = 0;
            while (received < bytes.Length)
            {
                int count = await RandomAccess.ReadAsync(handle!, bytes[received..], offset + received, stop).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
            }
            return received;
        }
    }

    private static async Task<string> TransferCoreAsync(FileStream source, MirrorPulseConflictKeepBothCopy proof,
        Func<long, ReadOnlyMemory<byte>, CancellationToken, ValueTask> transfer,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> retrieve,
        Func<CancellationToken, ValueTask>? beginDelivery, Func<CancellationToken, ValueTask>? completeDelivery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(transfer); ArgumentNullException.ThrowIfNull(retrieve);
        token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || proof.Length < 0 || source.Length != proof.Length ||
            proof.Sha256 is not { Length: 64 } || proof.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new InvalidDataException("KeepRemote requires the complete originally selected readable private copy and canonical byte proof.");
        byte[] buffer = new byte[BufferSize];
        // Authenticate all original private bytes before the first native range, using absolute
        // offsets so the retained lease's stream position is never shared with another reader.
        if (await HashSourceAsync().ConfigureAwait(false) != proof.Sha256)
            throw new InvalidDataException("The selected private KeepRemote copy changed before byte delivery.");
        if (beginDelivery is not null) await beginDelivery(token).ConfigureAwait(false);
        using var written = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < proof.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
            await ReadSourceExactlyAsync(offset, buffer.AsMemory(0, count)).ConfigureAwait(false);
            written.AppendData(buffer, 0, count);
            await transfer(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            offset += count;
        }
        if (source.Length != proof.Length || Convert.ToHexString(written.GetHashAndReset()) != proof.Sha256)
            throw new InvalidDataException("The selected KeepRemote source changed during byte delivery.");
        if (completeDelivery is not null) await completeDelivery(token).ConfigureAwait(false);
        using var readback = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < proof.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
            int received = await retrieve(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (received != count) throw new InvalidDataException("KeepRemote requires complete native range readback without a short or excessive result.");
            readback.AppendData(buffer, 0, received); offset += received;
        }
        string result = Convert.ToHexString(readback.GetHashAndReset());
        if (result != proof.Sha256 || source.Length != proof.Length || await HashSourceAsync().ConfigureAwait(false) != proof.Sha256)
            throw new InvalidDataException("KeepRemote requires exact whole-file native readback and the unchanged original private bytes.");
        token.ThrowIfCancellationRequested(); return result;

        async Task<string> HashSourceAsync()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < proof.Length;)
            {
                int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
                await ReadSourceExactlyAsync(offset, buffer.AsMemory(0, count)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count); offset += count;
            }
            if (source.Length != proof.Length) throw new InvalidDataException("The original private KeepRemote copy changed length.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        async Task ReadSourceExactlyAsync(long offset, Memory<byte> bytes)
        {
            int received = 0;
            while (received < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = await RandomAccess.ReadAsync(source.SafeFileHandle, bytes[received..], offset + received, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The selected private KeepRemote source ended before its retained complete length.");
                received += count;
            }
        }
    }
}
