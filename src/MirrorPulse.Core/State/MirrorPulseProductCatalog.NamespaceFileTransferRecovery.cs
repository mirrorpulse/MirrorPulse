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
        MirrorPulseFileTransferRecovery recovery, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original file transfer is missing.");
            if (copy.TargetBirthOperationId is not null)
                throw new InvalidOperationException("Copy recovery precedes the original target birth.");
            var plan = copy.Plan;
            var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedTransferId: operationId).ConfigureAwait(false);
            if (fences.Contains(plan.SourceRoute.RootId) || fences.Contains(plan.TargetRoute.RootId))
                throw new InvalidOperationException("Another original namespace owner prevents this transfer.");
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token).ConfigureAwait(false);
            var members = await ReadFileTransferRecoveryMembersCoreAsync(plan, recovery, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return members;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseFileTransferRecoveryMembers> ReadFileTransferRecoveryMembersCoreAsync(
        MirrorPulseNamespaceTransferPlan plan, MirrorPulseFileTransferRecovery recovery, SqliteTransaction transaction, CancellationToken token)
    {
        var sourceAudit = await RequireAuditAsync(recovery.SourceAuditId, plan.SourceRoute.RootId).ConfigureAwait(false);
        var targetAudit = await RequireAuditAsync(recovery.TargetAuditId, plan.TargetRoute.RootId).ConfigureAwait(false);
        if (sourceAudit.AnchorMemberId != targetAudit.AnchorMemberId)
            throw new InvalidOperationException("Both original transfer roots require audits of the same registered anchor.");
        return new(await RequireMemberAsync(plan.Source, sourceAudit).ConfigureAwait(false),
            await RequireMemberAsync(plan.SourceParent, sourceAudit).ConfigureAwait(false),
            await RequireMemberAsync(plan.TargetParent, targetAudit).ConfigureAwait(false));

        async Task<MirrorPulseNamespaceInitializationCompletion> RequireAuditAsync(Guid auditId, RootId rootId)
        {
            var status = await ReadNamespaceInitializationStatusCoreAsync(auditId, transaction, token).ConfigureAwait(false);
            if (status.Intent.FileTransferOperationId != plan.OperationId ||
                status.Completion is not { } completed || completed.ScopeRoot != rootId ||
                completed.RoleSid != recovery.RoleSid || completed.CompletedAt < plan.PreparedAt)
                throw new InvalidOperationException("File transfer recovery requires both original root audits under its current role.");
            return completed;
        }

        async Task<MirrorPulseNamespaceMemberProtectionSnapshot> RequireMemberAsync(MirrorPulseNamespaceMemberProtectionSnapshot original,
            MirrorPulseNamespaceInitializationCompletion audit)
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
    }
}
