using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseFileTransferRecovery(Guid SourceAuditId, Guid TargetAuditId, string RoleSid);

internal sealed record MirrorPulseFileTransferRecoveryMembers(MirrorPulseNamespaceMemberProtectionSnapshot Source,
    MirrorPulseNamespaceMemberProtectionSnapshot SourceParent, MirrorPulseNamespaceMemberProtectionSnapshot TargetParent);

public sealed partial class MirrorPulseProductCatalog
{
    internal async Task<Dictionary<RootId, Guid>> ReadFileTransferRecoveryAuditIdsAsync(Guid transferId,
        string roleSid, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var operations = new List<Guid>(2);
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT i.operation_id FROM namespace_initialization_intents i
                    JOIN namespace_initialization_completions c ON c.operation_id=i.operation_id
                    WHERE json_extract(CAST(i.payload AS TEXT),'$.FileTransferOperationId')=$transfer
                      AND json_extract(CAST(c.payload AS TEXT),'$.RoleSid')=$role
                    LIMIT 3;
                    """;
                query.Parameters.AddWithValue("$transfer", transferId.ToString("D"));
                query.Parameters.AddWithValue("$role", roleSid);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            var result = new Dictionary<RootId, Guid>();
            foreach (Guid operation in operations)
            {
                var status = await ReadNamespaceInitializationStatusCoreAsync(operation, transaction, token).ConfigureAwait(false);
                if (status.Intent.FileTransferOperationId != transferId || status.Completion?.RoleSid != roleSid ||
                    status.Completion.ScopeRoot is not { } root || !result.TryAdd(root, operation) || result.Count > 2)
                    throw new InvalidDataException("The original file transfer has inconsistent scoped audit receipts.");
            }
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseFileTransferRecoveryMembers> ReadFileTransferRecoveryMembersAsync(Guid operationId,
        MirrorPulseFileTransferRecovery recovery, CancellationToken token, bool activatingTarget = false)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original file transfer is missing.");
            if (activatingTarget)
                await RequireUnstartedFileTransferTargetAsync(copy, transaction, token).ConfigureAwait(false);
            else if (copy.TargetBirth is not null && copy.TargetRecovery != recovery)
                throw new InvalidOperationException("Target recovery requires its original selected audit receipts.");
            var plan = copy.Plan;
            var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedTransferId: operationId,
                admittedTransferBirthId: copy.TargetBirthOperationId).ConfigureAwait(false);
            if (fences.Contains(plan.SourceRoute.RootId) || fences.Contains(plan.TargetRoute.RootId))
                throw new InvalidOperationException("Another original namespace owner prevents this transfer.");
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token).ConfigureAwait(false);
            var members = await ReadFileTransferRecoveryMembersCoreAsync(plan, recovery, transaction, token,
                activatingTarget ? copy.TargetBirth!.Birth.PreparedAt : null).ConfigureAwait(false);
            transaction.Commit();
            return members;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseFileTransferRecoveryMembers> ReadFileTransferRecoveryMembersCoreAsync(
        MirrorPulseNamespaceTransferPlan plan, MirrorPulseFileTransferRecovery recovery, SqliteTransaction transaction, CancellationToken token,
        DateTimeOffset? publishedAt = null)
    {
        var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(plan, recovery, publishedAt ?? plan.PreparedAt,
            transaction, token).ConfigureAwait(false);
        return new(await RequireAuditedFileTransferMemberAsync(plan.Source, sourceAudit, transaction, token).ConfigureAwait(false),
            await RequireAuditedFileTransferMemberAsync(plan.SourceParent, sourceAudit, transaction, token).ConfigureAwait(false),
            await RequireAuditedFileTransferMemberAsync(plan.TargetParent, targetAudit, transaction, token).ConfigureAwait(false));
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> RequireAuditedFileTransferMemberAsync(
        MirrorPulseNamespaceMemberProtectionSnapshot original, MirrorPulseNamespaceInitializationCompletion audit,
        SqliteTransaction transaction, CancellationToken token)
    {
        var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
        if (current is null || current.Context.Member != original.Context.Member || current.Context.Location != original.Context.Location ||
            current.Identity != original.Identity || current.Context.Protection.RoleSid != audit.RoleSid ||
            current.Context.Protection.VerifiedAt > audit.CompletedAt)
            throw new InvalidOperationException("The current audit must retain each original transfer member, position and public identity.");
        await RequireCurrentNamespaceMoveLineageAsync(current.Context.Member, audit.RoleSid, audit.CompletedAt, transaction, token)
            .ConfigureAwait(false);
        return current;
    }

    private async Task<(MirrorPulseNamespaceInitializationCompletion Source, MirrorPulseNamespaceInitializationCompletion Target)>
        RequireFileTransferAuditsAsync(MirrorPulseNamespaceTransferPlan plan, MirrorPulseFileTransferRecovery recovery,
            DateTimeOffset earliest, SqliteTransaction transaction, CancellationToken token)
    {
        var sourceAudit = await RequireFileTransferAuditAsync(plan.OperationId, recovery.SourceAuditId, plan.SourceRoute.RootId,
            recovery.RoleSid, earliest, transaction, token).ConfigureAwait(false);
        var targetAudit = await RequireFileTransferAuditAsync(plan.OperationId, recovery.TargetAuditId, plan.TargetRoute.RootId,
            recovery.RoleSid, earliest, transaction, token).ConfigureAwait(false);
        if (sourceAudit.AnchorMemberId != targetAudit.AnchorMemberId)
            throw new InvalidOperationException("Both original transfer roots require audits of the same registered anchor.");
        return (sourceAudit, targetAudit);

    }

    private async Task<MirrorPulseNamespaceInitializationCompletion> RequireFileTransferAuditAsync(Guid operationId,
        Guid auditId, RootId rootId, string role, DateTimeOffset earliest, SqliteTransaction transaction, CancellationToken token)
    {
        var status = await ReadNamespaceInitializationStatusCoreAsync(auditId, transaction, token).ConfigureAwait(false);
        if (status.Intent.FileTransferOperationId != operationId ||
            status.Completion is not { } completed || completed.ScopeRoot != rootId ||
            completed.RoleSid != role || status.Intent.PreparedAt < earliest || completed.CompletedAt < earliest)
            throw new InvalidOperationException("File transfer recovery requires its original root audit under the current role.");
        return completed;
    }

    internal async Task<MirrorPulseFileTransferRecoveryMembers> ReadPublishedFileTransferRecoveryMembersAsync(Guid operationId,
        MirrorPulseFileTransferRecovery recovery, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original published file transfer is missing.");
            await RequireCurrentTransferSourceDispositionAsync(copy, transaction, token, recovery).ConfigureAwait(false);
            var members = await ReadFileTransferRecoveryMembersCoreAsync(copy.Plan, recovery, transaction, token,
                copy.TargetPublication!.PublishedAt).ConfigureAwait(false);
            transaction.Commit();
            return members;
        }
        finally { _gate.Release(); }
    }

    internal async Task<(MirrorPulseNamespaceMemberProtectionSnapshot Parent, MirrorPulseNamespaceMemberProtectionSnapshot Target)>
        ReadFileTransferRetirementTargetAsync(Guid operationId, Guid auditId, string role, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            if (copy.TargetPublication is not { } publication || retirement.StartedAt is null ||
                retirement.Phase is not (MirrorPulseNamespaceRetirementPhase.Prepared or MirrorPulseNamespaceRetirementPhase.NativeDeleted))
                throw new InvalidOperationException("Missing-source recovery requires its original pending retirement and published target.");
            var plan = copy.Plan;
            var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedTransferId: operationId,
                admittedTransferBirthId: copy.TargetBirthOperationId).ConfigureAwait(false);
            if (fences.Contains(plan.SourceRoute.RootId) || fences.Contains(plan.TargetRoute.RootId))
                throw new InvalidOperationException("Another original namespace owner prevents transfer retirement.");
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token).ConfigureAwait(false);
            var audit = await RequireFileTransferAuditAsync(operationId, auditId, plan.TargetRoute.RootId, role,
                publication.PublishedAt, transaction, token).ConfigureAwait(false);
            var parent = await RequireAuditedFileTransferMemberAsync(plan.TargetParent, audit, transaction, token).ConfigureAwait(false);
            var target = await ReadNamespaceMemberProtectionSnapshotCoreAsync(publication.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Missing-source recovery lost its protected target.");
            if (target.Context.Member.LocalObject != publication.LocalObject || target.Context.Location.ParentMemberId != parent.Context.Member.MemberId ||
                target.Context.Member.RootId != plan.TargetRoute.RootId || target.Context.Location.RelativePath != plan.TargetRelativePath ||
                target.Identity.ItemId != publication.ItemId || target.Identity.RemoteId != copy.TargetBirth!.Plan.RemoteId ||
                target.Context.Member.IsDirectory)
                throw new InvalidOperationException("Missing-source recovery requires its original protected target identity and placement.");
            await RequireCurrentNamespaceMoveLineageAsync(target.Context.Member, role, audit.CompletedAt, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return (parent, target);
        }
        finally { _gate.Release(); }
    }
}
