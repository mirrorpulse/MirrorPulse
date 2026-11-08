using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Binds the retained public identity to its original backend object without selecting a new generation.</summary>
internal static class MirrorPulseKeepRemotePublicIdentity
{
    internal static CloudPlaceholderIdentity ReadOriginal(RootRegistration root, Guid itemId, string backendRemoteId,
        string? encodedIdentity)
    {
        ArgumentNullException.ThrowIfNull(root);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedIdentity ?? string.Empty); }
        catch (FormatException error) { throw new InvalidDataException("KeepRemote requires its exact original public identity.", error); }
        if (bytes.Length is 0 or > 4096 || Convert.ToBase64String(bytes) != encodedIdentity ||
            !CloudPlaceholderIdentity.TryDecode(bytes, out var identity) || identity is null || identity.ItemId != itemId ||
            !MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, backendRemoteId, identity))
            throw new InvalidDataException("KeepRemote requires its original public item, instance/root and backend object.");
        return identity;
    }

    internal static CloudPlaceholderIdentity SelectRevision(RootRegistration root, Guid itemId, string backendRemoteId,
        string? encodedIdentity, string selectedRevision)
    {
        var original = ReadOriginal(root, itemId, backendRemoteId, encodedIdentity);
        // The selected revision belongs to the retained public address, including its original
        // instance/root scope or generation. A backend identifier cannot replace that address.
        return new(original.ItemId, original.RemoteId, selectedRevision);
    }
}
