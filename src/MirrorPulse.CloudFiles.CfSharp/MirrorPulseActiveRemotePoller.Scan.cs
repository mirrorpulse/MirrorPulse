using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseActiveRemotePoller
{
    private async Task<IReadOnlyDictionary<string, SnapshotEntry>> ReadVerifiedSnapshotAsync(
        RootRegistration root, IReadOnlyDictionary<string, SnapshotEntry>? previous, CancellationToken token)
    {
        var (entries, paged) = await ReadSnapshotAsync(root, token).ConfigureAwait(false);
        // Existing Workers use positional cursors. An insertion or removal before an offset
        // can yield a complete-looking listing with a missing live item. Independently reread
        // paged scans and proposed deletions before using absence as evidence.
        if (previous is not null && ObservationsEqual(previous, entries)) return entries;
        if (!paged && previous?.Keys.Any(id => !entries.ContainsKey(id)) != true) return entries;

        var (confirmation, _) = await ReadSnapshotAsync(root, token).ConfigureAwait(false);
        if (!ObservationsEqual(entries, confirmation))
        {
            throw new IOException("The remote directory changed during enumeration. Retry from the first page.");
        }

        return confirmation;
    }

    private static bool ObservationsEqual(IReadOnlyDictionary<string, SnapshotEntry> first,
        IReadOnlyDictionary<string, SnapshotEntry> second) => first.Count == second.Count && first.All(pair =>
        second.TryGetValue(pair.Key, out var verified) &&
        pair.Value.RemoteRevision == verified.RemoteRevision && pair.Value.ItemKind == verified.ItemKind &&
        pair.Value.RelativePath == verified.RelativePath && pair.Value.Length == verified.Length &&
        ToSnapshotMetadata(pair.Value.Metadata) == ToSnapshotMetadata(verified.Metadata));

    private async Task<(Dictionary<string, SnapshotEntry> Entries, bool Paged)> ReadSnapshotAsync(
        RootRegistration root, CancellationToken token)
    {
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Queue<string>([string.Empty]);
        var pages = 0;
        var paged = false;
        while (directories.TryDequeue(out string? directory))
        {
            ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (++pages > _maximumPages)
                    throw new InvalidDataException("The active remote poll exceeded its page limit.");
                CloudRemoteDirectoryPage page = await _source.ReadPageAsync(
                    root.InstanceId, root.UniquenessKey, directory, cursor, _pageSize, token).ConfigureAwait(false);
                if (page.Entries.Count > _pageSize)
                    throw new InvalidDataException("The remote poll returned more entries than requested.");

                foreach (CloudRemoteDirectoryEntry entry in page.Entries)
                {
                    string path = NormalizeRemotePath(entry.RelativePath);
                    int separator = path.LastIndexOf('/');
                    var parent = separator < 0 ? ReadOnlySpan<char>.Empty : path.AsSpan(0, separator);
                    if (!parent.Equals(directory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The remote poll returned an entry outside the requested directory.");
                    if (!seenIds.Add(entry.RemoteId))
                        throw new InvalidDataException("The remote poll returned duplicate object identifiers.");
                    if (entry.IsDeleted) continue;
                    if (!livePaths.Add(path))
                        throw new InvalidDataException("The remote poll returned several live objects at the same path.");

                    CloudPlaceholderMetadata metadata = entry.Metadata ?? (entry.ItemKind == CloudItemKind.Directory
                        ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()
                        : CloudPlaceholderMetadata.CreateFileBuilder().Build());
                    entries.Add(entry.RemoteId, new(entry.RemoteId, entry.RemoteRevision, entry.ItemKind, path, entry.Length, metadata));
                    if (entry.ItemKind == CloudItemKind.Directory) directories.Enqueue(path);
                }

                if (page.IsComplete) break;
                paged = true;
                if (page.ContinuationCursor.IsEmpty || page.ContinuationCursor.Length > 4096 ||
                    !seenCursors.Add(Convert.ToBase64String(page.ContinuationCursor.Span)))
                {
                    throw new InvalidDataException("The remote poll returned an invalid continuation cursor.");
                }

                cursor = page.ContinuationCursor;
            }
        }

        return (entries, paged);
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("The remote poll returned an invalid path.");
        string normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The remote poll returned an unsafe path.");
        return normalized;
    }
}
