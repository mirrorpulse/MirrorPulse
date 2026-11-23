using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Host;

/// <summary>Runs a signed Worker for one authorization, without opening a sync session or granting it the live Worker state.</summary>
internal sealed class AdapterConfigurationWorkerSession : IAsyncDisposable
{
    private const int ProtocolVersion = 2;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly string _workspace;
    private readonly AdapterInstance _instance;
    private readonly AdapterConfigurationCallbackRequest _invocation;
    private readonly AdapterConfigurationCallbackPolicy _policy;
    private readonly ISecureCredentialStore _credentials;
    private NamedPipeServerStream? _pipe;
    private WorkerJobObject? _job;
    private WorkerProcessHandle? _worker;
    private bool _attached;
    private bool _connected;
    private bool _faulted;
    private AdapterConfigurationCallbackState? _lastState;
    private int _activeRequest;
    private int _disposed;

    private AdapterConfigurationWorkerSession(string workspace, AdapterInstance instance,
        AdapterConfigurationCallbackRequest invocation, AdapterConfigurationCallbackPolicy policy,
        ISecureCredentialStore credentials)
    {
        _workspace = workspace;
        _instance = instance;
        _invocation = invocation;
        _policy = policy;
        _credentials = credentials;
    }

    public static async Task<AdapterConfigurationWorkerSession> OpenAsync(InstalledAdapter installation,
        AdapterInstance? instance, IReadOnlyDictionary<string, string> configuration, string authorizationRoot,
        AdapterConfigurationCallbackRequest invocation, ISecureCredentialStore credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!installation.IsSigned || installation.Manifest.Presentation is not { } presentation)
            throw new NotSupportedException("Authorization requires a signed Adapter configuration form.");
        if (instance is not null && (instance.InstallId != installation.InstallId || instance.AdapterId != installation.AdapterId))
            throw new InvalidDataException("The Adapter installation does not match the selected deployment.");
        if (invocation.InvocationId == Guid.Empty || invocation.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The Adapter authorization has expired.");

        var policy = new AdapterConfigurationCallbackPolicy(presentation, invocation.CallbackId);
        var preview = SignedAdapterFormValidator.Preview(presentation, instance, configuration);
        var sessionId = WorkerSessionId.New();
        string workspace = WorkerPersistentStateDirectory.Prepare(authorizationRoot, new InstanceId(sessionId.Value));
        var context = new AdapterInstance(installation.AdapterId, installation.InstallId, instance?.InstanceId ?? InstanceId.New(),
            instance?.DisplayName ?? presentation.Resolve(null).Title, preview, instance?.CredentialReferences ?? [],
            Path.Combine(workspace, "files"), Path.Combine(workspace, "transfer"), false, AdapterLifecycleState.Disabled,
            sessionId, DateTimeOffset.UtcNow)
        {
            CredentialBindings = instance?.CredentialBindings,
        };
        var session = new AdapterConfigurationWorkerSession(workspace, context, invocation, policy, credentials);
        try
        {
            await session.ConnectAsync(installation, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectAsync(InstalledAdapter installation, CancellationToken cancellationToken)
    {
        WorkerSessionId sessionId = _instance.WorkerSessionId!.Value;
        string pipeName = WorkerLaunchNonce.CreatePipeName();
        string[] arguments = ["--instance-id", _instance.InstanceId.ToString(), "--worker-session-id", sessionId.ToString(),
            "--pipe-name", pipeName, "--configuration-only"];
        string runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The Adapter process architecture is unsupported."),
        };
        WorkerLaunchRequest launch = AdapterInstanceWorkerLaunchResolver.ResolveInstallation(
            installation, _instance.InstanceId, sessionId, runtime, arguments).LaunchRequest;
        string state = Directory.CreateDirectory(Path.Combine(_workspace, "state")).FullName;
        Directory.CreateDirectory(_instance.FileCacheDirectory);
        Directory.CreateDirectory(_instance.TransferCacheDirectory);
        var request = new WorkerLaunchRequest(launch.InstanceId, launch.WorkerSessionId, launch.ExecutablePath,
            launch.WorkingDirectory, launch.Arguments, new Dictionary<string, string>
            {
                ["MP_FILE_CACHE_DIR"] = _instance.FileCacheDirectory,
                ["MP_TRANSFER_CACHE_DIR"] = _instance.TransferCacheDirectory,
                ["MP_WORKER_STATE_DIR"] = state,
            });

        _pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName)
        {
            PipeOptions = PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
        });
        _job = WorkerJobObject.Create();
        _worker = WorkerProcessLauncher.Start(request);
        _job.Attach(_worker.Process);
        _attached = true;
        using var deadline = Deadline(TimeSpan.FromSeconds(15), cancellationToken);
        await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        NamedPipePeerIdentity.ValidateClient(_pipe, _worker.ProcessId, _worker.Process.SessionId);
        ControlFrameEnvelope hello = await WorkerControlFrames.ReadAsync(_pipe, deadline.Token).ConfigureAwait(false);
        AdapterInstanceProcessSupervisor.ValidateFrame(hello, "Hello", _instance.InstanceId, sessionId);
        AdapterWorkerProtocolSession protocol = AdapterWorkerProtocolSession.NegotiateConfigurationOnly(hello.Payload, installation.Manifest.Protocol);
        await WorkerControlFrames.WriteAsync(_pipe, new(1, "Ready", hello.RequestId, _instance.InstanceId, sessionId,
            true, protocol.CreateReadyPayload(_instance)), deadline.Token).ConfigureAwait(false);
    }

