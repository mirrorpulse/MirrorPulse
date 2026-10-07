using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public interface IMirrorPulseDirectoryPageSource
{
    ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
        InstanceId instanceId, string rootKey, string normalizedPath, ReadOnlyMemory<byte> continuationCursor,
        int pageSize, CancellationToken cancellationToken) =>
        ReadPageAsync(instanceId, normalizedPath, continuationCursor, pageSize, cancellationToken);

    ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
        InstanceId instanceId,
        string normalizedPath,
        ReadOnlyMemory<byte> continuationCursor,
        int pageSize,
        CancellationToken cancellationToken);
}

/// <summary>
/// Converts one CfSharp hydration callback into reads from the Worker selected by the stable
/// placeholder identity. The returned seekable stream translates CfSharp absolute offsets into
/// bounded Worker range requests without buffering the entire remote file.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseDemandProvider : ICloudDemandProvider
{
    private const int DirectoryPageSize = 128;
    private readonly InstanceId[] _activeInstances;
    private readonly IMirrorPulseWorkerRangeTransport _transport;
    private readonly IMirrorPulseDirectoryPageSource? _directoryPages;
    private readonly MirrorPulseRootRouter? _rootRouter;

    public MirrorPulseDemandProvider(
        IEnumerable<InstanceId> activeInstances,
        IMirrorPulseWorkerRangeTransport transport,
        IMirrorPulseDirectoryPageSource? directoryPages = null)
    {
        ArgumentNullException.ThrowIfNull(activeInstances);
        ArgumentNullException.ThrowIfNull(transport);
        _activeInstances = activeInstances.Distinct().ToArray();
        _transport = transport;
        _directoryPages = directoryPages;
    }

    public MirrorPulseDemandProvider(
        MirrorPulseRootRouter rootRouter,
        IMirrorPulseWorkerRangeTransport transport,
        IMirrorPulseDirectoryPageSource? directoryPages = null)
        : this([], transport, directoryPages)
    {
        ArgumentNullException.ThrowIfNull(rootRouter);
        _rootRouter = rootRouter;
    }

    /// <summary>Connects a real CfSharp session while the current user has no active Adapters.</summary>
    public static MirrorPulseDemandProvider CreateWithoutAdapters() =>
        new([], new NoActiveAdapterRangeTransport());

    public static MirrorPulseDemandProvider CreateWithoutAdapters(string syncRootPath) =>
        new(new MirrorPulseRootRouter(syncRootPath, []), new NoActiveAdapterRangeTransport());

    public ValueTask<CloudProviderPolicyDecision> ApproveDeleteAsync(CloudProviderDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_rootRouter is null ? CloudProviderPolicyDecision.Deny :
            MirrorPulseRootNamespacePolicy.ApproveDelete(_rootRouter, request.NormalizedPath));
    }

    public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(
        CloudProviderFetchPlaceholdersRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FetchChildrenAsync(
            request.NormalizedPath,
            request.DirectoryIdentity,
            request.ContinuationToken,
            cancellationToken);
    }

    public async ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(
        string normalizedPath,
        ReadOnlyMemory<byte> directoryIdentity,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (_rootRouter?.IsSyncRoot(normalizedPath) == true)
        {
            if (continuationToken is not null)
            {
                throw new InvalidDataException("The top-level Adapter roots have no continuation page.");
            }

            return _rootRouter.CreateRootPage();
        }

        if ((normalizedPath.Length == 0 || normalizedPath is "/" or "\\")
            && directoryIdentity.IsEmpty && _activeInstances.Length == 0)
        {
            return new CloudProviderDirectoryPage([]);
        }

        if (_directoryPages is null)
        {
            throw new NotSupportedException("No Adapter directory source is connected.");
        }

        InstanceId instanceId;
        string adapterPath = normalizedPath;
        string? rootKey = null;
        if (_rootRouter is null)
        {
            CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(directoryIdentity.Span);
            instanceId = ResolveInstance(identity);
        }
        else
        {
            MirrorPulseRoutedItem routed = _rootRouter.Resolve(normalizedPath, directoryIdentity.Span);
            instanceId = routed.InstanceId;
            adapterPath = routed.RelativePath;
            rootKey = routed.RootKey;
        }

        ReadOnlyMemory<byte> cursor = DecodeContinuation(continuationToken);
        CloudRemoteDirectoryPage page = rootKey is null
            ? await _directoryPages.ReadPageAsync(instanceId, adapterPath, cursor, DirectoryPageSize, cancellationToken).ConfigureAwait(false)
            : await _directoryPages.ReadPageAsync(instanceId, rootKey, adapterPath, cursor, DirectoryPageSize, cancellationToken).ConfigureAwait(false);
        if (page is null) throw new InvalidDataException("The Adapter returned no directory page.");
        if (page.Entries.Count > DirectoryPageSize)
        {
            throw new InvalidDataException("The Adapter directory page exceeds the requested size.");
        }

        string? next = page.IsComplete ? null : Convert.ToBase64String(page.ContinuationCursor.Span);
        if (next?.Length > 4096)
        {
            throw new InvalidDataException("The Adapter directory continuation cursor is too large.");
        }
        if (next is not null && string.Equals(next, continuationToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Adapter repeated a directory continuation cursor.");
        }

        MirrorPulsePlaceholderBatchPlan plan = MirrorPulsePlaceholderBatchCoordinator.Plan(instanceId, page.Entries,
            rootKey is null ? null : _rootRouter!.GetRegistration(instanceId, rootKey));
        return new CloudProviderDirectoryPage(plan.Placeholders, next);
    }

    private static ReadOnlyMemory<byte> DecodeContinuation(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (continuationToken.Length > 4096)
        {
            throw new InvalidDataException("The directory continuation token is too large.");
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(continuationToken);
            if (decoded.Length == 0)
            {
                throw new InvalidDataException("The directory continuation cursor is empty.");
            }

            return decoded;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The directory continuation token is invalid.", exception);
        }
    }

    public ValueTask<Stream> OpenReadAsync(
        CloudFileFetchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenReadAsync(
            request.NormalizedPath,
            request.FileIdentity.ToArray(),
            request.FileSize,
            request.Offset,
            request.Length,
            cancellationToken);
    }

    public ValueTask<Stream> OpenReadAsync(
        string normalizedPath,
        ReadOnlyMemory<byte> encodedIdentity,
        long fileSize,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (offset > fileSize || length > fileSize - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The hydration range exceeds the file size.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        InstanceId instanceId;
        string adapterPath = normalizedPath;
        string? rootKey = null;
        if (_rootRouter is null)
        {
            CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(encodedIdentity.Span);
            instanceId = ResolveInstance(identity);
        }
        else
        {
            MirrorPulseRoutedItem routed = _rootRouter.Resolve(normalizedPath, encodedIdentity.Span);
            instanceId = routed.InstanceId;
            adapterPath = routed.RelativePath;
            rootKey = routed.RootKey;
        }

        Stream stream = new WorkerRangeStream(
            _transport,
            instanceId,
            adapterPath,
            encodedIdentity.ToArray(),
            fileSize,
            rootKey);
        return ValueTask.FromResult(stream);
    }

    private InstanceId ResolveInstance(CloudPlaceholderIdentity identity)
    {
        foreach (InstanceId candidate in _activeInstances)
        {
            if (MirrorPulsePlaceholderIdentity.BelongsToInstance(candidate, identity))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("The placeholder does not belong to an active Adapter instance.");
    }

    private sealed class NoActiveAdapterRangeTransport : IMirrorPulseWorkerRangeTransport
    {
        public ValueTask<Stream> ReadRangeAsync(
            MirrorPulseWorkerReadRangeRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new FileNotFoundException("No Adapter instance is active."));
    }

    private sealed class WorkerRangeStream(
        IMirrorPulseWorkerRangeTransport transport,
        InstanceId instanceId,
        string normalizedPath,
        byte[] encodedIdentity,
        long fileSize,
        string? rootKey) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => fileSize;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > fileSize)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty || _position == fileSize)
            {
                return 0;
            }

            int length = checked((int)Math.Min(
                Math.Min(buffer.Length, fileSize - _position),
                AdapterWorkerReadRangeLimit));
            var request = new MirrorPulseWorkerReadRangeRequest(
                instanceId,
                normalizedPath,
                encodedIdentity,
                _position,
                length,
                rootKey);
            await using Stream source = await transport.ReadRangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!source.CanRead)
            {
                throw new InvalidDataException("The Adapter Worker returned an unreadable range stream.");
            }

            int received = 0;
            while (received < length)
            {
                int count = await source.ReadAsync(buffer.Slice(received, length - received), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The Adapter Worker returned a truncated range.");
                }

                received += count;
            }

            _position += received;
            return received;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = position;
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const int AdapterWorkerReadRangeLimit = 1024 * 1024;
}
