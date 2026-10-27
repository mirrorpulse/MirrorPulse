using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The immutable pending-initializer identity retained by a cancellation inventory.</summary>
/// <remarks>This witness grants no permission, native admission or current-tree readiness.</remarks>
public sealed record MirrorPulseRootLabelCancellationRecoveryOwner(Guid InitializationOperationId, string IntentSha256);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads a selected original unsealed Label's pending current-inventory owner, if any.</summary>
    public async Task<MirrorPulseRootLabelCancellationRecoveryOwner?> ReadRootLabelCancellationRecoveryOwnerAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var owner = await ReadRootLabelCancellationRecoveryOwnerCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return owner;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRootLabelCancellationRecoveryOwner?> ReadRootLabelCancellationRecoveryOwnerCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var history = await ReadManagedRootRenameHistoryCoreAsync(operationId, token, transaction).ConfigureAwait(false);
        MirrorPulseRootRenameIntent? current;
        await using (var selected = _connection.CreateCommand())
        {
            selected.Transaction = transaction;
            selected.CommandText = "SELECT payload,root_id FROM managed_root_renames WHERE root_id=$root;";
            selected.Parameters.AddWithValue("$root", history.Intent.RootId.ToString());
            await using var reader = await selected.ExecuteReaderAsync(token).ConfigureAwait(false);
            current = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)) : null;
        }
        if (history.Proof is not { DirectoryMoveEvidence: not null } || history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared ||
            history.Observation is not null || current != history.Intent)
            throw new InvalidOperationException("A cancellation recovery owner requires the exact original still-prepared public Label.");
        await RequireNoUnsealedCancellationMoveOwnershipAsync(operationId, transaction, token).ConfigureAwait(false);
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id FROM namespace_initialization_roots WHERE root_id=$root;";
        query.Parameters.AddWithValue("$root", history.Intent.RootId.ToString());
        object? stored = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (stored is null) return null;
        if (stored is not string text || !Guid.TryParseExact(text, "D", out Guid initialization) || initialization == Guid.Empty)
            throw new InvalidDataException("The selected cancellation initializer index is invalid.");
        return await ReadRootCancellationInitializerFingerprintCoreAsync(initialization, history.Intent.RootId,
            requirePending: true, transaction, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseRootLabelCancellationRecoveryOwner> ReadRootCancellationInitializerFingerprintCoreAsync(
        Guid initialization, RootId root, bool requirePending, SqliteTransaction transaction, CancellationToken token)
    {
        var status = await ReadNamespaceInitializationStatusCoreAsync(initialization, transaction, token).ConfigureAwait(false);
        if (requirePending && status.Completion is not null ||
            status.Intent.RequestedInventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory ||
            !status.Intent.Roots.Any(candidate => candidate.RootId == root))
            throw new InvalidOperationException("Cancellation recovery requires its original current-inventory initializer and exact selected root.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT fingerprint FROM namespace_initialization_intents WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", initialization.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not byte[] { Length: 32 } fingerprint)
            throw new InvalidDataException("The cancellation initializer lost its authenticated original intent fingerprint.");
        return new(initialization, Convert.ToHexString(fingerprint));
    }

    private async Task ValidateRootCancellationRecoveryOwnerCoreAsync(MirrorPulseRootLabelCancellationRecoveryOwner owner,
        RootId root, bool requirePending, SqliteTransaction transaction, CancellationToken token)
    {
        if (owner.InitializationOperationId == Guid.Empty || owner.IntentSha256 is not { Length: 64 } ||
            owner.IntentSha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("Cancellation recovery requires its exact original initializer identity and fingerprint.");
        var current = await ReadRootCancellationInitializerFingerprintCoreAsync(owner.InitializationOperationId, root,
            requirePending, transaction, token).ConfigureAwait(false);
        if (current != owner) throw new InvalidOperationException("Cancellation cannot replace its retained original initializer intent.");
    }
}
