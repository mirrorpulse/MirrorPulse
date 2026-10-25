using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

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
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly MirrorPulseManagedRootRenameApproval? _rootRenameApproval;
    internal MirrorPulseNamespaceDirectoryPublisher? NamespacePublisher { get; set; }
    // Observe the actual boundary before the SDK maps an exception to a native
    // callback status. Tests use only synthetic fixture data; production has no observer.
    internal Action<Exception>? NamespacePublicationFailureObservation { get; init; }

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
        IMirrorPulseDirectoryPageSource? directoryPages = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        MirrorPulseProductCatalog? catalog = null)
        : this([], transport, directoryPages)
    {
        ArgumentNullException.ThrowIfNull(rootRouter);
        _rootRouter = rootRouter;
        _namespaceWork = namespaceWork;
        _rootRenameApproval = catalog is null ? null : new(rootRouter, catalog);
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
        return ValueTask.FromResult(_rootRouter is null ? CloudProviderPolicyDecision.Allow :
            MirrorPulseRootNamespacePolicy.ApproveDelete(_rootRouter, request.NormalizedPath));
    }

    public ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _rootRenameApproval is null ? ValueTask.FromResult(CloudProviderPolicyDecision.Deny)
            : _rootRenameApproval.EvaluateAsync(request.NormalizedPath, request.FileIdentity, request.TargetPath,
                request.IsDirectory, request.SourceInScope, request.TargetInScope, cancellationToken);
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

            // Protected roots are materialized by controlled startup/recovery with
            // durable birth provenance. Returning specifications here would let the
            // library create them outside that boundary. Existing local roots remain
            // visible to the filesystem when this callback completes with no new items.
            return _namespaceWork is null ? _rootRouter.CreateRootPage() : new CloudProviderDirectoryPage([]);
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
        using var admission = _namespaceWork?.Enter([_rootRouter!.GetRegistration(instanceId, rootKey!).RootId]);
        if (_rootRouter is not null)
            await _rootRouter.RequireCurrentCallbackAsync(new(instanceId, rootKey!, adapterPath), directoryIdentity, cancellationToken).ConfigureAwait(false);
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

        MirrorPulsePlaceholderBatchPlan plan = rootKey is null
            ? MirrorPulsePlaceholderBatchCoordinator.Plan(instanceId, page.Entries)
            : await MirrorPulsePlaceholderBatchCoordinator.PlanAsync(_rootRouter!, instanceId, rootKey, page.Entries, cancellationToken).ConfigureAwait(false);
        var result = new CloudProviderDirectoryPage(plan.Placeholders, next);
        if (NamespacePublisher is { } publisher)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected source population requires Windows 11 24H2 or later.");
            try { return await publisher.PublishAsync(normalizedPath, directoryIdentity, result, cancellationToken).ConfigureAwait(false); }
            catch (Exception error)
            {
                NamespacePublicationFailureObservation?.Invoke(error);
                throw;
            }
        }
        return result;
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

    public async ValueTask<Stream> OpenReadAsync(
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

        byte[] retainedIdentity = encodedIdentity.ToArray();
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
        try
        {
            admission = _namespaceWork?.Enter([_rootRouter!.GetRegistration(instanceId, rootKey!).RootId]);
            if (_rootRouter is not null)
                await _rootRouter.RequireCurrentCallbackAsync(new(instanceId, rootKey!, adapterPath), retainedIdentity, cancellationToken).ConfigureAwait(false);
            Stream stream = new WorkerRangeStream(_transport, instanceId, adapterPath, retainedIdentity, fileSize, rootKey, admission);
            return stream;
        }
        catch { admission?.Dispose(); throw; }
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
        string? rootKey,
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission) : Stream
    {
        private long _position;
        private readonly object _lifetime = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        private bool _closing;

        public override bool CanRead => !Volatile.Read(ref _closing);

        public override bool CanSeek => !Volatile.Read(ref _closing);

        public override bool CanWrite => false;

        public override long Length => fileSize;

        public override long Position
        {
            get => _position;
            set
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing), this);
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
            lock (_lifetime) { ObjectDisposedException.ThrowIf(_closing, this); _reads++; }
            try { return await ReadCoreAsync(buffer, cancellationToken).ConfigureAwait(false); }
            finally
            {
                lock (_lifetime)
                    if (--_reads == 0 && _closing) ReleaseAdmission();
            }
        }

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lifetime)
                {
                    _closing = true;
                    if (_reads == 0) ReleaseAdmission();
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            await _drained.Task.ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void ReleaseAdmission()
        {
            admission?.Dispose(); _drained.TrySetResult();
        }
    }

    private const int AdapterWorkerReadRangeLimit = 1024 * 1024;
}
