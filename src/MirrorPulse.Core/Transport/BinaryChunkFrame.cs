using System.Buffers.Binary;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Transport;

/// <summary>
/// A binary stream chunk transported beside a control frame.
/// </summary>
public sealed class BinaryChunkFrame
{
    public BinaryChunkFrame(
        Guid requestId,
        InstanceId instanceId,
        WorkerSessionId workerSessionId,
        Guid streamId,
        long offset,
        ReadOnlyMemory<byte> data,
        bool endOfStream,
        Sha256Digest? sha256 = null)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A binary chunk request ID cannot be empty.", nameof(requestId));
        }

        if (streamId == Guid.Empty)
        {
            throw new ArgumentException("A binary chunk stream ID cannot be empty.", nameof(streamId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (data.Length > BinaryChunkCodec.MaxDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "The binary chunk exceeds the frame payload limit.");
        }

        RequestId = requestId;
        InstanceId = instanceId;
        WorkerSessionId = workerSessionId;
        StreamId = streamId;
        Offset = offset;
        Data = data.ToArray();
        EndOfStream = endOfStream;
        Sha256 = sha256;
    }

    public Guid RequestId { get; }

    public InstanceId InstanceId { get; }

    public WorkerSessionId WorkerSessionId { get; }

    public Guid StreamId { get; }

    public long Offset { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public bool EndOfStream { get; }

    public Sha256Digest? Sha256 { get; }
    public string? RootKey { get; init; }
}

/// <summary>
/// Encodes the fixed metadata header and binary payload for a chunk frame.
/// </summary>
public static class BinaryChunkCodec
{
    private const int GuidBytes = 16;
    private const int HashBytes = 32;
    private const int HeaderBytes = (GuidBytes * 4) + sizeof(long) + sizeof(uint) + sizeof(byte) + HashBytes;
    private const byte EndOfStreamFlag = 1;
    private const byte HasHashFlag = 2;

    public const int MaxDataBytes = (int)ControlFrameLimits.MaxPayloadBytes - HeaderBytes;

    public static byte[] Encode(BinaryChunkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var encoded = new byte[HeaderBytes + frame.Data.Length];
        var offset = 0;
        frame.RequestId.TryWriteBytes(encoded.AsSpan(offset, GuidBytes));
        offset += GuidBytes;
        frame.InstanceId.Value.TryWriteBytes(encoded.AsSpan(offset, GuidBytes));
        offset += GuidBytes;
        frame.WorkerSessionId.Value.TryWriteBytes(encoded.AsSpan(offset, GuidBytes));
        offset += GuidBytes;
        frame.StreamId.TryWriteBytes(encoded.AsSpan(offset, GuidBytes));
        offset += GuidBytes;
        BinaryPrimitives.WriteInt64LittleEndian(encoded.AsSpan(offset, sizeof(long)), frame.Offset);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(offset, sizeof(uint)), (uint)frame.Data.Length);
        offset += sizeof(uint);
        encoded[offset++] = (byte)((frame.EndOfStream ? EndOfStreamFlag : 0) | (frame.Sha256.HasValue ? HasHashFlag : 0));
        if (frame.Sha256 is { } sha256)
        {
            Convert.FromHexString(sha256.Hexadecimal).CopyTo(encoded.AsSpan(offset, HashBytes));
        }

        offset += HashBytes;
        frame.Data.Span.CopyTo(encoded.AsSpan(offset));
        return encoded;
    }

    public static BinaryChunkFrame Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderBytes)
        {
            throw new ArgumentException("The binary chunk frame is shorter than its header.", nameof(encoded));
        }

        var offset = 0;
        var requestId = new Guid(encoded.Slice(offset, GuidBytes));
        offset += GuidBytes;
        var instanceId = new InstanceId(new Guid(encoded.Slice(offset, GuidBytes)));
        offset += GuidBytes;
        var workerSessionId = new WorkerSessionId(new Guid(encoded.Slice(offset, GuidBytes)));
        offset += GuidBytes;
        var streamId = new Guid(encoded.Slice(offset, GuidBytes));
        offset += GuidBytes;
        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(encoded.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(encoded.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        var flags = encoded[offset++];
        if ((flags & ~(EndOfStreamFlag | HasHashFlag)) != 0)
        {
            throw new ArgumentException("The binary chunk frame contains unknown flags.", nameof(encoded));
        }

        var hashBytes = encoded.Slice(offset, HashBytes);
        offset += HashBytes;
        if (dataLength > MaxDataBytes || encoded.Length - offset != dataLength)
        {
            throw new ArgumentException("The binary chunk frame data length does not match the payload.", nameof(encoded));
        }

        Sha256Digest? sha256 = null;
        if ((flags & HasHashFlag) != 0)
        {
            sha256 = Sha256Digest.Parse(Convert.ToHexString(hashBytes));
        }

        return new BinaryChunkFrame(
            requestId,
            instanceId,
            workerSessionId,
            streamId,
            dataOffset,
            encoded.Slice(offset, (int)dataLength).ToArray(),
            (flags & EndOfStreamFlag) != 0,
            sha256);
    }
}
