using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

internal sealed class AdapterWorkerUpdateClient(
    AdapterWorkerReadRangeClient channel, InstanceId instanceId, WorkerSessionId sessionId, InstallId installId)
{
    private readonly Dictionary<Guid, TaskCompletionSource<JsonElement>> _requests = [];
    private bool _closed;

    public InstallId InstallId { get; } = installId;
    public bool IsSupported => channel.Protocol?.Capabilities.Contains(AdapterDeploymentContract.UpdateDiscoveryCapability,
        StringComparer.Ordinal) == true;

    public async Task<JsonElement> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requests)
        {
            if (_closed) throw new AdapterWorkerOperationException("Disconnected");
            // Keep outstanding correlations so a delayed reply cannot interrupt sync on the same pipe.
            if (_requests.Count >= 8) throw new AdapterWorkerOperationException("CapabilityUnavailable");
            _requests.Add(requestId, completion);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await channel.WriteControlAsync(new(channel.ProtocolVersion, AdapterDeploymentContract.CheckUpdateMessage,
                requestId, instanceId, sessionId, false, JsonSerializer.SerializeToElement(new { })),
                deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(deadline.Token);
            throw new TimeoutException("The Adapter update check timed out.");
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch
        {
            lock (_requests) _requests.Remove(requestId);
            throw;
        }
    }

    public bool TryHandle(ControlFrameEnvelope frame)
    {
        if (frame.InstanceId != instanceId || frame.WorkerSessionId != sessionId) return false;
        TaskCompletionSource<JsonElement>? completion;
        lock (_requests)
        {
            if (!_requests.Remove(frame.RequestId, out completion)) return false;
        }

        if (frame.MessageType == AdapterDeploymentContract.UpdateOfferMessage)
            completion.TrySetResult(frame.Payload);
        else
            completion.TrySetException(new AdapterWorkerOperationException("WorkerFailure"));
        return true;
    }

    public void Close()
    {
        lock (_requests)
        {
            _closed = true;
            foreach (TaskCompletionSource<JsonElement> request in _requests.Values)
                request.TrySetException(new AdapterWorkerOperationException("Disconnected"));
            _requests.Clear();
        }
    }
}
