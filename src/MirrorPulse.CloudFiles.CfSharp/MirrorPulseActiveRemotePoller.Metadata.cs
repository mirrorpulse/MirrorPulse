using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseActiveRemotePoller
{
    internal const int MetadataProjectionVersion = 6;

    private async ValueTask<IReadOnlyDictionary<string, SnapshotEntry>> SelectMetadataAsync(RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> previous, IReadOnlyDictionary<string, SnapshotEntry> current, CancellationToken token)
    {
        var candidates = new List<(string Key, Guid ItemId, SnapshotEntry Prior, SnapshotEntry Current)>();
        Dictionary<string, SnapshotEntry>? selected = null;
        foreach (var (key, observed) in current)
        {
            if (!previous.TryGetValue(key, out var prior) || prior.ItemKind != observed.ItemKind || prior.Length != observed.Length ||
                prior.RemoteRevision != observed.RemoteRevision || prior.RelativePath != observed.RelativePath ||
                SameSupportedTimes(prior.Metadata, observed.Metadata) || !SamePublicIdentity(root, prior, observed)) continue;

            // Only the timestamps exposed by the directory protocol participate. An
            // omitted timestamp carries no instruction to clear the previous value.
            var metadata = MergeSupportedTimes(prior.Metadata, observed.Metadata);
            if (SameSupportedTimes(prior.Metadata, metadata))
            {
                selected ??= new(current, StringComparer.Ordinal);
                selected[key] = observed with { Metadata = prior.Metadata };
                continue;
            }
            candidates.Add((key, CapturedIdentity(root, observed).ItemId, prior, observed with { Metadata = metadata }));
        }

        if (candidates.Count > 0)
        {
            var materialized = await _readMaterializedItems!(candidates.Select(candidate => candidate.ItemId).ToArray(), token)
                .ConfigureAwait(false);
            selected ??= new(current, StringComparer.Ordinal);
            foreach (var (key, itemId, prior, observed) in candidates)
            {
                // An on-demand placeholder will take the latest Worker metadata on birth.
                // Until then keep the original comparison so a concurrent population of an
                // older page cannot permanently consume the metadata-only change.
                selected[key] = materialized.Contains(itemId) ? observed : observed with { Metadata = prior.Metadata };
            }
        }
        return selected ?? current;
    }

    private static CloudPlaceholderMetadata MergeSupportedTimes(CloudPlaceholderMetadata prior, CloudPlaceholderMetadata observed)
    {
        var builder = observed.Kind == CloudItemKind.Directory
            ? CloudPlaceholderMetadata.CreateDirectoryBuilder() : CloudPlaceholderMetadata.CreateFileBuilder();
        builder.WithAttributes(observed.Attributes);
        if ((observed.CreationTime ?? prior.CreationTime) is { } creation) builder.WithCreationTime(creation.ToUniversalTime());
        if ((observed.LastWriteTime ?? prior.LastWriteTime) is { } written) builder.WithLastWriteTime(written.ToUniversalTime());
        return builder.Build();
    }

    private static bool SameSupportedTimes(CloudPlaceholderMetadata first, CloudPlaceholderMetadata second) =>
        first.CreationTime == second.CreationTime && first.LastWriteTime == second.LastWriteTime;

    private static bool SupportedMetadataEqual(IReadOnlyDictionary<string, SnapshotEntry> previous,
        IReadOnlyDictionary<string, SnapshotEntry> current) => current.All(pair =>
            previous.TryGetValue(pair.Key, out var prior) && SameSupportedTimes(prior.Metadata, pair.Value.Metadata));

    private static byte[] ChainedFingerprint(RootRegistration root, IReadOnlyDictionary<string, SnapshotEntry> snapshot,
        int projectionVersion, byte[] initialCursor)
    {
        string previous = Convert.ToHexString(initialCursor);
        string content = Convert.ToHexString(RootFingerprint(root, snapshot, 4));
        if (projectionVersion == 5)
            return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { "MirrorPulse/PollProjection/v5", previous, content }));

        var times = snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            RemoteId = pair.Key,
            Creation = pair.Value.Metadata.CreationTime?.UtcTicks,
            LastWrite = pair.Value.Metadata.LastWriteTime?.UtcTicks,
        });
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version = "MirrorPulse/PollProjection/v6", previous, content, times }));
    }
}
