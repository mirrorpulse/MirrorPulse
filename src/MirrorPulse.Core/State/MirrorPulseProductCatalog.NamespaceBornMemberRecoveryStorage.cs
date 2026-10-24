using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record BornMemberRecoveryHistory(int Count, MirrorPulseNamespaceBornMemberRecoveryAction? Latest,
        MirrorPulseNamespaceBornMemberRecoveryAction? Selected);

    internal const string BornMemberRecoveryActionSequencePageSql = """
        SELECT sequence FROM namespace_born_member_recovery_actions
        WHERE member_id=$member AND sequence >= $after AND ($first=1 OR sequence<>$after)
        ORDER BY sequence LIMIT 16;
        """;

    private async Task<IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction>> ReadBornMemberRecoveryHistoryCoreAsync(
        Guid memberId, SqliteTransaction transaction, CancellationToken token)
    {
        var requested = new List<MirrorPulseNamespaceBornMemberRecoveryAction>();
        _ = await ReadBornMemberRecoverySummaryCoreAsync(memberId, transaction, token, requestedHistory: requested).ConfigureAwait(false);
        return requested.AsReadOnly();
    }

    private async Task<BornMemberRecoveryHistory> ReadBornMemberRecoverySummaryCoreAsync(Guid memberId,
        SqliteTransaction transaction, CancellationToken token, Guid? selectedActionId = null,
        List<MirrorPulseNamespaceBornMemberRecoveryAction>? requestedHistory = null)
    {
        int count = 0;
        MirrorPulseNamespaceBornMemberRecoveryAction? latest = null, selected = null;
        long after = long.MinValue;
        bool first = true;
        while (true)
        {
            var page = new List<long>(16);
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = BornMemberRecoveryActionSequencePageSql;
                query.Parameters.AddWithValue("$member", memberId.ToString("D"));
                query.Parameters.AddWithValue("$after", after); query.Parameters.AddWithValue("$first", first ? 1 : 0);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                long previous = after;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    long sequence = reader.GetInt64(0);
                    if (page.Count == 16 || (!first || page.Count != 0) && sequence <= previous)
                        throw new InvalidDataException("The born recovery sequence page lost its strict continuation.");
                    page.Add(sequence); previous = sequence;
                }
            }
            foreach (long sequence in page)
            {
                var action = await ReadBornMemberRecoveryActionRowCoreAsync(memberId, sequence, transaction, token).ConfigureAwait(false);
                if (action.PreviousActionId != latest?.ActionId || count != 0 && action.PreparedAt <= latest!.PreparedAt || count == 4096)
                    throw new InvalidDataException("The born recovery indexes or predecessor chain are inconsistent.");
                var admission = await ReadNamespaceInitializationCoreAsync(action.InitializationOperationId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original born recovery initialization is missing.");
                if (!admission.Roots.Any(root => root.RootId == action.Child.Member.RootId) || action.PreparedAt < admission.PreparedAt)
                    throw new InvalidDataException("The born recovery action does not retain its admitted root and chronology.");
                await RequireBornMemberRecoveryHistoryReferencesAsync(action, transaction, token).ConfigureAwait(false);
                _ = await ReadBornMemberRecoveryCompletionCoreAsync(action.ActionId, action, transaction, token).ConfigureAwait(false);
                count++; latest = action;
                if (action.ActionId == selectedActionId)
                {
                    if (selected is not null) throw new InvalidDataException("The born recovery action index has multiple original owners.");
                    selected = action;
                }
                requestedHistory?.Add(action);
            }
            if (page.Count < 16) break;
            after = page[^1]; first = false;
        }
        await using (var head = _connection.CreateCommand())
        {
            head.Transaction = transaction; head.CommandText = "SELECT action_id FROM namespace_born_member_recovery_heads WHERE member_id=$member;";
            head.Parameters.AddWithValue("$member", memberId.ToString("D"));
            object? value = await head.ExecuteScalarAsync(token).ConfigureAwait(false);
            if ((value is null ? null : (string)value) != latest?.ActionId.ToString("D"))
                throw new InvalidDataException("The born recovery head lost its complete original action chain.");
        }
        await using (var pending = _connection.CreateCommand())
        {
            pending.Transaction = transaction; pending.CommandText = "SELECT root_id,action_id FROM namespace_born_member_recovery_pending WHERE member_id=$member;";
            pending.Parameters.AddWithValue("$member", memberId.ToString("D"));
            string? pendingRoot = null, pendingId = null;
            await using (var reader = await pending.ExecuteReaderAsync(token).ConfigureAwait(false))
                if (await reader.ReadAsync(token).ConfigureAwait(false)) { pendingRoot = reader.GetString(0); pendingId = reader.GetString(1); }
            bool unfinished = latest is not null && await ReadBornMemberRecoveryCompletionCoreAsync(latest.ActionId, latest, transaction, token).ConfigureAwait(false) is null;
            if (unfinished ? pendingId != latest!.ActionId.ToString("D") || pendingRoot != latest.Child.Member.RootId!.Value.Value.ToString("D") : pendingId is not null)
                throw new InvalidDataException("Born recovery lost its pending ownership or retained a completed reservation.");
        }
        return new(count, latest, selected);
    }

    private async Task<MirrorPulseNamespaceBornMemberRecoveryAction> ReadBornMemberRecoveryActionRowCoreAsync(Guid memberId,
        long sequence, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action_id,initialization_id,previous_id,payload,fingerprint FROM namespace_born_member_recovery_actions WHERE member_id=$member AND sequence=$sequence;";
        query.Parameters.AddWithValue("$member", memberId.ToString("D")); query.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The born recovery sequence lost its original action.");
        var action = ReadNamespaceReplacementPayload<MirrorPulseNamespaceBornMemberRecoveryAction>((byte[])reader[3], (byte[])reader[4], ValidateBornMemberRecoveryAction);
        if (action.Child.Member.MemberId != memberId || reader.GetString(0) != action.ActionId.ToString("D") ||
            reader.GetString(1) != action.InitializationOperationId.ToString("D") ||
            (reader.IsDBNull(2) ? null : reader.GetString(2)) != action.PreviousActionId?.ToString("D"))
            throw new InvalidDataException("The born recovery indexes or predecessor chain are inconsistent.");
        return action;
    }

    private async IAsyncEnumerable<MirrorPulseNamespaceBornMemberRecoveryAction> ReadPendingBornMemberRecoveryActionsCoreAsync(
        SqliteTransaction transaction, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (Guid member in ReadNamespaceOwnerIdsCoreAsync(NamespaceBornRecoveryMemberPageSql, transaction, token).ConfigureAwait(false))
        {
            var history = await ReadBornMemberRecoverySummaryCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (history.Latest is not { } latest) throw new InvalidDataException("Born recovery indexes have no original action.");
            if (await ReadBornMemberRecoveryCompletionCoreAsync(latest.ActionId, latest, transaction, token).ConfigureAwait(false) is null) yield return latest;
        }
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
        int separator = childPosition.RelativePath.LastIndexOf('/');
        string parentPath = separator < 0 ? string.Empty : childPosition.RelativePath[..separator];
        if (childPosition.RelativePath != action.Child.Location.RelativePath || childPosition.ParentMemberId != parent.MemberId ||
            parentPosition.RelativePath != action.Parent.Context.Location.RelativePath || parentPath != parentPosition.RelativePath)
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
        return (await ReadBornMemberRecoverySummaryCoreAsync(member, transaction, token, selectedActionId: actionId).ConfigureAwait(false)).Selected
            ?? throw new InvalidDataException("The born recovery action index lost its original intent.");
    }
}
