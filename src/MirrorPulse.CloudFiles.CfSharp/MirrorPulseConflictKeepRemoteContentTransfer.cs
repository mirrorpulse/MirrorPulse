using System.Security.Cryptography;
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
    internal static async Task<string> TransferRetainedAsync(FileStream source, MirrorPulseConflictKeepBothCopy proof,
        Func<long, ReadOnlyMemory<byte>, CancellationToken, ValueTask> transfer,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> retrieve, CancellationToken token)
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
