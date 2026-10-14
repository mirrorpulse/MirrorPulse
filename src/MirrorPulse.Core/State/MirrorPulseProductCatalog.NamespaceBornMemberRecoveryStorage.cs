using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction>> ReadBornMemberRecoveryHistoryCoreAsync(
        Guid memberId, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new List<MirrorPulseNamespaceBornMemberRecoveryAction>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT action_id,initialization_id,previous_id,payload,fingerprint FROM namespace_born_member_recovery_actions WHERE member_id=$member ORDER BY sequence LIMIT 4097;";
            query.Parameters.AddWithValue("$member", memberId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var action = ReadNamespaceReplacementPayload<MirrorPulseNamespaceBornMemberRecoveryAction>((byte[])reader[3], (byte[])reader[4], ValidateBornMemberRecoveryAction);
                if (action.Child.Member.MemberId != memberId || reader.GetString(0) != action.ActionId.ToString("D") ||
                    reader.GetString(1) != action.InitializationOperationId.ToString("D") ||
                    (reader.IsDBNull(2) ? null : reader.GetString(2)) != action.PreviousActionId?.ToString("D") ||
                    action.PreviousActionId != result.LastOrDefault()?.ActionId ||
                    result.Count != 0 && action.PreparedAt <= result[^1].PreparedAt || result.Count == 4096)
                    throw new InvalidDataException("The born recovery indexes or predecessor chain are inconsistent.");
                result.Add(action);
            }
        }
        await using (var head = _connection.CreateCommand())
        {
            head.Transaction = transaction; head.CommandText = "SELECT action_id FROM namespace_born_member_recovery_heads WHERE member_id=$member;";
            head.Parameters.AddWithValue("$member", memberId.ToString("D"));
            object? value = await head.ExecuteScalarAsync(token).ConfigureAwait(false);
            if ((value is null ? null : (string)value) != result.LastOrDefault()?.ActionId.ToString("D"))
                throw new InvalidDataException("The born recovery head lost its complete original action chain.");
        }
        foreach (var action in result)
        {
            var admission = await ReadNamespaceInitializationCoreAsync(action.InitializationOperationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original born recovery initialization is missing.");
            if (!admission.Roots.Any(root => root.RootId == action.Child.Member.RootId) || action.PreparedAt < admission.PreparedAt)
                throw new InvalidDataException("The born recovery action does not retain its admitted root and chronology.");
            await RequireBornMemberRecoveryHistoryReferencesAsync(action, transaction, token).ConfigureAwait(false);
            _ = await ReadBornMemberRecoveryCompletionCoreAsync(action.ActionId, action, transaction, token).ConfigureAwait(false);
        }
        await using (var pending = _connection.CreateCommand())
        {
            pending.Transaction = transaction; pending.CommandText = "SELECT root_id,action_id FROM namespace_born_member_recovery_pending WHERE member_id=$member;";
            pending.Parameters.AddWithValue("$member", memberId.ToString("D"));
            string? pendingRoot = null, pendingId = null;
            await using (var reader = await pending.ExecuteReaderAsync(token).ConfigureAwait(false))
                if (await reader.ReadAsync(token).ConfigureAwait(false)) { pendingRoot = reader.GetString(0); pendingId = reader.GetString(1); }
            var latest = result.Count == 0 ? null : result[^1];
            bool unfinished = latest is not null && await ReadBornMemberRecoveryCompletionCoreAsync(latest.ActionId, latest, transaction, token).ConfigureAwait(false) is null;
            if (unfinished ? pendingId != latest!.ActionId.ToString("D") || pendingRoot != latest.Child.Member.RootId!.Value.Value.ToString("D") : pendingId is not null)
                throw new InvalidDataException("Born recovery lost its pending ownership or retained a completed reservation.");
        }
        return result.AsReadOnly();
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction>> ReadPendingBornMemberRecoveryActionsCoreAsync(
        SqliteTransaction transaction, CancellationToken token)
    {
        var members = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id FROM namespace_born_member_recovery_actions UNION SELECT member_id FROM namespace_born_member_recovery_heads UNION SELECT member_id FROM namespace_born_member_recovery_pending;";
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) members.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        var result = new List<MirrorPulseNamespaceBornMemberRecoveryAction>();
        foreach (Guid member in members)
        {
            var history = await ReadBornMemberRecoveryHistoryCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (history.Count == 0) throw new InvalidDataException("Born recovery indexes have no original action.");
            var latest = history[^1];
            if (await ReadBornMemberRecoveryCompletionCoreAsync(latest.ActionId, latest, transaction, token).ConfigureAwait(false) is null) result.Add(latest);
        }
        return result.AsReadOnly();
    }

    private async Task RequireBornMemberRecoveryHistoryReferencesAsync(MirrorPulseNamespaceBornMemberRecoveryAction action,
        SqliteTransaction transaction, CancellationToken token)
    {
        var child = await ReadNamespaceMemberCoreAsync(action.Child.Member.MemberId, transaction, token).ConfigureAwait(false);
        var parent = await ReadNamespaceMemberCoreAsync(action.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
        if (child is null || parent is null || child != action.Child.Member || parent != action.Parent.Context.Member ||
            !SameNamespaceReplacementPayload(await ReadNamespaceBirthProtectionCoreAsync(action.Child.BirthProtection!.ProtectionId, transaction, token).ConfigureAwait(false),
                action.Child.BirthProtection) ||
            !SameNamespaceReplacementPayload(await ReadNamespaceBirthObservationCoreAsync(child.OriginId, transaction, token).ConfigureAwait(false), action.Child.BirthObservation))
            throw new InvalidDataException("Born recovery lost its immutable member, original observation or earlier protection.");
        var childPosition = await ReadNamespaceMemberPositionEvidenceCoreAsync(child, action.Child.Location.CurrentPositionOperationId, action.PreparedAt, transaction, token).ConfigureAwait(false);
        var parentPosition = await ReadNamespaceMemberPositionEvidenceCoreAsync(parent, action.Parent.Context.Location.CurrentPositionOperationId, action.PreparedAt, transaction, token).ConfigureAwait(false);
        if (childPosition.RelativePath != action.Child.Location.RelativePath || childPosition.ParentMemberId != parent.MemberId ||
            parentPosition.RelativePath != action.Parent.Context.Location.RelativePath || ParentMovePath(childPosition.RelativePath) != parentPosition.RelativePath)
            throw new InvalidDataException("Born recovery lost its retained child and direct-parent positions.");
        var proof = await ReadNamespaceMemberProofAsync(parent, false, transaction, token,
            action.Parent.Context.Protection.ProtectionId, action.PreparedAt).ConfigureAwait(false);
        if (proof.RoleSid != action.Parent.Context.Protection.RoleSid || proof.VerifiedAt != action.Parent.Context.Protection.VerifiedAt)
            throw new InvalidDataException("Born recovery lost its retained parent protection epoch.");
    }

    private async Task<MirrorPulseNamespaceBornMemberRecoveryAction> RequireBornMemberRecoveryActionCoreAsync(Guid actionId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_born_member_recovery_actions WHERE action_id=$id UNION SELECT member_id FROM namespace_born_member_recovery_heads WHERE action_id=$id;";
        query.Parameters.AddWithValue("$id", actionId.ToString("D"));
        Guid member;
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new FileNotFoundException("The born recovery action is missing.");
            member = ParseNamespaceMemberId(reader.GetString(0));
            if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The born recovery action has inconsistent ownership indexes.");
        }
        return (await ReadBornMemberRecoveryHistoryCoreAsync(member, transaction, token).ConfigureAwait(false)).SingleOrDefault(action => action.ActionId == actionId)
            ?? throw new InvalidDataException("The born recovery action index lost its original intent.");
    }
}