    public async Task<AdapterConfigurationCallbackResult> ExchangeAsync(string message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (message is not (AdapterConfigurationCallbackContract.StartMessage or AdapterConfigurationCallbackContract.PollMessage
            or AdapterConfigurationCallbackContract.CancelMessage))
            throw new ArgumentOutOfRangeException(nameof(message));
        if (Interlocked.Exchange(ref _activeRequest, 1) != 0)
            throw new InvalidOperationException("The Adapter authorization already has an active request.");

        try
        {
            if (_faulted) throw new InvalidOperationException("The Adapter authorization Worker is unavailable.");
            if (message == AdapterConfigurationCallbackContract.StartMessage ? _lastState is not null :
                _lastState != AdapterConfigurationCallbackState.Pending)
                throw new InvalidOperationException("The Adapter authorization cannot accept this request in its current state.");
            using var deadline = Deadline(TimeSpan.FromSeconds(12), cancellationToken);
            WorkerSessionId sessionId = _instance.WorkerSessionId!.Value;
            Guid requestId = Guid.NewGuid();
            await WorkerControlFrames.WriteAsync(_pipe!, new(ProtocolVersion, message, requestId,
                _instance.InstanceId, sessionId, false, JsonSerializer.SerializeToElement(_invocation, WireJson)),
                deadline.Token).ConfigureAwait(false);

            while (true)
            {
                ControlFrameEnvelope frame = await WorkerControlFrames.ReadAsync(_pipe!, deadline.Token).ConfigureAwait(false);
                if (frame.InstanceId != _instance.InstanceId || frame.WorkerSessionId != sessionId ||
                    frame.ProtocolVersion != ProtocolVersion)
                    throw new InvalidDataException("The authorization frame does not belong to this Worker session.");
                if (frame.IsResponse)
                {
                    if (frame.RequestId != requestId || frame.MessageType != AdapterConfigurationCallbackContract.ResultMessage)
                        throw new InvalidDataException("The Adapter returned an unmatched authorization response.");
                    var result = frame.Payload.Deserialize<AdapterConfigurationCallbackResult>(WireJson)
                        ?? throw new InvalidDataException("The Adapter returned an empty authorization response.");
                    _policy.Validate(_invocation, result, DateTimeOffset.UtcNow,
                        cancelling: message == AdapterConfigurationCallbackContract.CancelMessage);
                    _lastState = result.State;
                    return result;
                }

                if (frame.MessageType == "Connected" && !_connected)
                {
                    _connected = true;
                    continue;
                }

                if (frame.MessageType != "CredentialRequest")
                    throw new InvalidDataException("The Adapter sent a sync message during configuration.");
                ControlFrameEnvelope credential = await AdapterWorkerCredentialBroker.CreateResponseAsync(
                    _credentials, _instance, frame, ProtocolVersion, deadline.Token).ConfigureAwait(false);
                await WorkerControlFrames.WriteAsync(_pipe!, credential, deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally { Volatile.Write(ref _activeRequest, 0); }
    }

    private CancellationTokenSource Deadline(TimeSpan maximum, CancellationToken cancellationToken)
    {
        TimeSpan remaining = _invocation.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new InvalidDataException("The Adapter authorization has expired.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < maximum ? remaining : maximum);
        return deadline;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_worker is not null && !_worker.Process.HasExited)
            {
                if (_attached)
                {
                    if (_pipe?.IsConnected is true && !_faulted && Volatile.Read(ref _activeRequest) == 0)
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        try
                        {
                            await WorkerControlFrames.WriteAsync(_pipe, new(ProtocolVersion, "Stop", Guid.NewGuid(),
                                _instance.InstanceId, _instance.WorkerSessionId!.Value, false, JsonSerializer.SerializeToElement(new { })),
                                grace.Token).ConfigureAwait(false);
                            await _worker.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
                    }
                    if (!_worker.Process.HasExited) _job!.Terminate();
                }
                else
                {
                    try { _worker.Process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (_worker.Process.HasExited) { }
                }

                await Task.Run(_worker.Process.WaitForExit).ConfigureAwait(false);
            }
        }
        finally
        {
            _job?.Dispose();
            _worker?.Dispose();
            if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
            WorkerPersistentStateDirectory.RequireExistingAncestors(_workspace);
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        }
    }
}
