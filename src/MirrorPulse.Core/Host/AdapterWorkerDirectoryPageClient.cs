using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Host;

/// <summary>Correlates one remote-directory page request with a connected Worker session.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "Close faults in-flight directory requests; releasing the semaphore after its continuation is unsafe.")]
public sealed class AdapterWorkerDirectoryPageClient
{
    private const int MaximumPageSize = 512;
    private readonly AdapterWorkerReadRangeClient _channel;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _pendingLock = new();
    private TaskCompletionSource<MirrorPulseWorkerDirectoryPage>? _pending;
    private Guid _requestId;
    private bool _closed;

    public AdapterWorkerDirectoryPageClient(
        AdapterWorkerReadRangeClient channel,
        InstanceId instanceId,
        WorkerSessionId sessionId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _instanceId = instanceId;
        _sessionId = sessionId;
    }

    public async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(
        MirrorPulseWorkerDirectoryPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.NormalizedPath);
        if (request.InstanceId != _instanceId || request.PageSize is < 1 or > MaximumPageSize ||
            request.ContinuationCursor.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                "The Worker directory request is invalid.");
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<MirrorPulseWorkerDirectoryPage> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _pending = completion;
                _requestId = requestId;
            }

            await _channel.WriteControlAsync(new ControlFrameEnvelope(1, "List", requestId,
                _instanceId, _sessionId, false,
                JsonSerializer.SerializeToElement(new
                {
                    path = request.NormalizedPath,
                    cursor = request.ContinuationCursor.IsEmpty
                        ? null
                        : Convert.ToBase64String(request.ContinuationCursor.Span),
                    pageSize = request.PageSize,
                })), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, completion))
                {
                    _pending = null;
                    if (!completion.Task.IsCompleted)
                    {
                        _closed = true;
                    }
                }
            }

            _operation.Release();
        }
    }

    public bool CanHandle(ControlFrameEnvelope frame)
    {
        lock (_pendingLock)
        {
            return !_closed && _pending is not null && frame.RequestId == _requestId &&
                frame.InstanceId == _instanceId && frame.WorkerSessionId == _sessionId;
        }
    }

    /// <summary>Called only by the session's single pipe reader.</summary>
    public ValueTask HandleResponseAsync(ControlFrameEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TaskCompletionSource<MirrorPulseWorkerDirectoryPage> completion;
        lock (_pendingLock)
        {
            if (_closed || _pending is null || frame.RequestId != _requestId ||
                frame.InstanceId != _instanceId || frame.WorkerSessionId != _sessionId ||
                !frame.IsResponse || frame.ProtocolVersion != _channel.ProtocolVersion)
            {
                throw new InvalidDataException("The Worker response has no matching directory request.");
            }

            completion = _pending;
        }

        try
        {
            if (frame.MessageType == "OperationError")
            {
                string code = frame.Payload.GetProperty("code").GetString() ?? "Unknown";
                completion.TrySetException(new IOException($"The Adapter directory read failed: {code}."));
                return ValueTask.CompletedTask;
            }

            if (frame.MessageType != "DirectoryPage")
            {
                throw new InvalidDataException("The Worker returned an unexpected directory response.");
            }

            bool isComplete = frame.Payload.GetProperty("isComplete").GetBoolean();
            string? cursorText = frame.Payload.TryGetProperty("cursor", out JsonElement cursor)
                && cursor.ValueKind is not JsonValueKind.Null
                ? cursor.GetString()
                : null;
            byte[] cursorBytes = string.IsNullOrEmpty(cursorText)
                ? []
                : Convert.FromBase64String(cursorText);
            if (cursorBytes.Length > 4096 || (!isComplete && cursorBytes.Length == 0))
            {
                throw new InvalidDataException("The Worker returned an invalid directory cursor.");
            }

            JsonElement entriesElement = frame.Payload.GetProperty("entries");
            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The Worker directory entries are not an array.");
            }

            var entries = new List<MirrorPulseWorkerDirectoryEntry>();
            foreach (JsonElement entry in entriesElement.EnumerateArray())
            {
                string remoteId = RequiredString(entry, "remoteId");
                string remoteRevision = RequiredString(entry, "remoteRevision");
                string itemKind = RequiredString(entry, "itemKind");
                string relativePath = RequiredString(entry, "relativePath");
                long? length = entry.TryGetProperty("length", out JsonElement lengthElement) &&
                    lengthElement.ValueKind is not JsonValueKind.Null
                    ? lengthElement.GetInt64()
                    : null;
                if (length is < 0)
                {
                    throw new InvalidDataException("The Worker directory entry length is negative.");
                }

                DateTimeOffset? creationTime = ReadTimestamp(entry, "creationTime");
                DateTimeOffset? lastWriteTime = ReadTimestamp(entry, "lastWriteTime");
                bool isDeleted = entry.TryGetProperty("isDeleted", out JsonElement deleted) &&
                    deleted.GetBoolean();
                entries.Add(new(remoteId, remoteRevision, itemKind, relativePath, length,
                    creationTime, lastWriteTime, isDeleted));
            }

            completion.TrySetResult(new(entries.AsReadOnly(), cursorBytes, isComplete));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return ValueTask.CompletedTask;
    }

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _pending?.TrySetException(new IOException("The Adapter Worker disconnected."));
        }
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        string value = parent.GetProperty(property).GetString()
            ?? throw new InvalidDataException($"The Worker directory entry {property} is missing.");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The Worker directory entry {property} is empty.");
        }

        return value;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement element) ||
            element.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (!element.TryGetDateTimeOffset(out DateTimeOffset value))
        {
            throw new InvalidDataException($"The Worker directory entry {property} is invalid.");
        }

        return value.ToUniversalTime();
    }
}
