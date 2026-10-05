using System.Buffers.Binary;
using System.IO.Pipes;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Control.Transport;

/// <summary>
/// Length-prefixed framing used by the versioned control channel.
/// </summary>
public static class MirrorPulseControlPipeTransport
{
    public static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = await LengthPrefixedFrameReader.ReadAsync(stream, MirrorPulseControlSchema.MaximumFrameBytes, cancellationToken)
            .ConfigureAwait(false);
        if (frame.Length > MirrorPulseControlSchema.MaximumFrameBytes)
        {
            throw new InvalidDataException("The control frame exceeds the protocol limit.");
        }

        return frame;
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        if (payload.Length > MirrorPulseControlSchema.MaximumFrameBytes)
        {
            throw new InvalidDataException("The control frame exceeds the protocol limit.");
        }

        var lengthPrefix = new byte[ControlFrameLimits.LengthPrefixBytes];
        ControlFrameLimits.WritePayloadLength((uint)payload.Length, lengthPrefix);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Deadlines for untrusted control connections and cancellation-aware Host handlers.</summary>
public sealed record MirrorPulseControlPipeOptions
{
    public TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FrameWriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumConnections { get; init; } = 16;
    public int MaximumConcurrentRequests { get; init; } = 8;
    public int ReservedControlRequests { get; init; } = 2;

    internal void Validate()
    {
        if (FrameReadTimeout <= TimeSpan.Zero || FrameWriteTimeout <= TimeSpan.Zero ||
            ConnectionTimeout <= TimeSpan.Zero || FrameReadTimeout > TimeSpan.FromHours(1) ||
            FrameWriteTimeout > TimeSpan.FromHours(1) || ConnectionTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout), "Control deadlines must be positive and bounded.");
        }
        if (MaximumConnections is < 3 or > 64 || MaximumConcurrentRequests < 1 || ReservedControlRequests < 1 ||
            MaximumConcurrentRequests + ReservedControlRequests >= MaximumConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConnections), "Control concurrency must leave a bounded admission slot.");
        }
    }
}

/// <summary>A safe diagnostic for a control pipe name already claimed by another server.</summary>
public sealed class MirrorPulseControlPipeClaimException : IOException
{
    public MirrorPulseControlPipeClaimException() : base("The current-user control pipe name is already occupied.") { }
}

/// <summary>
/// Serves bounded concurrent requests while reserving status, cancel and shutdown capacity.
/// </summary>
public sealed class MirrorPulseControlPipeServer
{
    private readonly Func<ControlRequestEnvelope, CancellationToken, ValueTask<ControlResponseEnvelope>> _handler;
    private readonly string _pipeName;
    private readonly MirrorPulseControlPipeOptions _options;

    public MirrorPulseControlPipeServer(
        Func<ControlRequestEnvelope, CancellationToken, ValueTask<ControlResponseEnvelope>> handler,
        string? pipeName = null,
        MirrorPulseControlPipeOptions? options = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? MirrorPulseControlPipeNames.CurrentUserV1()
            : pipeName.Trim();
        _options = options ?? new();
        _options.Validate();
    }

