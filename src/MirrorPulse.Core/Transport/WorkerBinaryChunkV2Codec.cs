using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MirrorPulse.Core.Transport;

/// <summary>Host binding of the language-neutral MPB2 binary format.</summary>
public static class WorkerBinaryChunkV2Codec
{
    private const int MaximumChunkBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(BinaryChunkFrame chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (string.IsNullOrWhiteSpace(chunk.RootKey) || chunk.RootKey.Length > 256 || chunk.RootKey.Any(char.IsControl) ||
            chunk.Data.Length > MaximumChunkBytes || chunk.Sha256 is null)
            throw new InvalidDataException("InvalidV2Chunk");
        byte[] root = StrictUtf8.GetBytes(chunk.RootKey);
        byte[] legacy = BinaryChunkCodec.Encode(chunk);
        byte[] bytes = new byte[6 + root.Length + legacy.Length];
        "MPB2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), checked((ushort)root.Length));
        root.CopyTo(bytes, 6);
        legacy.CopyTo(bytes, 6 + root.Length);
        return bytes;
    }

    public static BinaryChunkFrame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || !bytes[..4].SequenceEqual("MPB2"u8)) throw new InvalidDataException("InvalidV2Chunk");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
        if (count is < 1 or > 1024 || bytes.Length < 6 + count || bytes.Length > 6 + 1024 + 109 + MaximumChunkBytes)
            throw new InvalidDataException("InvalidV2ChunkLength");
        string root;
        try { root = StrictUtf8.GetString(bytes.Slice(6, count)); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("InvalidRootEncoding", exception); }
        BinaryChunkFrame chunk = BinaryChunkCodec.Decode(bytes[(6 + count)..]);
        if (string.IsNullOrWhiteSpace(root) || root.Length > 256 || root.Any(char.IsControl) ||
            chunk.Data.Length > MaximumChunkBytes || chunk.Sha256 is not { } hash ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash.Hexadecimal), SHA256.HashData(chunk.Data.Span)))
            throw new InvalidDataException("InvalidV2Chunk");
        return new(chunk.RequestId, chunk.InstanceId, chunk.WorkerSessionId, chunk.StreamId,
            chunk.Offset, chunk.Data, chunk.EndOfStream, chunk.Sha256)
        { RootKey = root };
    }
}
