using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Records drained completion and the existing located birth protection in one transaction.</summary>
    public async Task<MirrorPulseNamespaceBornMemberRecoveryCompletion> CompleteNamespaceBornMemberRecoveryAsync(Guid actionId,
        MirrorPulseNamespaceBirthProtection protection, bool drained, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protection); ValidateNamespaceBirthProtection(protection);
        var completion = new MirrorPulseNamespaceBornMemberRecoveryCompletion(1, actionId, protection.ProtectionId, drained, completedAt);
        ValidateBornMemberRecoveryCompletion(completion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var action = await RequireBornMemberRecoveryActionCoreAsync(actionId, transaction, cancellationToken).ConfigureAwait(false);
            var replay = await ReadBornMemberRecoveryCompletionCoreAsync(actionId, action, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != completion || !SameNamespaceReplacementPayload(
                    await ReadNamespaceBirthProtectionCoreAsync(protection.ProtectionId, transaction, cancellationToken).ConfigureAwait(false), protection))
                    throw new InvalidOperationException("The born recovery completion is immutable.");
                transaction.Commit(); return replay;
            }
            var history = await ReadBornMemberRecoveryHistoryCoreAsync(action.Child.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (history[^1].ActionId != actionId) throw new InvalidOperationException("A superseded born recovery action cannot complete.");
            await RequireBornMemberRecoveryCurrentAsync(action, transaction, cancellationToken).ConfigureAwait(false);
            RequireBornMemberRecoveryProjection(action, protection, completion);
            await RecordNamespaceBirthProtectionCoreAsync(protection, transaction, cancellationToken,
                allowedBornRecoveryInitialization: action.InitializationOperationId).ConfigureAwait(false);
            byte[] payload = NamespaceReplacementPayload(completion);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_born_member_recovery_completions(action_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", actionId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var release = _connection.CreateCommand(); release.Transaction = transaction;
            release.CommandText = "DELETE FROM namespace_born_member_recovery_pending WHERE member_id=$member AND action_id=$id;";
            release.Parameters.AddWithValue("$member", action.Child.Member.MemberId.ToString("D")); release.Parameters.AddWithValue("$id", actionId.ToString("D"));
            if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("Born recovery completion lost its pending member ownership.");
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBornMemberRecoveryCompletion?> ReadNamespaceBornMemberRecoveryCompletionAsync(Guid actionId,
        CancellationToken cancellationToken = default)
    {
        if (actionId == Guid.Empty) throw new ArgumentException("An action ID is required.", nameof(actionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var action = await RequireBornMemberRecoveryActionCoreAsync(actionId, transaction, cancellationToken).ConfigureAwait(false);
            var completion = await ReadBornMemberRecoveryCompletionCoreAsync(actionId, action, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBornMemberRecoveryCompletion?> ReadBornMemberRecoveryCompletionCoreAsync(Guid actionId,
        MirrorPulseNamespaceBornMemberRecoveryAction action, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_born_member_recovery_completions WHERE action_id=$id;";
        query.Parameters.AddWithValue("$id", actionId.ToString("D"));
        MirrorPulseNamespaceBornMemberRecoveryCompletion completion;
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            completion = ReadNamespaceReplacementPayload<MirrorPulseNamespaceBornMemberRecoveryCompletion>((byte[])reader[0], (byte[])reader[1], ValidateBornMemberRecoveryCompletion);
        }
        if (completion.ActionId != actionId || await ReadNamespaceBirthProtectionCoreAsync(completion.ProtectionId, transaction, token).ConfigureAwait(false) is not { } protection ||
            protection.Verification.ObservedAt > completion.CompletedAt)
            throw new InvalidDataException("The born recovery completion lost its verified protection projection.");
        try { RequireBornMemberRecoveryProjection(action, protection, completion); }
        catch (InvalidOperationException error) { throw new InvalidDataException("The born recovery completion lost its original action correlation.", error); }
        return completion;
    }

    private static void RequireBornMemberRecoveryProjection(MirrorPulseNamespaceBornMemberRecoveryAction action,
        MirrorPulseNamespaceBirthProtection protection, MirrorPulseNamespaceBornMemberRecoveryCompletion completion)
    {
        var parent = action.Parent.Context;
        var expected = new MirrorPulseNamespaceBirthProtectionPlacement(action.Child.Member.MemberId,
            action.Child.Location.CurrentPositionOperationId, parent.Member.MemberId, parent.Protection.ProtectionId,
            parent.Location.CurrentPositionOperationId, action.Child.Location.RelativePath);
        if (protection.Version != 3 || protection.ProtectionId != action.ActionId || protection.BirthOperationId != action.Child.Member.OriginId ||
            protection.PreviousProtectionId != action.Child.BirthProtection!.ProtectionId || protection.Placement != expected ||
            protection.RoleSid != parent.Protection.RoleSid || protection.Verification.LocalObject != action.Child.Member.LocalObject ||
            protection.Verification.OwnerSid != action.Child.Member.OwnerSid || protection.Verification.ObservedAt < action.PreparedAt ||
            completion.CompletedAt < protection.Verification.ObservedAt ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.TargetDacl, protection.Verification.Dacl))
            throw new InvalidOperationException("Born recovery completion lost its admitted binding, parent, role, descriptor or chronology.");
    }

    private static void ValidateBornMemberRecoveryCompletion(MirrorPulseNamespaceBornMemberRecoveryCompletion completion)
    {
        if (completion.Version != 1 || completion.ActionId == Guid.Empty || completion.ProtectionId != completion.ActionId ||
            !completion.Drained || completion.CompletedAt == default)
            throw new ArgumentException("Born recovery completion requires the actual drained scope and its separate protection epoch.");
    }
}
