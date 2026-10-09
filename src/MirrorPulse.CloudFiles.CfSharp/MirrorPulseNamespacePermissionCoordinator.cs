using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Private local-object facts inspected through a retained namespace lease.</summary>
public sealed record MirrorPulseNamespacePermissionObject(MirrorPulseLocalFileBinding LocalObject,
    RootId? RootId, string RelativePath, bool IsDirectory, string OwnerSid, string Dacl,
    DateTimeOffset ObservedAt);

/// <summary>The platform boundary for one stable, already scope-validated object.</summary>
/// <remarks>
/// Implementations must hold the target and its ancestor chain against replacement throughout
/// the lease, reject foreign reparse points, and apply the DACL to that retained object handle.
/// A read must freshly inspect owner and DACL, not return a cached descriptor. The caller owns
/// the lease. This contract does not expose source access, Worker RPC or namespace mutations.
/// </remarks>
public interface IMirrorPulseNamespacePermissionLease : IAsyncDisposable
{
    ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken);
    ValueTask ApplyDaclAsync(string dacl, CancellationToken cancellationToken);
}

public enum MirrorPulseNamespacePermissionOutcome { Verified, AlreadyVerified, RecoveryRequired }

public sealed record MirrorPulseNamespacePermissionResult(Guid OperationId,
    MirrorPulseNamespacePermissionOutcome Outcome, MirrorPulseNamespacePermissionRecoveryReason? RecoveryReason = null);

/// <summary>Reconciles one owned ACL intent with retained object facts and the product catalog.</summary>
/// <remarks>
/// Preparation precedes this call. This coordinator does not capture a new baseline, acquire a
/// platform lease, enable Host protection or declare a subtree ready. Cancellation or failure
/// after a native write leaves the original intent pending for inspection on the next attempt.
/// </remarks>
public sealed class MirrorPulseNamespacePermissionCoordinator(MirrorPulseProductCatalog catalog) : IAsyncDisposable
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _admission = new();
    private int _operations;
    private bool _disposing;
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;

    public async Task<MirrorPulseNamespacePermissionResult> ApplyAsync(Guid operationId,
        IMirrorPulseNamespacePermissionLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_admission)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _operations++;
        }
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            MirrorPulseNamespacePermissionChange change = await _catalog.ReadNamespacePermissionChangeForApplicationAsync(
                operationId, cancellationToken).ConfigureAwait(false);
            MirrorPulseNamespacePermissionBaseline baseline = await _catalog.ReadNamespacePermissionBaselineAsync(
                change.Intent.EvidenceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original permission evidence is missing.");
            if (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
                return new(operationId, MirrorPulseNamespacePermissionOutcome.RecoveryRequired, change.RecoveryReason);
            MirrorPulseNamespacePermissionObject observed = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseNamespacePermissionRecoveryReason? mismatch = MatchObject(baseline, change.Intent, observed);
            if (mismatch is not null) return await FenceAsync(change, mismatch.Value).ConfigureAwait(false);
            if (change.Phase == MirrorPulseNamespacePermissionPhase.Verified)
                return observed.Dacl == change.Verification!.Dacl
                    ? new(operationId, MirrorPulseNamespacePermissionOutcome.AlreadyVerified)
                    : await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged).ConfigureAwait(false);

            if (change.Phase == MirrorPulseNamespacePermissionPhase.Prepared)
            {
                if (observed.Dacl == change.Intent.ExpectedDacl)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await lease.ApplyDaclAsync(change.Intent.TargetDacl, cancellationToken).ConfigureAwait(false);
                }
                else if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(change.Intent.TargetDacl, observed.Dacl))
                    return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged).ConfigureAwait(false);
                // Either the write returned successfully, or recovery observed the exact target
                // on the original object. Retain that fact before the separate read-back.
                change = await _catalog.RecordNamespacePermissionApplicationAsync(operationId, baseline.LocalObject,
                    DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                observed = await lease.InspectAsync(CancellationToken.None).ConfigureAwait(false);
                mismatch = MatchObject(baseline, change.Intent, observed);
                if (mismatch is not null) return await FenceAsync(change, mismatch.Value).ConfigureAwait(false);
            }
            if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(change.Intent.TargetDacl, observed.Dacl))
                return await FenceAsync(change, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged).ConfigureAwait(false);
            await _catalog.VerifyNamespacePermissionChangeAsync(operationId,
                new(observed.LocalObject, observed.OwnerSid, observed.Dacl, observed.ObservedAt), CancellationToken.None).ConfigureAwait(false);
            return new(operationId, MirrorPulseNamespacePermissionOutcome.Verified);
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_admission)
            {
                _operations--;
                if (_disposing && _operations == 0) _drained?.TrySetResult();
            }
        }
    }

    /// <summary>Rejects new admission and drains accepted reconciliation before disposing its gate.</summary>
    /// <remarks>The owner must not await disposal from inside an admitted lease operation.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_admission)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposing = true;
            Task drain = _operations == 0 ? Task.CompletedTask :
                (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = ReleaseAsync(drain);
            return new(_disposeTask);
        }
    }

    private async Task ReleaseAsync(Task drain)
    {
        await drain.ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<MirrorPulseNamespacePermissionResult> FenceAsync(MirrorPulseNamespacePermissionChange change,
        MirrorPulseNamespacePermissionRecoveryReason reason)
    {
        // A verified record is a past fact. The Host must report the new mismatch and fence the
        // root; it must not replace historical verification with a later observation.
        if (change.Phase != MirrorPulseNamespacePermissionPhase.Verified)
            await _catalog.RequireNamespacePermissionRecoveryAsync(change.Intent.OperationId, reason, CancellationToken.None).ConfigureAwait(false);
        return new(change.Intent.OperationId, MirrorPulseNamespacePermissionOutcome.RecoveryRequired, reason);
    }

    private static MirrorPulseNamespacePermissionRecoveryReason? MatchObject(MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent, MirrorPulseNamespacePermissionObject observed)
    {
        if (observed.LocalObject != baseline.LocalObject || observed.IsDirectory != baseline.IsDirectory ||
            observed.RootId != intent.RootId || observed.RelativePath != intent.RelativePath)
            return MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged;
        if (observed.OwnerSid != baseline.OwnerSid) return MirrorPulseNamespacePermissionRecoveryReason.OwnerChanged;
        if (observed.ObservedAt < intent.PreparedAt) throw new InvalidDataException("The object observation predates its permission intent.");
        return null;
    }
}
