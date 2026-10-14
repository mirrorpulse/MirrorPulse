using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Starts the public CfSharp runtime with the Host's local namespace identity.</summary>
/// <remarks>
/// The same retained session restores normal identity after startup. Provider source calls and
/// lazy range bodies must use the separate normal-user transport; starting the runtime in this
/// role neither changes ACLs nor establishes tree readiness or remote dispatch authorization.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal static class MirrorPulseNamespaceRuntimeStartup
{
    internal static Task StartAsync(CloudFileSystem fileSystem, MirrorPulseNamespaceExecutionSession execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(execution);
        return execution.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(cancellationToken).ConfigureAwait(false));
    }
}