    public string PipeName => _pipeName;

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        using var ordinary = new SemaphoreSlim(_options.MaximumConcurrentRequests);
        using var control = new SemaphoreSlim(_options.ReservedControlRequests);
        using var mutation = new SemaphoreSlim(1);
        var connections = new List<Task>();
        NamedPipeServerStream? listener = null;
        try
        {
            listener = CreateListener(firstInstance: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                Task[] completed = connections.Where(task => task.IsCompleted).ToArray();
                if (completed.Length > 0)
                {
                    await Task.WhenAll(completed).ConfigureAwait(false);
                    foreach (Task task in completed) connections.Remove(task);
                }

                await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream pipe = listener;
                listener = null;
                // Keep the pipe name owned throughout handover, even if a request completes immediately.
                try { listener = CreateListener(firstInstance: false); }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
                if (connections.Count >= _options.MaximumConnections)
                {
                    // One bounded admission slot rejects overload without creating additional tasks.
                    await HandleConnectionAsync(pipe, ordinary, control, mutation, true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    connections.Add(Task.Run(() => HandleConnectionAsync(pipe, ordinary, control, mutation,
                        false, cancellationToken), CancellationToken.None));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
            // Host handlers must honor cancellation. Keep their ownership until they actually finish.
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreateListener(bool firstInstance)
    {
        try
        {
            return SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(_pipeName)
            {
                MaxInstances = _options.MaximumConnections + 2,
                PipeOptions = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            });
        }
        catch (Exception exception) when (firstInstance && exception is IOException or UnauthorizedAccessException)
        {
            throw new MirrorPulseControlPipeClaimException();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, SemaphoreSlim ordinary,
        SemaphoreSlim control, SemaphoreSlim mutation, bool overloaded, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            Guid requestId = Guid.Empty;
            try
            {
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connection.CancelAfter(_options.ConnectionTimeout);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                read.CancelAfter(_options.FrameReadTimeout);
                var request = MirrorPulseControlJsonCodec.Deserialize<ControlRequestEnvelope>(
                    await MirrorPulseControlPipeTransport.ReadFrameAsync(pipe, read.Token)
                        .ConfigureAwait(false));
                requestId = request.RequestId;
                ControlResponseEnvelope response = overloaded
                    ? Busy(requestId)
                    : await DispatchBoundedAsync(request, ordinary, control, mutation, connection.Token).ConfigureAwait(false);
                connection.Token.ThrowIfCancellationRequested();
                using var write = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                write.CancelAfter(_options.FrameWriteTimeout);
                await MirrorPulseControlPipeTransport.WriteFrameAsync(
                    pipe,
                    MirrorPulseControlJsonCodec.Serialize(response),
                    write.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                await TryWriteFailureAsync(pipe, requestId,
                    MirrorPulseControlErrorCodes.RequestTimeout,
                    "The control connection timed out.",
                    ErrorCategory.Network,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await TryWriteFailureAsync(pipe, requestId,
                    MirrorPulseControlErrorCodes.InvalidRequest,
                    "The control request is invalid.",
                    ErrorCategory.Protocol,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                await TryWriteFailureAsync(pipe, requestId,
                    MirrorPulseControlErrorCodes.InvalidRequest,
                    "The control request is invalid.",
                    ErrorCategory.Validation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A disconnected client must not stop the Host control channel.
            }
        }
    }

    private async ValueTask<ControlResponseEnvelope> DispatchBoundedAsync(ControlRequestEnvelope request,
        SemaphoreSlim ordinary, SemaphoreSlim control, SemaphoreSlim mutation, CancellationToken cancellationToken)
    {
        bool priority = request.Command is MirrorPulseControlCommands.HostStatus or MirrorPulseControlCommands.HostStop or
            MirrorPulseControlCommands.HostRestart or MirrorPulseControlCommands.SyncStatus or
            MirrorPulseControlCommands.OperationGet or MirrorPulseControlCommands.OperationCancel;
        SemaphoreSlim capacity = priority ? control : ordinary;
        if (!capacity.Wait(0, cancellationToken)) return Busy(request.RequestId);
        try
        {
            bool mutating = !priority && MirrorPulseControlCommands.Catalog.Any(command =>
                command.Name == request.Command && command.Mutating);
            if (!mutating) return await _handler(request, cancellationToken).ConfigureAwait(false);
            await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await _handler(request, cancellationToken).ConfigureAwait(false); }
            finally { mutation.Release(); }
        }
        finally { capacity.Release(); }
    }

    private static ControlResponseEnvelope Busy(Guid requestId) => ControlResponseEnvelope.Failure(requestId,
        new ControlError(MirrorPulseControlErrorCodes.HostBusy, "The Host control channel is busy.",
            ErrorCategory.Network, retryable: true));

    private async Task TryWriteFailureAsync(
        NamedPipeServerStream pipe,
        Guid requestId,
        string code,
        string message,
        ErrorCategory category,
        CancellationToken cancellationToken)
    {
        if (!pipe.IsConnected || requestId == Guid.Empty)
        {
            return;
        }

        var response = ControlResponseEnvelope.Failure(
            requestId,
            new ControlError(code, message, category));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.FrameWriteTimeout);
            await MirrorPulseControlPipeTransport.WriteFrameAsync(
                pipe,
                MirrorPulseControlJsonCodec.Serialize(response),
                deadline.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The peer may have disconnected while the error was being sent.
        }
        catch (OperationCanceledException)
        {
            // An unresponsive peer cannot prolong an error response or Host shutdown.
        }
    }
}
