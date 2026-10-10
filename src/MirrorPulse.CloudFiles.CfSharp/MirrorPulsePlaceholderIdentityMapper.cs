using System.Security.Cryptography;
using System.Text;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Maps an Adapter instance and remote object identity to a stable CfSharp placeholder identity.
/// </summary>
public sealed record MirrorPulsePlaceholderIdentity(
    InstanceId InstanceId,
    string RemoteId,
    string? RemoteRevision)
{
    public string? RootKey { get; init; }

    public CloudPlaceholderIdentity ToCfSharp()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RemoteId);
        if (RootKey is null) return new(CreateItemId(InstanceId, RemoteId), RemoteId.Trim(), RemoteRevision?.Trim());
        ArgumentException.ThrowIfNullOrWhiteSpace(RootKey);
        string scoped = "mp2:" + InstanceId.Value.ToString("N") + ":" + EncodePart(RootKey) + ":" + EncodePart(RemoteId.Trim());
        return new(CreateItemId(InstanceId, "v2/" + scoped), scoped, RemoteRevision?.Trim());
    }

    public byte[] Encode() => ToCfSharp().Encode();

    public static MirrorPulsePlaceholderIdentity Create(InstanceId instanceId, string remoteId, string? remoteRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        return new(instanceId, remoteId.Trim(), remoteRevision?.Trim());
    }

    public static MirrorPulsePlaceholderIdentity Decode(InstanceId instanceId, ReadOnlySpan<byte> encoded)
    {
        var identity = CloudPlaceholderIdentity.Decode(encoded);
        if (TryReadScoped(instanceId, identity.RemoteId, out string? rootKey, out string? remoteId))
        {
            var scoped = Create(instanceId, remoteId!, identity.RemoteRevision) with { RootKey = rootKey };
            if (scoped.ToCfSharp().ItemId != identity.ItemId) throw new InvalidDataException("IdentityScopeMismatch");
            return scoped;
        }
        return new(instanceId, identity.RemoteId, identity.RemoteRevision);
    }

    public static MirrorPulsePlaceholderIdentity CreateForRoot(RootRegistration root, string remoteId, string? revision = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Create(root.InstanceId, remoteId, revision) with
        {
            RootKey = root.IdentityScope == RootIdentityScope.InstanceRoot ? root.UniquenessKey : null,
        };
    }

    public static bool BelongsToRoot(RootRegistration root, CloudPlaceholderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(identity);
        if (root.IdentityScope == RootIdentityScope.LegacyInstance)
            return identity.ItemId == Create(root.InstanceId, identity.RemoteId, identity.RemoteRevision).ToCfSharp().ItemId;
        if (!TryReadScoped(root.InstanceId, identity.RemoteId, out string? rootKey, out string? remoteId) || rootKey != root.UniquenessKey)
            return false;
        return identity.ItemId == CreateForRoot(root, remoteId!, identity.RemoteRevision).ToCfSharp().ItemId;
    }

    public static bool BelongsToInstance(InstanceId instance, CloudPlaceholderIdentity identity, string? rootKey = null)
    {
        if (TryReadScoped(instance, identity.RemoteId, out string? encodedRoot, out string? remoteId))
            return (rootKey is null || encodedRoot == rootKey) && identity.ItemId ==
                (Create(instance, remoteId!, identity.RemoteRevision) with { RootKey = encodedRoot }).ToCfSharp().ItemId;
        return identity.ItemId == Create(instance, identity.RemoteId, identity.RemoteRevision).ToCfSharp().ItemId;
    }

    private static string EncodePart(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryReadScoped(InstanceId instance, string value, out string? rootKey, out string? remoteId)
    {
        rootKey = null;
        remoteId = null;
        string[] parts = value.Split(':');
        if (parts.Length != 4 || parts[0] != "mp2" || parts[1] != instance.Value.ToString("N")) return false;
        try
        {
            string DecodePart(string part)
            {
                string base64 = part.Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
                return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            }
            rootKey = DecodePart(parts[2]);
            remoteId = DecodePart(parts[3]);
            return !string.IsNullOrWhiteSpace(rootKey) && !string.IsNullOrWhiteSpace(remoteId) &&
                EncodePart(rootKey) == parts[2] && EncodePart(remoteId) == parts[3];
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }

    private static Guid CreateItemId(InstanceId instanceId, string remoteId)
    {
        var input = Encoding.UTF8.GetBytes($"MirrorPulse/{instanceId}/{remoteId.Trim()}");
        var digest = SHA256.HashData(input);
        var itemId = new Guid(digest.AsSpan(0, 16));
        return itemId == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : itemId;
    }
}
