using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateNamespaceTransferPlan(MirrorPulseNamespaceTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version is not (1 or 2) || plan.OperationId == Guid.Empty || plan.PrivateContentId == Guid.Empty ||
            plan.CopyOperationId == Guid.Empty || plan.DeleteOperationId == Guid.Empty ||
            new[] { plan.OperationId, plan.PrivateContentId, plan.CopyOperationId, plan.DeleteOperationId }.Distinct().Count() != 4 ||
            plan.PreparedAt == default || plan.Length < 0 || plan.Sha256 is not { Length: 64 } ||
            plan.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)) ||
            plan.SourceRoute is null || plan.TargetRoute is null || plan.SourceRoute.RootId == plan.TargetRoute.RootId ||
            plan.Source is not { Context: { Member: { IsDirectory: false } source, Location: not null, Protection: not null, Verification: not null } child, Identity: not null } ||
            plan.SourceParent is not { Context: { Member: { IsDirectory: true } sourceParent, Location: not null, Protection: not null, Verification: not null } parent, Identity: not null } ||
            plan.TargetParent is not { Context: { Member: { IsDirectory: true } targetParent, Location: not null, Protection: not null, Verification: not null } target, Identity: not null } ||
            source.RootId != plan.SourceRoute.RootId || sourceParent.RootId != source.RootId || targetParent.RootId != plan.TargetRoute.RootId ||
            child.Location.ParentMemberId != sourceParent.MemberId ||
            source.LocalObject.VolumeSerialNumber != sourceParent.LocalObject.VolumeSerialNumber ||
            source.LocalObject.SyncRootFileId != sourceParent.LocalObject.SyncRootFileId ||
            source.LocalObject.VolumeSerialNumber != targetParent.LocalObject.VolumeSerialNumber ||
            source.LocalObject.SyncRootFileId != targetParent.LocalObject.SyncRootFileId ||
            child.Protection.RoleSid != parent.Protection.RoleSid || child.Protection.RoleSid != target.Protection.RoleSid ||
            source.OwnerSid != sourceParent.OwnerSid || source.OwnerSid != targetParent.OwnerSid ||
            plan.Source.Identity is not { ItemId: not null, RemoteId: not null } ||
            plan.SourceRemoteRevision is { } revision && InvalidRemoteUpdateText(revision, 4096))
            throw new ArgumentException("A file transfer requires distinct original roots, protected members, stable effects and complete content identity.", nameof(plan));
        if (plan.Version == 1 && plan.CommandOrigin is not null || plan.Version == 2 &&
            (plan.CommandOrigin is not { Version: 1 } command || command.CommandId != plan.OperationId ||
                command.SourceMemberId != child.Member.MemberId || command.TargetParentMemberId != target.Member.MemberId ||
                command.TargetName != plan.TargetRelativePath[(plan.TargetRelativePath.LastIndexOf('/') + 1)..] ||
                command.RequestedAt == default || command.RequestedAt > plan.PreparedAt))
            throw new ArgumentException("A controlled transfer must retain its original command, members, direct target name and request time.", nameof(plan));
        if (plan.TargetRoute.ProtocolVersion != 2)
            throw new ArgumentException("The first transfer copy requires explicit Worker V2 conditional target support.", nameof(plan));
        foreach (var route in new[] { plan.SourceRoute, plan.TargetRoute })
            if (route.InstanceId.Value == Guid.Empty || route.RootId.Value == Guid.Empty || route.InstallId.Value == Guid.Empty ||
                route.ProtocolVersion is not (1 or 2) || InvalidRemoteUpdateText(route.RootKey, 256) || route.RootKey.Any(char.IsControl) ||
                InvalidRemoteUpdateText(route.DirectoryName, 255) || route.DirectoryName.IndexOfAny(['/', '\\']) >= 0 ||
                route.ScopeSha256 is not { Length: 64 } || route.ScopeSha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
                throw new ArgumentException("A transfer route requires its original backend and wire scope.", nameof(plan));
        ValidatePermissionRelativePath(plan.TargetRelativePath);
        if (plan.TargetRelativePath.LastIndexOf('/') < 1 || child.Location.RelativePath.LastIndexOf('/') < 1 ||
            ParentMovePath(plan.TargetRelativePath) != target.Location.RelativePath ||
            ParentMovePath(child.Location.RelativePath) != parent.Location.RelativePath ||
            !child.Location.RelativePath.StartsWith(plan.SourceRoute.DirectoryName + "/", StringComparison.Ordinal) ||
            !target.Location.RelativePath.StartsWith(plan.TargetRoute.DirectoryName + "/", StringComparison.Ordinal) &&
                target.Location.RelativePath != plan.TargetRoute.DirectoryName)
            throw new ArgumentException("Transfer paths must retain their exact direct parents in their original roots.", nameof(plan));
        foreach (var snapshot in new[] { plan.Source, plan.SourceParent, plan.TargetParent })
            if (plan.PreparedAt < snapshot.Context.Member.RegisteredAt || plan.PreparedAt < snapshot.Context.Verification.ObservedAt ||
                snapshot.Context.PositionVerification is { } position && plan.PreparedAt < position.ObservedAt)
                throw new ArgumentException("Transfer admission cannot predate its protected objects.", nameof(plan));
    }

    private async Task RequireCurrentNamespaceTransferAsync(MirrorPulseNamespaceTransferPlan plan,
        SqliteTransaction transaction, CancellationToken token, bool admitted = false,
        MirrorPulseNamespaceTransferTargetBirth? targetBirth = null)
    {
        if (targetBirth is not null && (!admitted || targetBirth.OperationId != plan.OperationId ||
            await ReadNamespaceTransferTargetBirthRowAsync(plan.OperationId, null, transaction, token).ConfigureAwait(false) != targetBirth))
            throw new InvalidOperationException("A transfer continuation cannot bypass another target birth or its original owner.");
        foreach (var snapshot in new[] { plan.Source, plan.SourceParent, plan.TargetParent })
        {
            if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(snapshot.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != snapshot)
                throw new InvalidOperationException("Transfer execution requires the exact current members, protection, positions and public identities.");
            await RequireCurrentNamespaceMoveLineageAsync(snapshot.Context.Member, snapshot.Context.Protection.RoleSid,
                plan.PreparedAt, transaction, token).ConfigureAwait(false);
        }
        var roots = await ReadNamespaceRootFencesCoreAsync(true, transaction, token,
            admittedTransferId: admitted ? plan.OperationId : null,
            admittedTransferBirthId: admitted ? targetBirth?.Birth.OperationId : null).ConfigureAwait(false);
        if (roots.Contains(plan.SourceRoute.RootId) || roots.Contains(plan.TargetRoute.RootId))
            throw new InvalidOperationException("Another original namespace owner prevents this transfer.");
        await RequireNamespaceTransferRoutesAsync(plan, transaction, token).ConfigureAwait(false);
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_member_names WHERE parent_member_id=$parent AND child_name_key=$name;";
        query.Parameters.AddWithValue("$parent", plan.TargetParent.Context.Member.MemberId.ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(plan.TargetRelativePath));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string occupied)
        {
            if (targetBirth is null || targetBirth.OperationId != plan.OperationId || occupied != targetBirth.Birth.OperationId.ToString("D"))
                throw new InvalidOperationException("An existing member owns the transfer destination.");
            var member = await ReadNamespaceMemberCoreAsync(targetBirth.Birth.OperationId, transaction, token).ConfigureAwait(false);
            if (member is null || member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.OriginId != targetBirth.Birth.OperationId ||
                member.ParentMemberId != plan.TargetParent.Context.Member.MemberId || member.RootId != plan.TargetRoute.RootId ||
                member.RelativePath != plan.TargetRelativePath)
                throw new InvalidDataException("The occupied transfer destination lost its exact original target birth.");
        }
    }

    private async Task RequireNamespaceTransferRoutesAsync(MirrorPulseNamespaceTransferPlan plan,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        var payload = await query.ExecuteScalarAsync(token).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("Transfer execution requires its current registered topology.");
        var topology = DeserializeTopology(payload);
        foreach (var route in new[] { plan.SourceRoute, plan.TargetRoute })
        {
            var instance = topology.Instances.SingleOrDefault(item => item.InstanceId == route.InstanceId)
                ?? throw new InvalidOperationException("The original transfer instance is missing.");
            var root = topology.Roots.SingleOrDefault(item => item.RootId == route.RootId)
                ?? throw new InvalidOperationException("The original transfer root is missing.");
            if (MirrorPulseTransferRoute.Capture(instance, root, route.ProtocolVersion) != route ||
                route.ProtocolVersion == 1 && topology.Roots.Count(item => item.InstanceId == route.InstanceId) != 1)
                throw new InvalidOperationException("The transfer backend, version or root configuration changed.");
        }
    }

    private async Task RequireNamespaceTransferIdAvailableAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT 1 FROM namespace_transfers WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_transfer_effects WHERE effect_id=$id
            UNION ALL SELECT 1 FROM namespace_transfer_target_births WHERE birth_id=$id
            UNION ALL SELECT 1 FROM namespace_transfer_journal_members WHERE operation_id=$id
            UNION ALL SELECT 1 FROM retired_transfer_observations WHERE operation_id=$id
            UNION ALL SELECT 1 FROM mutation_intents WHERE operation_id=$id
            UNION ALL SELECT 1 FROM worker_requests WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_moves WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_birth_intents WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_remote_updates WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_retirements WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_retirement_batches WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_replacements WHERE operation_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_plans WHERE plan_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_steps WHERE operation_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_retries WHERE operation_id=$id OR followup_operation_id=$id
            UNION ALL SELECT 1 FROM conflict_deletion_members WHERE original_id=$id
            UNION ALL SELECT 1 FROM conflict_local_removal_members WHERE original_id=$id
            UNION ALL SELECT 1 FROM conflict_keep_both_members WHERE original_id=$id
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An original product owner already retains this transfer or effect ID.");
    }
}
