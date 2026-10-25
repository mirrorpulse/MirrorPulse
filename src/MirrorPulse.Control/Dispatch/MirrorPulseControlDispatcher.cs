using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Control.Dispatch;

public delegate ValueTask<TResult> MirrorPulseControlCommandHandler<TArguments, TResult>(
    TArguments arguments,
    CancellationToken cancellationToken);

/// <summary>
/// Resolves versioned command names and translates handler failures into safe responses.
/// </summary>
public sealed class MirrorPulseControlDispatcher
{
    private readonly Dictionary<string, Func<ControlRequestEnvelope, CancellationToken,
        ValueTask<ControlResponseEnvelope>>> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> RegisteredCommands => _handlers.Keys.ToArray();

    public void Register<TArguments, TResult>(
        string command,
        MirrorPulseControlCommandHandler<TArguments, TResult> handler)
        where TArguments : IMirrorPulseControlArguments
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A command name is required.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(handler);
        var normalizedCommand = command.Trim();
        if (!_handlers.TryAdd(normalizedCommand, InvokeAsync))
        {
            throw new InvalidOperationException($"The control command '{normalizedCommand}' is already registered.");
        }

        async ValueTask<ControlResponseEnvelope> InvokeAsync(
            ControlRequestEnvelope request,
            CancellationToken cancellationToken)
        {
            TArguments arguments = DeserializeArguments<TArguments>(request.Arguments);
            TResult result = await handler(arguments, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return ControlResponseEnvelope.Success(request.RequestId);
            }

            using var resultDocument = JsonDocument.Parse(MirrorPulseControlJsonCodec.Serialize(result));
            return ControlResponseEnvelope.Success(request.RequestId, resultDocument.RootElement.Clone());
        }
    }

    public async ValueTask<ControlResponseEnvelope> DispatchAsync(
        ControlRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            return ControlResponseEnvelope.Failure(
                request.RequestId,
                new ControlError(
                    MirrorPulseControlErrorCodes.UnknownCommand,
                    $"The control command '{request.Command}' is not supported.",
                    ErrorCategory.Unsupported));
        }

        try
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            return ControlResponseEnvelope.Failure(
                request.RequestId,
                MirrorPulseControlErrorMapper.FromException(exception));
        }
    }

    private static TArguments DeserializeArguments<TArguments>(JsonElement arguments)
        where TArguments : IMirrorPulseControlArguments
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(arguments.GetRawText());
        return MirrorPulseControlJsonCodec.Deserialize<TArguments>(payload);
    }
}

/// <summary>
/// Maps implementation exceptions to stable, non-sensitive control errors.
/// </summary>
public static class MirrorPulseControlErrorMapper
{
    public static ControlError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            MirrorPulse.Core.Sync.MirrorPulseTransferAdmissionBusyException => new ControlError(
                MirrorPulseControlErrorCodes.HostBusy,
                "Original local work must settle before a new transfer can be admitted.",
                ErrorCategory.Native,
                retryable: true),
            OperationCanceledException => new ControlError(
                MirrorPulseControlErrorCodes.OperationCancelled,
                "The operation was cancelled.",
                ErrorCategory.Cancelled),
            TimeoutException => new ControlError(
                MirrorPulseControlErrorCodes.RequestTimeout,
                "The operation timed out.",
                ErrorCategory.Network,
                retryable: true),
            UnauthorizedAccessException => new ControlError(
                MirrorPulseControlErrorCodes.Unauthorized,
                "The operation is not authorized for the current user.",
                ErrorCategory.Authorization),
            NotSupportedException => new ControlError(
                MirrorPulseControlErrorCodes.Unsupported,
                "The requested operation is not supported.",
                ErrorCategory.Unsupported),
            IOException => new ControlError(
                MirrorPulseControlErrorCodes.HostUnavailable,
                "The Host or one of its storage dependencies is unavailable.",
                ErrorCategory.Network,
                retryable: true),
            ArgumentException or JsonException or InvalidDataException => new ControlError(
                MirrorPulseControlErrorCodes.InvalidRequest,
                "The control request is invalid.",
                ErrorCategory.Validation),
            _ => new ControlError(
                MirrorPulseControlErrorCodes.InternalFailure,
                "The Host could not complete the operation.",
                ErrorCategory.Native,
                diagnosticId: Guid.NewGuid().ToString("N"))
        };
    }
}
