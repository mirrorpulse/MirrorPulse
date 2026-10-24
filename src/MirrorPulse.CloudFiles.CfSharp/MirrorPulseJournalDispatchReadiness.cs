using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Rechecks current policy after a finite admission may have spanned scheduling turns.</summary>
internal static class MirrorPulseJournalDispatchReadiness
{
    internal static async ValueTask<bool> MayDispatchAsync(MirrorPulseWorkerChangeCommand command, MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog, Func<InstanceId, bool> mayDispatch, CancellationToken token)
    {
        RootRegistration root = router.GetRegistration(command.InstanceId, command.RootKey);
        return root.State == RootRegistrationState.Active && mayDispatch(command.InstanceId) &&
            !(await catalog.ReadNamespaceDispatchFencedRootsAsync(token).ConfigureAwait(false)).Contains(root.RootId) &&
            !await catalog.HasPendingUploadConflictAsync(command.OperationId, token).ConfigureAwait(false);
    }
}
