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

    internal void Validate()
    {
        if (FrameReadTimeout <= TimeSpan.Zero || FrameWriteTimeout <= TimeSpan.Zero ||
            ConnectionTimeout <= TimeSpan.Zero || FrameReadTimeout > TimeSpan.FromHours(1) ||
            FrameWriteTimeout > TimeSpan.FromHours(1) || ConnectionTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout), "Control deadlines must be positive and bounded.");
        }
    }
}

/// <summary>
/// Serves one request at a time on the current-user versioned control pipe.
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
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = SecureNamedPipeServerFactory.Create(
                new NamedPipeServerOptions(_pipeName));
            Guid requestId = Guid.Empty;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connection.CancelAfter(_options.ConnectionTimeout);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                read.CancelAfter(_options.FrameReadTimeout);
                var request = MirrorPulseControlJsonCodec.Deserialize<ControlRequestEnvelope>(
                    await MirrorPulseControlPipeTransport.ReadFrameAsync(pipe, read.Token)
                        .ConfigureAwait(false));
                requestId = request.RequestId;
                var response = await _handler(request, connection.Token).ConfigureAwait(false);
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
