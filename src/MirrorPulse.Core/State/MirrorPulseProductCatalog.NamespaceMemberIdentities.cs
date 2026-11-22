using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A member's retained public identity, separate from native authorization and its first identity.</summary>
public sealed record MirrorPulseNamespaceMemberIdentity(Guid MemberId, Guid? ItemId, string? RemoteId,
    Guid? AcceptanceOperationId, Guid? OriginalItemId, string? OriginalRemoteId);

/// <summary>Immutable correlation between one registered object and a confirmed upload acceptance.</summary>
/// <remarks>
/// The Host decodes both opaque identities through CfSharp before recording this correlation.
/// A successful historical receipt does not establish current content, protection, or location.
/// The original member, upload binding, and first identity are never rewritten.
/// </remarks>
public sealed record MirrorPulseNamespaceIdentityAcceptance(int Version, Guid MemberId, RootId RootId,
    Guid? PreviousAcceptanceOperationId, Guid PreviousItemId, string PreviousRemoteId,
    MirrorPulseContentAcceptanceProof Proof, MirrorPulseContentConfirmationReceipt Confirmation, string UploadIntentSha256);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceMemberIdentity> ReadNamespaceMemberIdentityAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var value = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains a confirmed identity transition before acknowledging its upload.</summary>
    /// <remarks>Caller-owned decoding and fresh native checks remain required; this method issues no filesystem operation or ACK.</remarks>
    public async Task<MirrorPulseNamespaceIdentityAcceptance> RecordNamespaceIdentityAcceptanceAsync(Guid memberId,
        Guid operationId, Guid? previousAcceptanceOperationId, Guid previousItemId, string previousRemoteId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty || operationId == Guid.Empty || previousItemId == Guid.Empty ||
            previousAcceptanceOperationId == Guid.Empty || previousAcceptanceOperationId == operationId)
            throw new ArgumentException("Complete, distinct identity correlation references are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(previousRemoteId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceIdentityAcceptanceCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.MemberId != memberId || replay.PreviousAcceptanceOperationId != previousAcceptanceOperationId ||
                    replay.PreviousItemId != previousItemId || replay.PreviousRemoteId != previousRemoteId)
                    throw new InvalidOperationException("An accepted identity correlation is immutable.");
                transaction.Commit(); return replay;
            }
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var current = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (member.IsDirectory || member.RootId is null || current.AcceptanceOperationId != previousAcceptanceOperationId ||
                current.ItemId != previousItemId || current.RemoteId != previousRemoteId)
                throw new InvalidOperationException("An identity acceptance must follow this file's latest retained public identity.");
            await RequireNamespaceMoveRootUnfencedAsync(member.RootId.Value, null, transaction, cancellationToken).ConfigureAwait(false);
            var proof = await ReadContentAcceptanceProofCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The durable upload acceptance proof is missing.");
            var confirmation = await ReadContentConfirmationReceiptCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The successful native and official confirmation receipt is missing.");
            var mutation = await ReadMutationCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The accepted upload intent is missing.");
            var accepted = new MirrorPulseNamespaceIdentityAcceptance(1, memberId, member.RootId.Value,
                previousAcceptanceOperationId, previousItemId, previousRemoteId, proof, confirmation,
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mutation.Intent, TopologyJsonOptions))));
            ValidateNamespaceIdentityAcceptance(accepted);
            await ValidateNamespaceIdentityAcceptanceReferencesAsync(accepted, member, transaction, cancellationToken).ConfigureAwait(false);
            if (previousAcceptanceOperationId is { } previous)
            {
                var predecessor = await ReadNamespaceIdentityTransitionAsync(member, previous, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The accepted identity predecessor is missing.");
                if (predecessor.ObservedAt > accepted.Confirmation.ObservedAt)
                    throw new InvalidOperationException("An accepted identity cannot predate its confirmed predecessor.");
            }
            await RequireNamespaceIdentityRootScopeAsync(accepted, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(accepted, TopologyJsonOptions);
            if (payload.Length > NamespaceIdentityAcceptanceLimit) throw new ArgumentException("Identity correlation must be bounded.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_member_identity_acceptances(operation_id,member_id,previous_operation_id,previous_move_id,payload,fingerprint)
                VALUES($operation,$member,$previous,$previousMove,$payload,$hash);
                """;
            insert.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$member", memberId.ToString("D"));
            await BindNamespaceIdentityPredecessorAsync(insert, memberId, previousAcceptanceOperationId, transaction, cancellationToken).ConfigureAwait(false);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return accepted;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceIdentityAcceptance?> ReadNamespaceIdentityAcceptanceAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An upload operation ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var value = await ReadNamespaceIdentityAcceptanceCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberIdentity> ReadNamespaceMemberIdentityCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _namespaceRecoveryReadSnapshots.TryGetValue(transaction, out var snapshot);
        if (snapshot?.MemberIdentities.TryGetValue(member, out var retained) == true) return retained;
        var original = await ReadOriginalNamespaceMemberIdentityAsync(member, transaction, token).ConfigureAwait(false);
        var identity = await ReadCurrentNamespaceIdentityHistoryAsync(member, original, transaction, token).ConfigureAwait(false);
        if (snapshot is not null) snapshot.MemberIdentities[member] = identity;
        return identity;
    }

    private async Task<(Guid? ItemId, string? RemoteId)> ReadOriginalNamespaceMemberIdentityAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
        {
            var birth = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The member lost its first public birth identity.");
            return (birth.ItemId, birth.RemoteId);
        }
        var identity = await ReadPermissionLocalIdentityCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false);
        return (identity?.ItemId, identity?.RemoteId);
    }
}
