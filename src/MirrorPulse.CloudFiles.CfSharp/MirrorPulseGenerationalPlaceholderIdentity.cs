using System.Security.Cryptography;
using System.Text;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>An address for an explicitly selected product generation, without namespace or retirement authority.</summary>
internal sealed record MirrorPulseGenerationalPlaceholderIdentity(InstanceId InstanceId, RootId RootId,
    string RootKey, string RemoteId, Guid Generation, string? RemoteRevision)
{
    internal static CloudPlaceholderIdentity Create(RootRegistration root, string remoteId, Guid generation, string? revision = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        if (generation == Guid.Empty) throw new ArgumentException("A selected generation must be nonempty.", nameof(generation));
        string scoped = string.Join(':', "mp3", root.InstanceId.Value.ToString("N"), root.RootId.Value.ToString("N"),
            EncodePart(root.UniquenessKey), generation.ToString("N"), EncodePart(remoteId));
        return new(CreateItemId(scoped), scoped, revision);
    }

    internal static bool TryDecode(InstanceId instance, CloudPlaceholderIdentity identity,
        out MirrorPulseGenerationalPlaceholderIdentity? decoded)
    {
        ArgumentNullException.ThrowIfNull(identity);
        decoded = null;
        string[] parts = identity.RemoteId.Split(':');
        if (parts.Length != 6 || parts[0] != "mp3" || parts[1] != instance.Value.ToString("N") ||
            !TryReadGuid(parts[2], out Guid root) || !TryReadGuid(parts[4], out Guid generation)) return false;
        try
        {
            string rootKey = DecodePart(parts[3]); string remoteId = DecodePart(parts[5]);
            if (string.IsNullOrWhiteSpace(rootKey) || string.IsNullOrWhiteSpace(remoteId) ||
                EncodePart(rootKey) != parts[3] || EncodePart(remoteId) != parts[5] ||
                CreateItemId(identity.RemoteId) != identity.ItemId) return false;
            decoded = new(instance, new(root), rootKey, remoteId, generation, identity.RemoteRevision);
            return true;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return false; }
    }

    internal static bool BelongsToRoot(RootRegistration root, CloudPlaceholderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(root);
        return TryDecode(root.InstanceId, identity, out var decoded) &&
            decoded!.RootId == root.RootId && decoded.RootKey == root.UniquenessKey;
    }

    private static bool TryReadGuid(string value, out Guid result) =>
        Guid.TryParseExact(value, "N", out result) && result != Guid.Empty && value == result.ToString("N");

    private static string EncodePart(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodePart(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
    }

    private static Guid CreateItemId(string scoped)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse/Generation/v1/" + scoped));
        Guid result = new(digest.AsSpan(0, 16));
        return result == Guid.Empty ? new(digest.AsSpan(16, 16)) : result;
    }
}
