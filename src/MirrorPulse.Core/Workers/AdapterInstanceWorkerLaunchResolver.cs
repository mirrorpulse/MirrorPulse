using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Workers;

public sealed record AdapterInstanceWorkerPayload(
    InstallId InstallId,
    string Version,
    WorkerLaunchRequest LaunchRequest);

/// <summary>Uses the instance's pinned installation, rather than an Adapter-wide active pointer.</summary>
public static class AdapterInstanceWorkerLaunchResolver
{
    public static AdapterInstanceWorkerPayload Resolve(
        MirrorPulseAdapterTopology topology,
        InstanceId instanceId,
        WorkerSessionId sessionId,
        string runtimeIdentifier,
        IEnumerable<string>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        AdapterInstance instance = topology.Instances.SingleOrDefault(item => item.InstanceId == instanceId)
            ?? throw new FileNotFoundException("The Adapter instance is not installed.");
        InstalledAdapter installation = topology.Installations.SingleOrDefault(item => item.InstallId == instance.InstallId)
            ?? throw new FileNotFoundException("The Adapter instance's selected installation is missing.");
        if (installation.AdapterId != instance.AdapterId)
        {
            throw new InvalidDataException("The selected installation belongs to a different Adapter.");
        }

        return ResolveInstallation(installation, instanceId, sessionId, runtimeIdentifier, arguments);
    }

    internal static AdapterInstanceWorkerPayload ResolveInstallation(InstalledAdapter installation,
        InstanceId instanceId, WorkerSessionId sessionId, string runtimeIdentifier, IEnumerable<string>? arguments)
    {
        AdapterInstallationHealth health = AdapterInstallationHealthChecker.Check(installation, runtimeIdentifier);
        if (!health.IsHealthy)
        {
            throw new InvalidDataException("The selected Adapter payload is unhealthy: " +
                string.Join(", ", health.Problems));
        }

        AdapterPayloadSelection selected = AdapterPayloadSelector.Select(installation.Manifest, runtimeIdentifier);
        string executable = Path.Combine(installation.InstallationDirectory,
            selected.Entrypoint.Replace('/', Path.DirectorySeparatorChar));
        return new(installation.InstallId, installation.Version,
            new WorkerLaunchRequest(instanceId, sessionId, executable,
                installation.InstallationDirectory, arguments));
    }
}
