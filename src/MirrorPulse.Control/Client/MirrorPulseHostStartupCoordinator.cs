using System.Diagnostics;
using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Control.Client;

/// <summary>
/// Candidate paths used by the Host starter. An MSIX path is trusted by the caller;
/// development paths require the explicit developer-mode switch.
/// </summary>
public sealed record MirrorPulseHostStartupOptions
{
    public string? InstalledHostPath { get; init; }

    public string? DevelopmentHostPath { get; init; }

    public bool DeveloperMode { get; init; }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        if (StartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StartTimeout));
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }
    }
}

/// <summary>
/// Starts the current-user Host and waits for its control channel.
/// </summary>
public sealed class MirrorPulseHostStartupCoordinator : IAsyncDisposable
{
    private readonly MirrorPulseHostLocator _locator;
    private readonly MirrorPulseHostStartupOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _startedProcess;
    private bool _disposed;

    public MirrorPulseHostStartupCoordinator(
        MirrorPulseHostLocator? locator = null,
        MirrorPulseHostStartupOptions? options = null)
    {
        _locator = locator ?? new MirrorPulseHostLocator();
        _options = options ?? new MirrorPulseHostStartupOptions();
        _options.Validate();
    }

    public string PipeName => _locator.PipeName;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (await _locator.IsRunningAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _locator.IsRunningAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            string executable = ResolveExecutablePath();
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    // The Host outlives this CLI invocation. Shell execution keeps its
                    // console handles separate, so a caller piping `mp` output does not
                    // wait forever for the Host to close the inherited pipe.
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                }) ?? throw new InvalidOperationException("The Host process did not start.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException &&
                                               exception is not StackOverflowException &&
                                               exception is not OutOfMemoryException)
            {
                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.HostStartFailed,
                        "The MirrorPulse Host process could not be started.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Native,
                        retryable: true));
            }

            _startedProcess = process;
            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.StartTimeout;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        throw new MirrorPulseControlException(
                            new ControlError(
                                MirrorPulseControlErrorCodes.HostStartFailed,
                                "The MirrorPulse Host exited before opening its control channel.",
                                MirrorPulse.Core.Contracts.ErrorCategory.Native,
                                retryable: true));
                    }

                    if (await _locator.IsRunningAsync(
                            _options.PollInterval, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }

                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.HostStartTimeout,
                        "The MirrorPulse Host did not open its control channel before the startup timeout.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Network,
                        retryable: true));
            }
            catch
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        if (_startedProcess is not null)
        {
            _startedProcess.Dispose();
        }

        await ValueTask.CompletedTask;
    }

    public string ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.InstalledHostPath))
        {
            return ValidateCandidate(_options.InstalledHostPath, trusted: true);
        }

        if (!_options.DeveloperMode)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.HostPathUntrusted,
                    "A development Host path requires developer mode.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Authorization));
        }

        string candidate = _options.DevelopmentHostPath ??
            Path.Combine(AppContext.BaseDirectory, "MirrorPulse.Host.exe");
        return ValidateCandidate(candidate, trusted: false);
    }

    private static string ValidateCandidate(string candidate, bool trusted)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.HostExecutableNotFound,
                    "The configured MirrorPulse Host path is invalid.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Validation));
        }

        if (!trusted && !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.HostPathUntrusted,
                    "A development Host path must point to an executable.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Authorization));
        }

        if (!File.Exists(fullPath))
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.HostExecutableNotFound,
                    "The configured MirrorPulse Host executable was not found.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Validation));
        }

        return fullPath;
    }
}
