using System.Runtime.Versioning;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseManagedRootRenameRestoreResult(Guid OperationId,
    MirrorPulseManagedRootRenameRecovery? Recovery, string? ErrorCode = null, int? FailureHResult = null);

/// <summary>Serializes historical root recovery with uploads and remote changes.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseManagedRootRenameService
{
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseInstanceScheduler _scheduler;
    private readonly Func<Guid, CancellationToken, ValueTask<MirrorPulseManagedRootRenameRecovery>> _recover;

    public MirrorPulseManagedRootRenameService(MirrorPulseProductCatalog catalog,
        MirrorPulseInstanceScheduler scheduler,
        Func<Guid, CancellationToken, ValueTask<MirrorPulseManagedRootRenameRecovery>> recover)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
    }

    public async ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A root rename operation ID is required.", nameof(operationId));
        MirrorPulseRootRenameIntent intent = (await _catalog.ReadManagedRootRenameHistoryAsync(cancellationToken).ConfigureAwait(false))
            .Select(item => item.Intent).SingleOrDefault(item => item.OperationId == operationId)
            ?? throw new FileNotFoundException("The root rename history is not registered.");
        RootRegistration root = (await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false))
            .Roots.SingleOrDefault(item => item.RootId == intent.RootId)
            ?? throw new FileNotFoundException("The managed root is not registered.");
        return await _scheduler.RunAsync(root.InstanceId, token => _recover(operationId, token), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult>> RestorePendingAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<MirrorPulseManagedRootRenameRestoreResult>();
        foreach (MirrorPulseRootRenameHistory history in await _catalog.ReadManagedRootRenameHistoryAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!history.Intent.IsPending) continue;
            if (history.Proof?.DirectoryMoveEvidence is null)
            {
                // A pre-preview.4 record cannot acquire a new pre-move object.
                // Leave its durable fence and original history untouched.
                results.Add(new(history.Intent.OperationId, null, "OriginalDirectoryProofMissing"));
                continue;
            }
            try
            {
                results.Add(new(history.Intent.OperationId,
                    await RecoverAsync(history.Intent.OperationId, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception failure) when (failure is not ObjectDisposedException &&
                failure is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // Per-root recovery failure must retain the namespace fence while
                // other roots start. Cancellation and catalog failures propagate.
                string code = failure switch
                {
                    InvalidDataException or ArgumentException => "OriginalDirectoryProofInvalid",
                    NotSupportedException => "OriginalDirectoryProofUnsupported",
                    InvalidOperationException => "DirectoryRecoveryBlocked",
                    _ => "DirectoryRecoveryUnavailable",
                };
                results.Add(new(history.Intent.OperationId, null, code, failure.HResult));
            }
        }
        return results.AsReadOnly();
    }
}
