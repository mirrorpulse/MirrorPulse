using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseActiveRemotePoller
{
    private async ValueTask<IReadOnlyDictionary<string, SnapshotEntry>> CaptureIdentitiesAsync(RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> entries, CancellationToken token)
    {
        var captured = new Dictionary<string, SnapshotEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var (key, entry) in entries)
        {
            var identity = await _identityResolver!.ResolveAsync(root, entry.RemoteId, entry.RemoteRevision, token).ConfigureAwait(false);
            captured.Add(key, entry with { PublicIdentity = new(identity.ItemId, identity.RemoteId) });
        }
        return new ReadOnlyDictionary<string, SnapshotEntry>(captured);
    }

    private static CloudPlaceholderIdentity CapturedIdentity(RootRegistration root, SnapshotEntry entry)
    {
        var original = MirrorPulsePlaceholderIdentity.CreateForRoot(root, entry.RemoteId, entry.RemoteRevision).ToCfSharp();
        if (entry.PublicIdentity is null) return original; // An actual pre-upgrade snapshot denotes its original address.
        var identity = new CloudPlaceholderIdentity(entry.PublicIdentity.ItemId, entry.PublicIdentity.RemoteId, entry.RemoteRevision);
        if (identity.ItemId == original.ItemId && identity.RemoteId == original.RemoteId) return identity;
        if (!MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, identity, out var decoded) ||
            decoded!.RootId != root.RootId || decoded.RootKey != root.UniquenessKey || decoded.RemoteId != entry.RemoteId)
            throw new InvalidDataException("The captured public address does not identify this backend object and stable root.");
        return identity;
    }

    private static bool SamePublicIdentity(RootRegistration root, SnapshotEntry left, SnapshotEntry right)
    {
        var first = CapturedIdentity(root, left); var second = CapturedIdentity(root, right);
        return first.ItemId == second.ItemId && first.RemoteId == second.RemoteId;
    }

    private static void ValidateCapturedSnapshots(RootRegistration root, IReadOnlyDictionary<string, SnapshotEntry> previous,
        IReadOnlyDictionary<string, SnapshotEntry> current)
    {
        foreach (var entry in previous.Values) _ = CapturedIdentity(root, entry);
        foreach (var entry in current.Values)
        {
            if (entry.PublicIdentity is null) throw new InvalidDataException("A new poll projection requires every selected public address.");
            _ = CapturedIdentity(root, entry);
        }
    }

    private static byte[] GenerationFingerprint(RootRegistration root, IReadOnlyDictionary<string, SnapshotEntry> snapshot)
    {
        // Base addresses add no new cursor material: upgrading an unchanged v3 snapshot must
        // neither advance nor invalidate its durable SDK cursor. Only actual successors extend it.
        byte[] original = CanonicalFingerprint(snapshot);
        var successors = new List<string[]>();
        foreach (var (key, entry) in snapshot.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var captured = CapturedIdentity(root, entry);
            var legacy = MirrorPulsePlaceholderIdentity.CreateForRoot(root, entry.RemoteId, entry.RemoteRevision).ToCfSharp();
            if (captured.ItemId != legacy.ItemId || captured.RemoteId != legacy.RemoteId)
                successors.Add([key, captured.ItemId.ToString("N"), captured.RemoteId]);
        }
        return successors.Count == 0 ? original : SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Version = "MirrorPulse/PollProjection/v4", Base = Convert.ToHexString(original), Successors = successors }));
    }
}
