using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Routes retained root recovery to the owner of its original admission.</summary>
/// <remarks>The caller retains instance/library quiescence. Protected settlement must
/// precede current-tree permission rotation; it does not itself admit the current tree.</remarks>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseManagedRootRenameRecoveryRouter(CloudFileSystem fileSystem,
    MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession? execution, MirrorPulseNamespaceActor? actor)
{
    internal async ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverAsync(Guid operationId, CancellationToken token)
    {
        var admitted = await catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false);
        if (admitted?.Version != 3)
            return await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                .RecoverAsync(operationId, token).ConfigureAwait(false);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || execution is null || actor is null)
            throw new NotSupportedException("Original protected root recovery requires its Host namespace boundary.");
        _ = await new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, execution, actor)
            .RecoverObservedRootLabelAsync(operationId, token).ConfigureAwait(false);
        var history = await catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The settled original root Label history is missing.");
        if (history.Intent.IsPending)
            throw new InvalidDataException("Original protected root settlement did not complete its Label history.");
        // Completion is in the original product receipt. Do not fabricate a new SDK result.
        return new(history.Intent, null);
    }
}
