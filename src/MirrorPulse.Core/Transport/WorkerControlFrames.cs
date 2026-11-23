using System.Buffers.Binary;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Transport;

internal static class WorkerControlFrames
{
    public static async Task<ControlFrameEnvelope> ReadAsync(Stream pipe, CancellationToken cancellationToken) =>
        ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(pipe, cancellationToken).ConfigureAwait(false));

    public static async Task WriteAsync(Stream pipe, ControlFrameEnvelope frame, CancellationToken cancellationToken)
    {
        byte[] payload = ControlFrameJsonCodec.Encode(frame);
        byte[] prefix = new byte[ControlFrameLimits.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
