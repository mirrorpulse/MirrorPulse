using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Binds the retained public identity to its original backend object without selecting a new generation.</summary>
internal static class MirrorPulseKeepRemotePublicIdentity
{
    internal static async ValueTask<CloudPlaceholderIdentity> ReadOriginalAsync(MirrorPulseProductCatalog catalog,
        RootRegistration root, MirrorPulseConflictKeepRemoteIntent intent, CancellationToken token)
    {
        if (root.InstanceId != intent.InstanceId || root.UniquenessKey != intent.RemoteSource.RootKey)
            throw new InvalidDataException("KeepRemote requires its original instance and root.");
        string? encoded = intent.LocalContent?.UploadBinding.PreviousPlaceholderIdentity;
        if (encoded is null) return ReadOriginal(root, intent.ItemId, intent.RemoteSource.RemoteId, encoded);
        if (encoded.Length > 5500) throw new InvalidDataException("KeepRemote requires its bounded original public identity.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("KeepRemote requires its exact original public identity.", error); }
        if (CloudPlaceholderIdentity.TryDecode(bytes, out var identity) && identity is not null &&
            MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, intent.RemoteSource.RemoteId, identity))
            return ReadOriginal(root, intent.ItemId, intent.RemoteSource.RemoteId, encoded);
        var moved = await catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(intent.CommandId, token).ConfigureAwait(false);
        // Some backends use the path itself as their object address. Only the same
        // group's acknowledged original Move proves the old-to-new correspondence.
        // Keep the original public item/address; callbacks route through its committed
        // namespace location. A fresh path, current version or unacknowledged move
        // never supplies this relation.
        if (moved is null || intent.RemoteSource.RemoteId != moved.Intent.RelativePath)
            throw new InvalidDataException("KeepRemote requires its original public item, instance/root and backend object.");
        return ReadOriginal(root, intent.ItemId, moved.Intent.PreviousRelativePath!, encoded);
    }

    internal static async ValueTask<CloudPlaceholderIdentity> SelectRevisionAsync(MirrorPulseProductCatalog catalog,
        RootRegistration root, MirrorPulseConflictKeepRemoteIntent intent, CancellationToken token)
    {
        var original = await ReadOriginalAsync(catalog, root, intent, token).ConfigureAwait(false);
        return new(original.ItemId, original.RemoteId, intent.RemoteSource.RemoteRevision);
    }

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
