using System.Collections.ObjectModel;
using System.Diagnostics;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Workers;

/// <summary>
/// MP-owned information required to start one isolated Worker process.
/// </summary>
public sealed record WorkerLaunchRequest
{
    public WorkerLaunchRequest(
        InstanceId instanceId,
        WorkerSessionId workerSessionId,
        string executablePath,
        string workingDirectory,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (arguments is not null && arguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Worker arguments cannot be empty.", nameof(arguments));
        }

        if (environment is not null && environment.Any(pair => string.IsNullOrWhiteSpace(pair.Key)))
        {
            throw new ArgumentException("Worker environment variable names cannot be empty.", nameof(environment));
        }

        InstanceId = instanceId;
        WorkerSessionId = workerSessionId;
        ExecutablePath = Path.GetFullPath(executablePath);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        Arguments = new ReadOnlyCollection<string>((arguments ?? Array.Empty<string>()).ToArray());
        Environment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }

    public InstanceId InstanceId { get; }

    public WorkerSessionId WorkerSessionId { get; }

    public string ExecutablePath { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }
}

public sealed class WorkerProcessHandle : IDisposable
{
    internal WorkerProcessHandle(Process process, WorkerLaunchRequest request)
    {
        Process = process;
        InstanceId = request.InstanceId;
        WorkerSessionId = request.WorkerSessionId;
    }

    public Process Process { get; }

    public InstanceId InstanceId { get; }

    public WorkerSessionId WorkerSessionId { get; }

    public int ProcessId => Process.Id;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Process.WaitForExitAsync(cancellationToken);

    public void Dispose() => Process.Dispose();
}

/// <summary>
/// Starts Worker executables without shell expansion or inherited console handles.
/// </summary>
public static class WorkerProcessLauncher
{
    public static WorkerProcessHandle Start(WorkerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in WorkerEnvironmentPolicy.Create(request))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The Worker process could not be started.");
        return new WorkerProcessHandle(process, request);
    }
}
