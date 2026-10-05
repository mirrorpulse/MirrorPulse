using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Transport;

/// <summary>
/// Reads little-endian length-prefixed control frames from a byte stream.
/// </summary>
public static class LengthPrefixedFrameReader
{
    public static ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        => ReadAsync(stream, (int)ControlFrameLimits.MaxPayloadBytes, cancellationToken);

    public static async ValueTask<byte[]> ReadAsync(Stream stream, int maximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        var lengthPrefix = new byte[ControlFrameLimits.LengthPrefixBytes];
        await ReadExactlyAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false);
        var payloadLength = ControlFrameLimits.ReadPayloadLength(lengthPrefix);
        if (maximumPayloadBytes < 0 || payloadLength > maximumPayloadBytes)
        {
            throw new InvalidDataException("The frame exceeds the selected protocol limit.");
        }
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return payload;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The stream ended before the complete frame was received.");
            }

            offset += read;
        }
    }
}
