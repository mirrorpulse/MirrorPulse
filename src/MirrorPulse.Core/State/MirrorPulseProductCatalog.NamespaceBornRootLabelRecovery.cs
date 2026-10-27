using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The original unsealed Label and retained initializer owning one born recovery action.</summary>
/// <remarks>This immutable correlation grants no permission, native admission or readiness.</remarks>
public sealed record MirrorPulseNamespaceBornRootLabelRecoveryOwner(Guid RootLabelOperationId,
    MirrorPulseRootLabelCancellationRecoveryOwner Initialization);

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record BornRootLabelRecoveryScope(MirrorPulseRootRenameHistory History,
        MirrorPulseNamespaceMemberRegistration Root);

    private static void ValidateBornRootLabelRecoveryOwner(MirrorPulseNamespaceBornMemberRecoveryAction action)
    {
        var owner = action.RootLabelRecoveryOwner;
        if (action.Version == 1 && owner is null) return;
        if (action.Version != 2 || owner is null || owner.Initialization is null ||
            owner.RootLabelOperationId == Guid.Empty || owner.RootLabelOperationId == action.ActionId ||
            owner.RootLabelOperationId == action.InitializationOperationId ||
            owner.Initialization.InitializationOperationId != action.InitializationOperationId ||
            owner.Initialization.IntentSha256 is not { Length: 64 } hash ||
            hash.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("A Label-owned born action requires its original operation and exact initializer fingerprint.", nameof(action));
    }

    private async Task<BornRootLabelRecoveryScope?> ValidateBornRootLabelRecoveryHistoryAsync(MirrorPulseNamespaceBornMemberRecoveryAction action,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (action.RootLabelRecoveryOwner is not { } owner) return null;
        await ValidateRootCancellationRecoveryOwnerCoreAsync(owner.Initialization, action.Child.Member.RootId!.Value,
            requirePending: false, transaction, token).ConfigureAwait(false);
        var history = await ReadManagedRootRenameHistoryCoreAsync(owner.RootLabelOperationId, token, transaction).ConfigureAwait(false);
        if (history.Proof is not { DirectoryMoveEvidence: not null } proof ||
            history.Intent.RootId != action.Child.Member.RootId || proof.CapturedAt > action.PreparedAt ||
            history.Observation is { } laterObservation && laterObservation.ObservedAt <= action.PreparedAt ||
            proof.LocalObject.VolumeSerialNumber != action.Child.Member.LocalObject.VolumeSerialNumber ||
            proof.LocalObject.SyncRootFileId != action.Child.Member.LocalObject.SyncRootFileId ||
            !(action.Child.Location.RelativePath == history.Intent.SourceName && action.Child.Member.IsDirectory &&
                action.Child.Member.LocalObject == proof.LocalObject ||
                action.Child.Location.RelativePath.StartsWith(history.Intent.SourceName + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Born recovery cannot replace its original unsealed Label, root, source scope or chronology.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_members WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;";
        AddNamespaceBirthBindingParameters(query, proof.LocalObject);
        object? stored = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var root = stored is null ? null : await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(stored), transaction, token).ConfigureAwait(false);
        if (root is null || !root.IsDirectory || root.RootId != action.Child.Member.RootId || root.LocalObject != proof.LocalObject ||
            root.OwnerSid != action.Child.Member.OwnerSid)
            throw new InvalidDataException("The original Label lost its exact retained root member and native binding.");
        var sealedMove = await ReadNamespaceMemberMoveRowAsync(owner.RootLabelOperationId, transaction, token).ConfigureAwait(false);
        if (sealedMove is not null && (sealedMove.Version != 3 || sealedMove.RemoteEntry is not null ||
            sealedMove.RootId != history.Intent.RootId || sealedMove.LocalObject != proof.LocalObject ||
            sealedMove.Source.RelativePath != history.Intent.SourceName || sealedMove.Target.RelativePath != history.Intent.TargetName ||
            sealedMove.DirectoryMoveEvidence != proof.DirectoryMoveEvidence || sealedMove.PreparedAt <= action.PreparedAt))
            throw new InvalidOperationException("Historical born recovery cannot replace later sealed Label ownership or its original chronology.");
        if (history.Intent.UnsealedCancellationOperationId is not null)
        {
            var cancellation = await ReadUnsealedRootLabelCancellationCoreAsync(owner.RootLabelOperationId, transaction, token).ConfigureAwait(false);
            if (cancellation is null || cancellation.RecoveryOwner != owner.Initialization ||
                cancellation.Observation.RootId != action.Child.Member.RootId ||
                history.Intent.UnsealedCancellationOperationId != owner.RootLabelOperationId)
                throw new InvalidOperationException("Born recovery requires its exact retained original cancellation and initializer.");
        }
        else if (history.Intent.Phase == MirrorPulseRootRenamePhase.Cancelled)
        {
            var cancellation = await ReadRootLabelCancellationRowAsync(owner.RootLabelOperationId, transaction, token).ConfigureAwait(false);
            if (sealedMove is null || cancellation is null || history.Intent.CancellationOperationId != owner.RootLabelOperationId)
                throw new InvalidOperationException("Historical born recovery lost its later original sealed cancellation receipt.");
            await ValidateRootLabelCancellationReferencesAsync(sealedMove, cancellation, current: false, transaction, token).ConfigureAwait(false);
        }
        return new(history, root);
    }

    private async Task ValidateBornRootLabelRecoveryCurrentAsync(MirrorPulseNamespaceBornMemberRecoveryAction action,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (action.RootLabelRecoveryOwner is not { } owner) return;
        var scope = await ValidateBornRootLabelRecoveryHistoryAsync(action, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The scoped born action lost its original Label correlation.");
        await ValidateRootCancellationRecoveryOwnerCoreAsync(owner.Initialization, action.Child.Member.RootId!.Value,
            requirePending: true, transaction, token).ConfigureAwait(false);
        var history = scope.History;
        if (history.Intent.Phase is not (MirrorPulseRootRenamePhase.Prepared or MirrorPulseRootRenamePhase.Cancelled) ||
            history.Observation is not null || history.Intent.CancellationOperationId is not null ||
            history.Intent.Phase == MirrorPulseRootRenamePhase.Cancelled && history.Intent.UnsealedCancellationOperationId != owner.RootLabelOperationId)
            throw new InvalidOperationException("Current born recovery requires its still-unsealed original Label or exact original unsealed cancellation.");
        await RequireNoUnsealedCancellationMoveOwnershipAsync(owner.RootLabelOperationId, transaction, token).ConfigureAwait(false);
        if (history.Intent.Phase == MirrorPulseRootRenamePhase.Prepared &&
            await ReadRootLabelCancellationRecoveryOwnerCoreAsync(owner.RootLabelOperationId, transaction, token).ConfigureAwait(false) != owner.Initialization)
            throw new InvalidOperationException("Born recovery lost its exact original pending Label and initializer owner.");
        var location = await ReadNamespaceMemberLocationCoreAsync(scope.Root, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMemberNameAsync(scope.Root, transaction, token).ConfigureAwait(false);
        if (location.RelativePath != history.Intent.SourceName || location.ParentMemberId is null)
            throw new InvalidOperationException("Label-owned born recovery requires its unchanged original first-level root position.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        string? payload = (string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var root = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(item => item.RootId == action.Child.Member.RootId);
        if (root is null || root.DirectoryName != history.Intent.SourceName ||
            root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("Born recovery cannot reinterpret its original root mapping or local lifecycle.");
    }
}
