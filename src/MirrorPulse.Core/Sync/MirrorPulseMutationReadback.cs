using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

public enum MirrorPulseMutationProofKind { Unknown, Verified, Conflict }
public sealed record MirrorPulseMutationProof(MirrorPulseMutationProofKind Kind, string? Revision);

/// <summary>Checks remote postconditions without issuing any remote mutation.</summary>
public sealed class MirrorPulseMutationReadback(IMirrorPulseWorkerStatTransport stats,
    IMirrorPulseWorkerRangeTransport? ranges = null, IMirrorPulseWorkerDirectoryPageSource? directories = null)
{
    public async ValueTask<MirrorPulseMutationProof> VerifyAsync(MirrorPulseMutationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        MirrorPulseMutationIntent intent = record.Intent;
        string? revision = await stats.StatAsync(new(intent.InstanceId, intent.RelativePath), cancellationToken).ConfigureAwait(false);
        if (intent.Kind == MirrorPulseWorkerChangeKind.Delete)
            return revision is null ? new(MirrorPulseMutationProofKind.Verified, null) :
                new(revision == intent.ExpectedRevision ? MirrorPulseMutationProofKind.Unknown : MirrorPulseMutationProofKind.Conflict, revision);
        if (intent.Kind == MirrorPulseWorkerChangeKind.Move)
        {
            string? source = await stats.StatAsync(new(intent.InstanceId, intent.PreviousRelativePath!), cancellationToken).ConfigureAwait(false);
            if (record.AcceptedRevision is not null && revision == record.AcceptedRevision && source is null)
                return new(MirrorPulseMutationProofKind.Verified, revision);
            return new(revision is not null && revision != record.AcceptedRevision
                ? MirrorPulseMutationProofKind.Conflict : MirrorPulseMutationProofKind.Unknown, revision);
        }
        if (ranges is null || directories is null || revision is null || intent.ContentSha256 is null || intent.ContentLength is null)
            return new(MirrorPulseMutationProofKind.Unknown, revision);
        MirrorPulseWorkerDirectoryEntry? metadata = await ReadMetadataAsync(intent, cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.IsDeleted || metadata.ItemKind != "file" || metadata.RemoteRevision != revision)
            return new(MirrorPulseMutationProofKind.Unknown, revision);
        bool matches = metadata.Length == intent.ContentLength;
        if (matches)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < intent.ContentLength)
            {
                int length = (int)Math.Min(1024 * 1024, intent.ContentLength.Value - offset);
                await using Stream content = await ranges.ReadRangeAsync(new(intent.InstanceId, intent.RelativePath,
                    ReadOnlyMemory<byte>.Empty, offset, length), cancellationToken).ConfigureAwait(false);
                byte[] buffer = new byte[length];
                await content.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (content.ReadByte() != -1) throw new InvalidDataException("The readback range exceeded its bound.");
                hash.AppendData(buffer);
                offset += length;
            }
            matches = string.Equals(Convert.ToHexString(hash.GetHashAndReset()), intent.ContentSha256, StringComparison.OrdinalIgnoreCase);
        }
        string? after = await stats.StatAsync(new(intent.InstanceId, intent.RelativePath), cancellationToken).ConfigureAwait(false);
        if (after != revision) return new(MirrorPulseMutationProofKind.Unknown, after);
        return new(matches ? MirrorPulseMutationProofKind.Verified :
            revision != intent.ExpectedRevision || record.State == MirrorPulseMutationState.RemoteAccepted
                ? MirrorPulseMutationProofKind.Conflict : MirrorPulseMutationProofKind.Unknown, revision);
    }

    public async ValueTask<MirrorPulseWorkerDirectoryEntry?> ReadMetadataAsync(MirrorPulseMutationIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (directories is null) throw new NotSupportedException("The Worker must provide remote metadata for content confirmation.");
        int separator = intent.RelativePath.LastIndexOf('/');
        string parent = separator < 0 ? string.Empty : intent.RelativePath[..separator];
        ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < 10000; pageIndex++)
        {
            MirrorPulseWorkerDirectoryPage page = await directories!.ReadDirectoryPageAsync(new(intent.InstanceId, parent, cursor, 256),
                cancellationToken).ConfigureAwait(false);
            MirrorPulseWorkerDirectoryEntry? found = page.Entries.SingleOrDefault(entry =>
                string.Equals(entry.RelativePath, intent.RelativePath, StringComparison.Ordinal));
            if (found is not null) return found;
            if (page.IsComplete) return null;
            cursor = page.ContinuationCursor;
            if (cursor.IsEmpty || !seen.Add(Convert.ToHexString(SHA256.HashData(cursor.Span))))
                throw new InvalidDataException("The readback directory cursor did not advance.");
        }
        throw new InvalidDataException("The readback directory exceeded its page limit.");
    }
}
