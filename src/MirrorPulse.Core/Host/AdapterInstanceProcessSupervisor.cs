using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Host;

/// <summary>Owns one isolated process and current-user control pipe per enabled Adapter instance.</summary>
public sealed class AdapterInstanceProcessSupervisor :
    IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerUploadTransport,
    IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource,
    IMirrorPulseWorkerMutationTransport, IAsyncDisposable
{
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly ISecureCredentialStore _credentials;
    private readonly Func<InstanceId, JsonElement, CancellationToken, ValueTask>? _remoteBatch;
    private readonly LocalRollingLogWriter? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<InstanceId, AdapterWorkerReadRangeClient> _connected = new();
    private readonly ConcurrentDictionary<InstanceId, AdapterWorkerUploadClient> _uploads = new();
    private readonly ConcurrentDictionary<InstanceId, AdapterWorkerStatClient> _stats = new();
    private readonly ConcurrentDictionary<InstanceId, AdapterWorkerDirectoryPageClient> _directories = new();
    private readonly ConcurrentDictionary<InstanceId, AdapterWorkerMutationClient> _mutations = new();
    private readonly ConcurrentDictionary<InstanceId, SemaphoreSlim> _instanceOperations = new();
    private Task[] _workers = [];
    private bool _started;

    public AdapterInstanceProcessSupervisor(
        MirrorPulseProductCatalog catalog,
        ISecureCredentialStore credentials,
        Func<InstanceId, JsonElement, CancellationToken, ValueTask>? remoteBatch = null,
        string? diagnosticsDirectory = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _remoteBatch = remoteBatch;
        _log = diagnosticsDirectory is null ? null : new(diagnosticsDirectory);
    }

    public async Task StartAsync(MirrorPulseAdapterTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (_started)
        {
            throw new InvalidOperationException("The Adapter process supervisor is already running.");
        }

        _started = true;
        foreach (AdapterInstance instance in topology.Instances.Where(item => !item.Enabled))
        {
            await SetPhaseAsync(instance.InstanceId, "Offline", _shutdown.Token).ConfigureAwait(false);
        }

        _workers = topology.Instances.Where(item => item.Enabled)
            .Select(instance => RunInstanceAsync(topology, instance, _shutdown.Token)).ToArray();
    }

    public ValueTask<Stream> ReadRangeAsync(
        MirrorPulseWorkerReadRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadRangeCoreAsync(request, cancellationToken);
    }

    public ValueTask<string> UploadAsync(
        MirrorPulseWorkerUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UploadCoreAsync(request, cancellationToken);
    }

    public ValueTask<string?> StatAsync(
        MirrorPulseWorkerStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StatCoreAsync(request, cancellationToken);
    }

    public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(
        MirrorPulseWorkerDirectoryPageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadDirectoryPageCoreAsync(request, cancellationToken);
    }

    public ValueTask<string?> DeleteAsync(
        MirrorPulseWorkerDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateDeleteCoreAsync(request, cancellationToken);
    }

    public ValueTask<string> MoveAsync(
        MirrorPulseWorkerMoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateMoveCoreAsync(request, cancellationToken);
    }

    public async ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_mutations.TryGetValue(request.InstanceId, out AdapterWorkerMutationClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
            throw new AdapterWorkerOperationException("Disconnected");
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await client.CreateDirectoryAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { operation.Release(); }
    }

    private async ValueTask<Stream> ReadRangeCoreAsync(
        MirrorPulseWorkerReadRangeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_connected.TryGetValue(request.InstanceId, out AdapterWorkerReadRangeClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.ReadRangeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async ValueTask<string> UploadCoreAsync(
        MirrorPulseWorkerUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_uploads.TryGetValue(request.InstanceId, out AdapterWorkerUploadClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.UploadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async ValueTask<string?> StatCoreAsync(
        MirrorPulseWorkerStatRequest request,
        CancellationToken cancellationToken)
    {
        if (!_stats.TryGetValue(request.InstanceId, out AdapterWorkerStatClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.StatAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageCoreAsync(
        MirrorPulseWorkerDirectoryPageRequest request,
        CancellationToken cancellationToken)
    {
        if (!_directories.TryGetValue(request.InstanceId, out AdapterWorkerDirectoryPageClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.ReadDirectoryPageAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async ValueTask<string?> MutateDeleteCoreAsync(
        MirrorPulseWorkerDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!_mutations.TryGetValue(request.InstanceId, out AdapterWorkerMutationClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.DeleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async ValueTask<string> MutateMoveCoreAsync(
        MirrorPulseWorkerMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (!_mutations.TryGetValue(request.InstanceId, out AdapterWorkerMutationClient? client) ||
            !_instanceOperations.TryGetValue(request.InstanceId, out SemaphoreSlim? operation))
        {
            throw new AdapterWorkerOperationException("Offline");
        }

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.MoveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task RunInstanceAsync(
        MirrorPulseAdapterTopology topology,
        AdapterInstance instance,
        CancellationToken cancellationToken)
    {
        var observation = new WorkerSessionFailureObservation();
        WorkerSessionId sessionId = WorkerSessionId.New();
        LogField[]? failureObservation = null;
        try
        {
            await SetPhaseAsync(instance.InstanceId, "Starting", cancellationToken).ConfigureAwait(false);
            string runtimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "win-x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException("The Adapter process architecture is unsupported."),
            };
            string pipeName = WorkerLaunchNonce.CreatePipeName();
            string[] arguments = ["--instance-id", instance.InstanceId.ToString(),
                "--worker-session-id", sessionId.ToString(), "--pipe-name", pipeName];
            AdapterInstanceWorkerPayload payload = AdapterInstanceWorkerLaunchResolver.Resolve(
                topology, instance.InstanceId, sessionId, runtimeIdentifier, arguments);
            WorkerLaunchRequest request = payload.LaunchRequest;
            request = new WorkerLaunchRequest(request.InstanceId, request.WorkerSessionId,
                request.ExecutablePath, request.WorkingDirectory, request.Arguments,
                new Dictionary<string, string> { ["MP_TRANSFER_CACHE_DIR"] = instance.TransferCacheDirectory });
            Directory.CreateDirectory(instance.TransferCacheDirectory);
            int selectedVersion = 1;
            observation.Advance(WorkerSessionStage.CreatePipe);
            await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName)
            {
                PipeOptions = PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            });
            observation.Advance(WorkerSessionStage.CreateJob);
            using var job = WorkerJobObject.Create();
            observation.Advance(WorkerSessionStage.StartProcess);
            using WorkerProcessHandle worker = WorkerProcessLauncher.Start(request);
            bool jobAttached = false;
            try
            {
                observation.Advance(WorkerSessionStage.AttachJob);
                job.Attach(worker.Process);
                jobAttached = true;
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                observation.Advance(WorkerSessionStage.AwaitPipe);
                try
                {
                    await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connectionTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    observation.DeadlineExpired = true;
                    throw;
                }
                observation.Advance(WorkerSessionStage.ValidatePeer);
                if (worker.Process.HasExited)
                {
                    throw new UnauthorizedAccessException("The launched Worker exited before its pipe handshake.");
                }
                NamedPipePeerIdentity.ValidateClient(pipe, worker.ProcessId, worker.Process.SessionId);
                await ServeWorkerAsync(pipe, topology, instance, sessionId, version => selectedVersion = version,
                    observation, () => observation.Capture(sessionId, worker.Process, pipe),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                failureObservation = observation.Capture(sessionId, worker.Process, pipe);
                throw;
            }
            finally
            {
                if (failureObservation is null)
                {
                    observation.Advance(WorkerSessionStage.StopWorker);
                    failureObservation = observation.Capture(sessionId, worker.Process, pipe);
                }
                try
                {
                    if (pipe.IsConnected && !worker.Process.HasExited)
                    {
                        try
                        {
                            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await WriteAsync(pipe, new ControlFrameEnvelope(selectedVersion, "Stop", Guid.NewGuid(),
                                instance.InstanceId, sessionId, false, JsonSerializer.SerializeToElement(new { })),
                                stopTimeout.Token).ConfigureAwait(false);
                            await worker.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or OperationCanceledException or
                            EndOfStreamException)
                        {
                            // The existing grace period ended; drain forced termination below.
                        }
                    }
                }
                finally
                {
                    // Closing a kill-on-close Job Object only requests termination. Retain the
                    // process reference until exit so private runtime mappings and file locks
                    // cannot outlive supervisor disposal or race package/cache reclamation.
                    if (jobAttached) job.Terminate();
                    else if (!worker.Process.HasExited)
                    {
                        try { worker.Process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (worker.Process.HasExited) { }
                    }
                    // WaitForExitAsync may short-circuit through HasExited/GetExitCodeProcess
                    // before Windows signals completed termination. The mature synchronous
                    // wait uses that signal; await it without blocking the Host's control path.
                    await Task.Run(worker.Process.WaitForExit, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await LogSessionFailureAsync(instance.InstanceId, exception,
                failureObservation ?? observation.Capture(sessionId)).ConfigureAwait(false);
            await SetPhaseAsync(instance.InstanceId, "Worker failed", CancellationToken.None,
                exception.GetType().Name).ConfigureAwait(false);
        }
    }

    private async Task LogSessionFailureAsync(InstanceId instanceId, Exception exception, LogField[] observation)
    {
        if (_log is null) return;
        try
        {
            Exception cause = exception.GetBaseException();
            LogField[] nativeFailure = cause is System.ComponentModel.Win32Exception native
                ? [new("workerNativeErrorCode", native.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture))] : [];
            await _log.WriteAsync(new(LogLevel.Warning, "worker", "WorkerSessionFailed", DateTimeOffset.UtcNow,
                [new("instanceId", instanceId.ToString()),
                 new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(cause)),
                 new("workerFailureCode", cause is AdapterWorkerOperationException failure ? failure.FailureCode : "Unknown"),
                 new("hresult", cause.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
                 .. nativeFailure,
                 .. observation]), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
    }

    private async Task ServeWorkerAsync(
        NamedPipeServerStream pipe,
        MirrorPulseAdapterTopology topology,
        AdapterInstance instance,
        WorkerSessionId sessionId,
        Action<int> selectProtocol,
        WorkerSessionFailureObservation observation,
        Func<LogField[]> captureObservation,
        CancellationToken cancellationToken)
    {
        observation.Advance(WorkerSessionStage.AwaitHello);
        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        ControlFrameEnvelope hello;
        try
        {
            hello = await ReadAsync(pipe, helloDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (helloDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            observation.DeadlineExpired = true;
            throw;
        }
        observation.Advance(WorkerSessionStage.Negotiate);
        ValidateFrame(hello, "Hello", instance.InstanceId, sessionId);
        AdapterWorkerProtocolSession protocol;
        try
        {
            protocol = AdapterWorkerProtocolSession.Negotiate(hello.Payload,
                topology.Roots.Where(root => root.InstanceId == instance.InstanceId),
                topology.Installations.Single(i => i.InstallId == instance.InstallId).Manifest.Protocol);
        }
        catch (InvalidDataException exception)
        {
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "HandshakeRejected", hello.RequestId,
                instance.InstanceId, sessionId, true, JsonSerializer.SerializeToElement(new { code = exception.Message })),
                cancellationToken).ConfigureAwait(false);
            await LogSessionFailureAsync(instance.InstanceId, exception, captureObservation()).ConfigureAwait(false);
            await SetPhaseAsync(instance.InstanceId, "Worker error", cancellationToken, exception.Message).ConfigureAwait(false);
            return;
        }
        var channel = new AdapterWorkerReadRangeClient(pipe, instance.InstanceId, sessionId, protocol);
        selectProtocol(protocol.ProtocolVersion);
        var upload = new AdapterWorkerUploadClient(channel, instance.InstanceId, sessionId);
        var stat = new AdapterWorkerStatClient(channel, instance.InstanceId, sessionId);
        var directory = new AdapterWorkerDirectoryPageClient(channel, instance.InstanceId, sessionId);
        var mutations = new AdapterWorkerMutationClient(channel, instance.InstanceId, sessionId);
        var instanceOperations = new SemaphoreSlim(1, 1);
        observation.Advance(WorkerSessionStage.SendReady);
        await channel.WriteControlAsync(new ControlFrameEnvelope(1, "Ready", hello.RequestId,
            instance.InstanceId, sessionId, true, protocol.CreateReadyPayload(instance)),
            cancellationToken).ConfigureAwait(false);
        if (!_connected.TryAdd(instance.InstanceId, channel))
        {
            throw new InvalidOperationException("The Adapter instance already has a connected Worker.");
        }

        _uploads[instance.InstanceId] = upload;
        _stats[instance.InstanceId] = stat;
        _directories[instance.InstanceId] = directory;
        _mutations[instance.InstanceId] = mutations;
        _instanceOperations[instance.InstanceId] = instanceOperations;

        await using var remoteInbox = new AdapterWorkerRemoteBatchInbox((payload, token) =>
            (_remoteBatch ?? throw new InvalidDataException("The Host has no remote batch ingress configured."))
                (instance.InstanceId, payload, token), cancellationToken);
        cancellationToken = remoteInbox.CancellationToken;

        try
        {
            observation.Advance(WorkerSessionStage.ReceiveFrames);
            while (true)
            {
                ControlFrameEnvelope frame = await ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (frame.ProtocolVersion != protocol.ProtocolVersion)
                    throw new InvalidDataException("The Worker changed its selected protocol version.");
                if (frame.IsResponse)
                {
                    if (channel.CanHandle(frame))
                    {
                        await channel.HandleResponseAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                    else if (upload.CanHandle(frame))
                    {
                        await upload.HandleResponseAsync(frame).ConfigureAwait(false);
                    }
                    else if (stat.CanHandle(frame))
                    {
                        await stat.HandleResponseAsync(frame).ConfigureAwait(false);
                    }
                    else if (directory.CanHandle(frame))
                    {
                        await directory.HandleResponseAsync(frame).ConfigureAwait(false);
                    }
                    else if (mutations.CanHandle(frame))
                    {
                        await mutations.HandleResponseAsync(frame).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new AdapterWorkerOperationException("UncorrelatedResponse");
                    }
                    continue;
                }

                ValidateFrame(frame, null, instance.InstanceId, sessionId, protocol.ProtocolVersion);
                switch (frame.MessageType)
                {
                    case "CredentialRequest":
                        await AnswerCredentialAsync(channel, instance, sessionId, frame, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "HostKeyChallenge":
                        await channel.WriteControlAsync(new ControlFrameEnvelope(1, "HostKeyDecision", frame.RequestId,
                            instance.InstanceId, sessionId, true,
                            JsonSerializer.SerializeToElement(new
                            {
                                sha256 = frame.Payload.GetProperty("sha256").GetString(),
                                approved = false,
                            })), cancellationToken).ConfigureAwait(false);
                        break;
                    case "Connected":
                        await SetPhaseAsync(instance.InstanceId, "Connected", cancellationToken).ConfigureAwait(false);
                        break;
                    case "TransferProgress":
                        await SaveTransferProgressAsync(instance.InstanceId, frame.Payload, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "RemoteBatch":
                        if (_remoteBatch is null)
                        {
                            throw new InvalidDataException("The Host has no remote batch ingress configured.");
                        }

                        remoteInbox.Post(frame.Payload);
                        break;
                    case "Error":
                        string code = frame.Payload.TryGetProperty("code", out JsonElement value)
                            ? value.GetString() ?? "Unknown" : "Unknown";
                        await LogSessionFailureAsync(instance.InstanceId, new AdapterWorkerOperationException(code), captureObservation()).ConfigureAwait(false);
                        await SetPhaseAsync(instance.InstanceId, "Worker error", cancellationToken, code)
                            .ConfigureAwait(false);
                        return;
                    default:
                        throw new InvalidDataException("The Adapter sent an unexpected control message.");
                }
            }
        }
        finally
        {
            _connected.TryRemove(instance.InstanceId, out _);
            _uploads.TryRemove(instance.InstanceId, out _);
            _stats.TryRemove(instance.InstanceId, out _);
            _directories.TryRemove(instance.InstanceId, out _);
            _mutations.TryRemove(instance.InstanceId, out _);
            _instanceOperations.TryRemove(instance.InstanceId, out _);
            channel.Close();
            upload.Close();
            stat.Close();
            directory.Close();
            mutations.Close();
            instanceOperations.Dispose();
        }
    }

    private async Task AnswerCredentialAsync(
        AdapterWorkerReadRangeClient channel,
        AdapterInstance instance,
        WorkerSessionId sessionId,
        ControlFrameEnvelope request,
        CancellationToken cancellationToken)
    {
        string referenceId = request.Payload.GetProperty("referenceId").GetString()
            ?? throw new InvalidDataException("The Worker credential reference is missing.");
        if (!instance.CredentialReferences.Contains(referenceId, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("The Worker requested a credential outside its instance.");
        }

        var reference = new CredentialReference(referenceId, CredentialKind.Password,
            instance.AdapterId.ToString(), CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
        using SecureCredentialValue stored = await _credentials.TryGetAsync(reference, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("The Adapter credential was not found.");
        string secret = Encoding.UTF8.GetString(stored.Value.Span);
        await channel.WriteControlAsync(new ControlFrameEnvelope(1, "CredentialResponse", request.RequestId,
            instance.InstanceId, sessionId, true,
            JsonSerializer.SerializeToElement(new { referenceId, secret })), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task SetPhaseAsync(
        InstanceId instanceId,
        string phase,
        CancellationToken cancellationToken,
        string? errorCode = null) =>
        _catalog.SaveInstanceRuntimeStateAsync(new(instanceId, phase, false, null, errorCode), cancellationToken);

    private async Task SaveTransferProgressAsync(
        InstanceId instanceId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        string operation = payload.GetProperty("operation").GetString()
            ?? throw new InvalidDataException("The transfer progress operation is missing.");
        long bytesTransferred = payload.GetProperty("bytesTransferred").GetInt64();
        long? totalBytes = payload.TryGetProperty("totalBytes", out JsonElement total)
            && total.ValueKind is not JsonValueKind.Null
            ? total.GetInt64()
            : null;
        if (string.IsNullOrWhiteSpace(operation) || bytesTransferred < 0 ||
            (totalBytes is not null && totalBytes.Value < 0) ||
            (totalBytes is not null && bytesTransferred > totalBytes.Value))
        {
            throw new InvalidDataException("The transfer progress values are invalid.");
        }

        string phase = payload.TryGetProperty("phase", out JsonElement phaseElement) &&
            phaseElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(phaseElement.GetString())
            ? phaseElement.GetString()!
            : "Transferring";
        MirrorPulseInstanceRuntimeState? current = await _catalog
            .ReadInstanceRuntimeStateAsync(instanceId, cancellationToken).ConfigureAwait(false);
        await _catalog.SaveInstanceRuntimeStateAsync(new(
            instanceId,
            phase,
            current?.RequiresFullRescan ?? false,
            current?.LastSuccessfulSync,
            current?.LastErrorCode,
            new MirrorPulseTransferProgress(operation, bytesTransferred, totalBytes, DateTimeOffset.UtcNow)),
            cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateFrame(
        ControlFrameEnvelope frame,
        string? expectedMessage,
        InstanceId instanceId,
        WorkerSessionId sessionId,
        int protocolVersion = 1)
    {
        if (frame.ProtocolVersion != protocolVersion || frame.InstanceId != instanceId || frame.WorkerSessionId != sessionId ||
            frame.IsResponse || (expectedMessage is not null && frame.MessageType != expectedMessage))
        {
            throw new InvalidDataException("The Adapter control frame does not belong to this session.");
        }
    }

    private static async Task<ControlFrameEnvelope> ReadAsync(Stream pipe, CancellationToken cancellationToken) =>
        ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(pipe, cancellationToken)
            .ConfigureAwait(false));

    private static async Task WriteAsync(Stream pipe, ControlFrameEnvelope frame, CancellationToken cancellationToken)
    {
        byte[] payload = ControlFrameJsonCodec.Encode(frame);
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _shutdown.Dispose();
        _log?.Dispose();
    }
}
