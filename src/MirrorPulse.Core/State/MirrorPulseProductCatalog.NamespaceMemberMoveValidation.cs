using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const int NamespaceMemberMovePayloadLimit = 2_097_152;

    private static void ValidateNamespaceMemberMove(MirrorPulseNamespaceMemberMoveIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent.Source);
        ArgumentNullException.ThrowIfNull(intent.Target);
        ValidatePermissionBinding(intent.LocalObject);
        ValidatePermissionBinding(intent.Source.ParentLocalObject);
        ValidatePermissionBinding(intent.Target.ParentLocalObject);
        ValidatePermissionSid(intent.OwnerSid, role: false);
        ValidatePermissionSid(intent.RoleSid, role: true);
        ValidatePermissionRelativePath(intent.Source.RelativePath);
        ValidatePermissionRelativePath(intent.Target.RelativePath);
        if (intent.Version != 1 || intent.OperationId == Guid.Empty || intent.MemberId == Guid.Empty || intent.ProtectionId == Guid.Empty ||
            intent.RootId.Value == Guid.Empty || intent.ItemId == Guid.Empty || intent.Source.ParentMemberId == Guid.Empty ||
            intent.Target.ParentMemberId == Guid.Empty || intent.Source.ParentProtectionId == Guid.Empty || intent.Target.ParentProtectionId == Guid.Empty ||
            intent.PreparedAt == default || string.IsNullOrWhiteSpace(intent.RemoteId) || intent.RemoteId.Length > 16_384 ||
            intent.Source.RelativePath.LastIndexOf('/') <= 0 || intent.Target.RelativePath.LastIndexOf('/') <= 0 || intent.Source.RelativePath == intent.Target.RelativePath)
            throw new ArgumentException("A controlled move requires distinct child paths and complete retained identities.", nameof(intent));
        if (intent.PreviousMoveOperationId == Guid.Empty || intent.PreviousMoveOperationId == intent.OperationId ||
            intent.IdentityAcceptanceOperationId == Guid.Empty || intent.SourcePositionOperationId == Guid.Empty ||
            intent.SourcePositionOperationId == intent.OperationId || intent.Source.ParentPositionOperationId == Guid.Empty ||
            intent.Target.ParentPositionOperationId == Guid.Empty || intent.Source.ParentPositionOperationId == intent.OperationId ||
            intent.Target.ParentPositionOperationId == intent.OperationId)
            throw new ArgumentException("A relocation predecessor must identify a distinct completed move.", nameof(intent));
        if (intent.DirectoryMoveEvidence is { } encoded)
        {
            try
            {
                if (encoded.Length > 1_048_576 || encoded.Length == 0 || Convert.ToBase64String(Convert.FromBase64String(encoded)) != encoded)
                    throw new ArgumentException("Directory move evidence must be a bounded public encoding.", nameof(intent));
            }
            catch (FormatException error) { throw new ArgumentException("Directory move evidence must be a public base64 encoding.", nameof(intent), error); }
        }
    }

    private async Task ValidateNamespaceMemberMoveReferencesAsync(MirrorPulseNamespaceMemberMoveIntent intent, bool current,
        SqliteTransaction transaction, CancellationToken token)
    {
        var member = await ReadNamespaceMemberCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original move member is missing.");
        var source = await ReadNamespaceMemberCoreAsync(intent.Source.ParentMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original source parent is missing.");
        var target = await ReadNamespaceMemberCoreAsync(intent.Target.ParentMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The target parent member is missing.");
        if (member.LocalObject != intent.LocalObject || member.OwnerSid != intent.OwnerSid ||
            member.RootId != intent.RootId || source.RootId != intent.RootId ||
            target.RootId != intent.RootId || !source.IsDirectory || !target.IsDirectory ||
            source.LocalObject != intent.Source.ParentLocalObject || target.LocalObject != intent.Target.ParentLocalObject ||
            source.OwnerSid != intent.OwnerSid || target.OwnerSid != intent.OwnerSid || intent.PreparedAt < member.RegisteredAt ||
            intent.PreparedAt < source.RegisteredAt || intent.PreparedAt < target.RegisteredAt ||
            target.MemberId == member.MemberId || member.IsDirectory != (intent.DirectoryMoveEvidence is not null))
            throw new InvalidOperationException("Move admission must preserve its original member and direct parents within one managed root.");
        await RequireNamespaceMoveIdentityAsync(intent, member, current, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMoveParentPositionAsync(source, intent.Source, intent.PreparedAt, current, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMoveParentPositionAsync(target, intent.Target, intent.PreparedAt, current, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMemberMoveSourceAsync(intent, member, transaction, token).ConfigureAwait(false);
        if (current)
        {
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (location.CompletedMoveOperationId != intent.PreviousMoveOperationId ||
                location.CurrentPositionOperationId != (intent.SourcePositionOperationId ?? intent.PreviousMoveOperationId) || location.ParentMemberId != source.MemberId ||
                location.RelativePath != intent.Source.RelativePath)
                throw new InvalidOperationException("Move admission must start at the member's latest committed location.");
        }
        if (current)
        {
            var ancestor = target;
            var visited = new HashSet<Guid>();
            while (true)
            {
                if (!visited.Add(ancestor.MemberId)) throw new InvalidDataException("The target's committed parent lineage contains a cycle.");
                var ancestorLocation = await ReadNamespaceMemberLocationCoreAsync(ancestor, transaction, token).ConfigureAwait(false);
                if (ancestorLocation.ParentMemberId is not { } ancestorId) break;
                if (ancestorId == member.MemberId) throw new InvalidOperationException("A directory cannot move into its own descendant.");
                ancestor = await ReadNamespaceMemberRowAsync(ancestorId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The target parent lost its committed lineage.");
            }
        }
        foreach (var pair in new[] { (Member: member, Protection: intent.ProtectionId),
            (Member: source, Protection: intent.Source.ParentProtectionId), (Member: target, Protection: intent.Target.ParentProtectionId) })
        {
            var proof = await ReadNamespaceMemberProofAsync(pair.Member, current, transaction, token,
                pair.Protection, intent.PreparedAt).ConfigureAwait(false);
            if (proof.ProtectionId != pair.Protection || proof.RoleSid != intent.RoleSid || proof.VerifiedAt > intent.PreparedAt)
                throw new InvalidOperationException("Move admission requires its retained verified protection epochs.");
            if (current) await RequireCurrentNamespaceMoveLineageAsync(pair.Member, intent.RoleSid, intent.PreparedAt, transaction, token).ConfigureAwait(false);
        }
    }

    private async Task RequireCurrentNamespaceMoveLineageAsync(MirrorPulseNamespaceMemberRegistration member, string roleSid,
        DateTimeOffset preparedAt, SqliteTransaction transaction, CancellationToken token)
    {
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(member.MemberId)) throw new InvalidDataException("The current member lineage contains a cycle.");
            var proof = await ReadNamespaceMemberProofAsync(member, true, transaction, token).ConfigureAwait(false);
            if (proof.RoleSid != roleSid || proof.VerifiedAt > preparedAt)
                throw new InvalidOperationException("Move execution requires current protection throughout its retained parent lineage.");
            await RequireNamespaceMemberNameAsync(member, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (location.ParentMemberId is not { } parentId) break;
            member = await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The protected move lineage lost a parent.");
        }
    }

    private static string ParentMovePath(string relativePath) => relativePath[..relativePath.LastIndexOf('/')];

    private async Task RequireNamespaceMoveTargetAvailableAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_member_names WHERE parent_member_id=$parent AND child_name_key=$name;";
        query.Parameters.AddWithValue("$parent", intent.Target.ParentMemberId.ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(intent.Target.RelativePath));
        object? value = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (value is not null && (ParseNamespaceMemberId(value) != intent.MemberId ||
            intent.Source.ParentMemberId != intent.Target.ParentMemberId ||
            NamespaceMemberNameKey(intent.Source.RelativePath) != NamespaceMemberNameKey(intent.Target.RelativePath)))
            throw new InvalidOperationException("An existing member owns the move destination.");
        var target = await ReadNamespaceMemberRowAsync(intent.Target.ParentMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The destination member disappeared.");
        query.Parameters.Clear();
        query.CommandText = "SELECT 1 FROM namespace_birth_reservations WHERE parent_kind=$kind AND parent_id=$parent AND child_name_key=$name;";
        query.Parameters.AddWithValue("$kind", (int)target.Origin);
        query.Parameters.AddWithValue("$parent", target.OriginId.ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(intent.Target.RelativePath));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An unresolved birth owns the move destination.");
    }

    private async Task RequireNamespaceMoveRootUnfencedAsync(RootId rootId, Guid? allowedMove,
        SqliteTransaction transaction, CancellationToken token, Guid? allowedRetirement = null, Guid? allowedBatch = null)
    {
        var pending = await ReadPendingNamespaceMemberMoveCoreAsync(rootId, transaction, token).ConfigureAwait(false);
        if (pending is not null && pending.OperationId != allowedMove)
            throw new InvalidOperationException("An unresolved controlled move has fenced this managed root.");
        if ((await ReadManagedRootRenamesCoreAsync(token, transaction).ConfigureAwait(false)).Any(intent => intent.RootId == rootId && intent.IsPending))
            throw new InvalidOperationException("An unresolved Label change has fenced this managed root.");
        foreach (var retirement in await ReadPendingNamespaceRetirementsCoreAsync(transaction, token).ConfigureAwait(false))
            if (retirement.Intent.RootId == rootId && retirement.OperationId != allowedRetirement)
                throw new InvalidOperationException("An unresolved controlled retirement has fenced this managed root.");
        var batch = await ReadPendingNamespaceRetirementBatchCoreAsync(rootId, transaction, token).ConfigureAwait(false);
        if (batch is not null && batch.OperationId != allowedBatch)
            throw new InvalidOperationException("An unresolved controlled subtree retirement has fenced this managed root.");
    }
}
