using MirrorPulse.Core.CloudFiles;

namespace MirrorPulse.Core.Conflicts;

/// <summary>Preserves a selected remote file using bounded Worker reads and an immutable source manifest.</summary>
/// <remarks>Capture and retain the source with the decision before calling PreserveAsync. This does not authorize a mutation or acknowledge local observations.</remarks>
public sealed class MirrorPulseConflictRemoteCopyStore(MirrorPulseConflictCopyStore copies,
    IMirrorPulseWorkerStatTransport stats, IMirrorPulseWorkerRangeTransport ranges,
    IMirrorPulseWorkerDirectoryPageSource directories)
{
    private readonly IMirrorPulseWorkerRangeTransport _ranges = ranges;
    public const int MaximumRangeLength = 1024 * 1024;
    public const int MaximumDirectoryPages = 256;
    private const int DirectoryPageSize = 256;

    public async Task<MirrorPulseConflictRemoteSource> CaptureSourceAsync(MirrorPulseConflictRecord conflict,
        string rootKey, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        MirrorPulseConflictRemoteSource.ValidatePath(relativePath);
        if (!conflict.IsPending || string.IsNullOrWhiteSpace(conflict.RemoteRevision))
            throw new InvalidOperationException("Remote preservation requires the pending conflict's observed version.");
        await RequireRevisionAsync(conflict, rootKey, relativePath, cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(conflict, rootKey, relativePath, cancellationToken).ConfigureAwait(false);
        if (metadata is not { IsDeleted: false, Length: >= 0 } || metadata.ItemKind != "File" ||
            metadata.RemoteRevision != conflict.RemoteRevision || string.IsNullOrWhiteSpace(metadata.RemoteId))
            throw new InvalidDataException("The selected remote version is not an unchanged file with a known length.");
        await RequireRevisionAsync(conflict, rootKey, relativePath, cancellationToken).ConfigureAwait(false);
        var source = new MirrorPulseConflictRemoteSource(1, conflict.InstanceId.ToString(), rootKey,
            relativePath, metadata.RemoteId, metadata.RemoteRevision, metadata.Length.Value);
        source.Validate(conflict);
        return source;
    }

    public Task<string> PreserveAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseConflictRemoteSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate(conflict);
        return copies.PreserveRemoteDecisionAsync(commandId, conflict, source,
            async stop =>
            {
                await RequireSourceAsync(conflict, source, stop).ConfigureAwait(false);
                return new RemoteStream(this, conflict, source);
            }, cancellationToken);
    }

    private async Task RequireRevisionAsync(MirrorPulseConflictRecord conflict, string rootKey,
        string path, CancellationToken token)
    {
        string? revision = await stats.StatAsync(new(conflict.InstanceId, path, rootKey), token).ConfigureAwait(false);
        if (revision != conflict.RemoteRevision)
            throw new InvalidDataException("The remote version changed before its conflict copy committed.");
    }

    private async Task RequireSourceAsync(MirrorPulseConflictRecord conflict, MirrorPulseConflictRemoteSource source,
        CancellationToken token)
    {
        await RequireRevisionAsync(conflict, source.RootKey, source.RelativePath, token).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(conflict, source.RootKey, source.RelativePath, token).ConfigureAwait(false);
        if (metadata is null || metadata.IsDeleted || metadata.ItemKind != "File" || metadata.RemoteId != source.RemoteId ||
            metadata.RemoteRevision != source.RemoteRevision || metadata.Length != source.Length)
            throw new InvalidDataException("The retained remote object, version or length changed.");
        await RequireRevisionAsync(conflict, source.RootKey, source.RelativePath, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseWorkerDirectoryEntry?> ReadMetadataAsync(MirrorPulseConflictRecord conflict,
        string rootKey, string path, CancellationToken token)
    {
        int separator = path.LastIndexOf('/');
        string parent = separator < 0 ? string.Empty : path[..separator];
        ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < MaximumDirectoryPages; pageIndex++)
        {
            var page = await directories.ReadDirectoryPageAsync(new(conflict.InstanceId, parent, cursor,
                DirectoryPageSize, rootKey), token).ConfigureAwait(false);
            if (page.Entries.Count > DirectoryPageSize)
                throw new InvalidDataException("The Worker exceeded the conflict metadata page bound.");
            var metadata = page.Entries.SingleOrDefault(entry => entry.RelativePath == path);
            if (metadata is not null) return metadata;
            if (page.IsComplete) return null;
            cursor = page.ContinuationCursor;
            if (cursor.IsEmpty || cursor.Length > 4096 || !seen.Add(Convert.ToHexString(cursor.Span)))
                throw new InvalidDataException("The conflict metadata cursor did not advance.");
        }
        throw new InvalidDataException("The conflict metadata search exceeded its page budget.");
    }

    private sealed class RemoteStream(MirrorPulseConflictRemoteCopyStore owner,
        MirrorPulseConflictRecord conflict, MirrorPulseConflictRemoteSource source) : Stream
    {
        private byte[] _range = [];
        private int _rangePosition;
        private long _position;
        private bool _verifiedEnd;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (_position == source.Length)
            {
                if (!_verifiedEnd)
                {
                    // EOF is the copy store's commit boundary, including empty files.
                    await owner.RequireSourceAsync(conflict, source, cancellationToken).ConfigureAwait(false);
                    _verifiedEnd = true;
                }
                return 0;
            }
            if (_rangePosition == _range.Length)
            {
                await owner.RequireRevisionAsync(conflict, source.RootKey, source.RelativePath, cancellationToken).ConfigureAwait(false);
                int length = (int)Math.Min(MaximumRangeLength, source.Length - _position);
                byte[] next = new byte[length];
                await using var remote = await owner._ranges.ReadRangeAsync(new(conflict.InstanceId, source.RelativePath,
                    ReadOnlyMemory<byte>.Empty, _position, length, source.RootKey), cancellationToken).ConfigureAwait(false);
                await remote.ReadExactlyAsync(next, cancellationToken).ConfigureAwait(false);
                if (await remote.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("The Worker exceeded the requested conflict content range.");
                await owner.RequireRevisionAsync(conflict, source.RootKey, source.RelativePath, cancellationToken).ConfigureAwait(false);
                _range = next;
                _rangePosition = 0;
            }
            int count = Math.Min(buffer.Length, _range.Length - _rangePosition);
            _range.AsMemory(_rangePosition, count).CopyTo(buffer);
            _position += count;
            _rangePosition += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous Worker reads.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }
}
