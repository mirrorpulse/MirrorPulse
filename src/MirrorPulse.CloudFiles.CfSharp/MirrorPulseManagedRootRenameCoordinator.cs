using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRootRenamePreparationCheckpoint { AfterPublicProofCaptured, AfterProductPreparationCommitted }

public sealed record MirrorPulseManagedRootRenameRecovery(MirrorPulseRootRenameIntent Intent,
    CloudDirectoryMoveReconciliationResult? LibraryResult);

/// <summary>Retains public pre-move evidence and projects verified local Label changes.</summary>
/// <remarks>
/// The caller quiesces remote apply and upload dispatch for the instance before preparation.
/// This coordinator never moves a source directory, issues a native rename, or acknowledges
/// journal entries. A pending product intent fences the root until recovery completes.
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseManagedRootRenameCoordinator
{
    private readonly CloudFileSystem _fileSystem;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseRootRouter _router;

    internal Func<MirrorPulseRootRenamePreparationCheckpoint, MirrorPulseRootRenameProof, CancellationToken, ValueTask>?
        PreparationCheckpoint
    { get; init; }

    public MirrorPulseManagedRootRenameCoordinator(CloudFileSystem fileSystem,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public async ValueTask<MirrorPulseRootRenameHistory> PrepareAsync(RootId rootId, string label,
        CancellationToken cancellationToken = default)
    {
        RootRegistration root = (await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false))
            .Roots.SingleOrDefault(item => item.RootId == rootId)
            ?? throw new FileNotFoundException("The managed root is not registered.");
        if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("The managed root cannot be renamed in its current state.");
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string target = label.Trim();
        var current = await _catalog.ReadManagedRootRenameAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (current is { IsPending: true } existing)
        {
            if (existing.TargetName != target || root.DirectoryName != existing.SourceName && root.DirectoryName != existing.TargetName)
                throw new InvalidOperationException("The managed root has a pending namespace transition.");
            var original = await ReadAsync(existing.OperationId, cancellationToken).ConfigureAwait(false);
            _ = Decode(original, root);
            return original;
        }
        if (await _catalog.ReadPendingRemoteBatchAsync(root.InstanceId, cancellationToken).ConfigureAwait(false) is not null ||
            await _catalog.ReadRetainedRemoteBatchAsync(root.InstanceId, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("The original remote batch must converge before preparing a root rename.");
        CloudDirectoryMoveProof proof = await _fileSystem.GetDirectory(root.DirectoryName)
            .PrepareMoveAsync(_fileSystem.Root, target, cancellationToken).ConfigureAwait(false);
        var captured = new MirrorPulseRootRenameProof(proof.RootItemId,
            new(proof.ExpectedBinding.VolumeSerialNumber, proof.ExpectedBinding.SyncRootFileId, proof.ExpectedBinding.LocalFileId),
            Convert.ToBase64String(proof.ExpectedPlaceholderIdentity.Span), DateTimeOffset.UtcNow,
            Convert.ToBase64String(proof.Encode()));
        _ = Decode(captured, root, root.DirectoryName, target);
        if (PreparationCheckpoint is not null)
            await PreparationCheckpoint(MirrorPulseRootRenamePreparationCheckpoint.AfterPublicProofCaptured, captured,
                cancellationToken).ConfigureAwait(false);
        // No bare product intent is published if SDK capture or validation fails.
        var history = await _catalog.PrepareManagedRootRenameWithProofAsync(rootId, target, root.DirectoryName,
            captured, cancellationToken).ConfigureAwait(false);
        _ = Decode(history, root);
        if (PreparationCheckpoint is not null)
            await PreparationCheckpoint(MirrorPulseRootRenamePreparationCheckpoint.AfterProductPreparationCommitted, history.Proof!,
                cancellationToken).ConfigureAwait(false);
        return history;
    }

    public async ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        MirrorPulseRootRenameHistory history = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (!history.Intent.IsPending)
        {
            // A historical receipt cannot revert a later Label change.
            await RefreshRouterAsync(cancellationToken).ConfigureAwait(false);
            return new(history.Intent, null);
        }
        RootRegistration root = (await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false))
            .Roots.SingleOrDefault(item => item.RootId == history.Intent.RootId)
            ?? throw new FileNotFoundException("The managed root is not registered.");
        CloudDirectoryMoveProof proof = Decode(history, root);
        CloudDirectoryMoveReconciliationResult result = await _fileSystem.GetDirectory(proof.SourceRelativePath)
            .ReconcileMoveAsync(proof, cancellationToken).ConfigureAwait(false);
        MirrorPulseRootRenameIntent intent = history.Intent;
        if (result.NativeMoveObserved && intent.Phase == MirrorPulseRootRenamePhase.Prepared)
        {
            // The public library verified the exact historical binding and opaque
            // identity at the intended destination. Preserve that fact, even when
            // projection has not committed. Never substitute a freshly found ID.
            MirrorPulseRootRenameProof original = history.Proof!;
            intent = await _catalog.ObserveManagedRootRenameAsync(operationId,
                new(original.LocalObject, original.PlaceholderIdentity, intent.TargetName, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        if (!result.NativeMoveObserved || !result.DurableProjectionCommitted || result.RequiresFullRescan ||
            result.Outcome is not (CloudDirectoryMoveReconciliationOutcome.Projected or
                CloudDirectoryMoveReconciliationOutcome.AlreadyProjected))
            return new(intent, result);
        if (intent.Phase == MirrorPulseRootRenamePhase.NativeObserved)
        {
            await _catalog.RenameManagedRootAsync(intent.RootId, intent.TargetName, cancellationToken).ConfigureAwait(false);
            intent = await _catalog.TransitionManagedRootRenameAsync(operationId, MirrorPulseRootRenamePhase.NativeObserved,
                MirrorPulseRootRenamePhase.LocalProjected, cancellationToken).ConfigureAwait(false);
        }
        await RefreshRouterAsync(cancellationToken).ConfigureAwait(false);
        if (intent.Phase == MirrorPulseRootRenamePhase.LocalProjected)
            intent = await _catalog.TransitionManagedRootRenameAsync(operationId, MirrorPulseRootRenamePhase.LocalProjected,
                MirrorPulseRootRenamePhase.Completed, cancellationToken).ConfigureAwait(false);
        return new(intent, result);
    }

    private async ValueTask<MirrorPulseRootRenameHistory> ReadAsync(Guid operationId, CancellationToken token) =>
        await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
        ?? throw new FileNotFoundException("The root rename history is not registered.");

    private async ValueTask RefreshRouterAsync(CancellationToken token) =>
        _router.ReplaceRegistrations((await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots,
            await _catalog.ReadManagedRootNamesAsync(token).ConfigureAwait(false));

    internal static CloudDirectoryMoveProof Decode(MirrorPulseRootRenameHistory history, RootRegistration root)
    {
        if (history.Intent.RootId != root.RootId)
            throw new InvalidDataException("The directory move evidence does not match the original managed root intent.");
        MirrorPulseRootRenameProof captured = history.Proof
            ?? throw new InvalidOperationException("The root rename has no original object proof.");
        return Decode(captured, root, history.Intent.SourceName, history.Intent.TargetName);
    }

    private static CloudDirectoryMoveProof Decode(MirrorPulseRootRenameProof captured, RootRegistration root,
        string sourceName, string targetName)
    {
        if (captured.DirectoryMoveEvidence is null)
            throw new NotSupportedException("This historical rename has no original public directory move evidence.");
        CloudDirectoryMoveProof proof = CloudDirectoryMoveProof.Decode(Convert.FromBase64String(captured.DirectoryMoveEvidence));
        Guid expectedItem = MirrorPulsePlaceholderIdentity.Create(root.InstanceId, $"mirrorpulse-root:{root.RootId}").ToCfSharp().ItemId;
        if (proof.RootItemId != expectedItem || proof.RootItemId != captured.ItemId ||
            !string.Equals(proof.SourceRelativePath, sourceName, StringComparison.OrdinalIgnoreCase) ||
            proof.DestinationRelativePath != targetName ||
            proof.ExpectedBinding.VolumeSerialNumber != captured.LocalObject.VolumeSerialNumber ||
            proof.ExpectedBinding.SyncRootFileId != captured.LocalObject.SyncRootFileId ||
            proof.ExpectedBinding.LocalFileId != captured.LocalObject.LocalFileId ||
            Convert.ToBase64String(proof.ExpectedPlaceholderIdentity.Span) != captured.PlaceholderIdentity)
            throw new InvalidDataException("The directory move evidence does not match the original managed root intent.");
        return proof;
    }
}
