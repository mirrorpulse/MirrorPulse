using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A retained current protection epoch; callers still need fresh native child and parent checks.</summary>
public sealed record MirrorPulseNamespaceMemberProtectionEpoch(Guid MemberId, Guid ProtectionId,
    string RoleSid, DateTimeOffset VerifiedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Rechecks current protection against the member's immutable provenance and permission references.</summary>
    public async Task<MirrorPulseNamespaceMemberProtectionEpoch> ReadNamespaceMemberProtectionEpochAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var proof = await ReadNamespaceMemberProofAsync(member, current: true, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new(member.MemberId, proof.ProtectionId, proof.RoleSid, proof.VerifiedAt);
        }
        finally { _gate.Release(); }
    }

    private async Task ValidatePermissionMemberLocationAsync(MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent, bool current, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? memberId = intent.Relocation?.MemberId;
        if (memberId is null)
        {
            if (!current) return;
            await using SqliteCommand query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id FROM namespace_members WHERE original_evidence_id=$original;";
            query.Parameters.AddWithValue("$original", baseline.EvidenceId.ToString("D"));
            object? stored = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (stored is null) return;
            memberId = ParseNamespaceMemberId(stored);
        }
        var member = await ReadNamespaceMemberRowAsync(memberId.Value, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The permission epoch's registered member is missing.");
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || member.OriginId != baseline.EvidenceId ||
            member.LocalObject != baseline.LocalObject || member.OwnerSid != baseline.OwnerSid || member.IsDirectory != baseline.IsDirectory ||
            intent.LocalObject != member.LocalObject || intent.RootId != member.RootId)
            throw new InvalidOperationException("The permission location must retain the same original member, binding and owner.");

        if (intent.Relocation is { } relocation)
        {
            // Check chronology before following references. A permission epoch cannot be
            // used by the same move that it depends on, including in damaged cyclic history.
            var placement = await ReadNamespaceMemberPositionEvidenceCoreAsync(member, relocation.CompletedMoveOperationId,
                intent.PreparedAt, transaction, token).ConfigureAwait(false);
            if (intent.RelativePath != placement.RelativePath || intent.PreparedAt <= placement.VerifiedAt ||
                intent.PreparedAt <= member.RegisteredAt || member.ProtectionId == intent.OperationId)
                throw new InvalidOperationException("A later permission epoch must follow the original object's completed relocation.");
        }
        if (current)
        {
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (location.RelativePath != intent.RelativePath || location.CurrentPositionOperationId != intent.Relocation?.CompletedMoveOperationId)
                throw new InvalidOperationException("Permission execution requires the registered member's latest committed location.");
        }
    }
}
