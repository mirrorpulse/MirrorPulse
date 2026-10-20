using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Original ownership of one same-object SDK upsert, separate from a new birth or local replacement.</summary>
public sealed record MirrorPulseNamespaceRemoteUpdatePlan(int Version, Guid OperationId, InstanceId InstanceId,
    string BatchId, string BatchFingerprint, int BatchEntryCount, int Ordinal, string ChangeId, string ChangeFingerprint,
    Guid ItemId, string RemoteId, string? PreviousRemoteRevision, string RemoteRevision,
    string? NativeRemoteRevision, string? OfficialRemoteRevision, bool NativeIsPlaceholder,
    MirrorPulseNamespaceMemberProtectionSnapshot Child, MirrorPulseNamespaceMemberProtectionSnapshot Parent,
    MirrorPulseNamespacePermissionVerification ChildBefore, MirrorPulseNamespacePermissionVerification ParentBefore,
    string PackageVersion, string PackageSource, MirrorPulseRemoteBirthPolicy Policy, DateTimeOffset PreparedAt);

public enum MirrorPulseNamespaceRemoteUpdateDisposition { Applied, Conflict }
public enum MirrorPulseNamespaceRemoteUpdateEvidenceSource { SdkReturn, RetainedSdkProgress }

/// <summary>Fresh same-object and public SDK facts; catalog correlation cannot establish native execution.</summary>
public sealed record MirrorPulseNamespaceRemoteUpdateOutcome(int Version, Guid OperationId,
    MirrorPulseNamespaceRemoteUpdateDisposition Disposition, MirrorPulseNamespaceRemoteUpdateEvidenceSource Source,
    Guid? ConflictId, DateTimeOffset? ConflictDetectedAt, string? NativeRemoteRevision, string? OfficialRemoteRevision,
    bool NativeIsPlaceholder, bool NativeIsInSync, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification Parent, DateTimeOffset VerifiedAt);

public sealed record MirrorPulseNamespaceRemoteUpdate(MirrorPulseNamespaceRemoteUpdatePlan Plan,
    DateTimeOffset? StartedAt, MirrorPulseNamespaceRemoteUpdateOutcome? Outcome);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Fences the original root before the SDK can update its existing object or persist its conflict.</summary>
    public async Task<MirrorPulseNamespaceRemoteUpdate> PrepareNamespaceRemoteUpdateAsync(
        MirrorPulseNamespaceRemoteUpdatePlan plan, CancellationToken token = default)
    {
        ValidateRemoteUpdatePlan(plan);
        if (plan.PackageVersion != MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion ||
            plan.PackageSource != MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit)
            throw new InvalidOperationException("New remote updates require the current reviewed SDK provenance.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadRemoteUpdateCoreAsync(plan.OperationId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.Plan != plan) throw new InvalidOperationException("A remote update cannot replace its original entry, object, policy or role.");
                transaction.Commit(); return replay;
            }
            await RequireRemoteUpdateCurrentAsync(plan, transaction, token).ConfigureAwait(false);
            var root = plan.Child.Context.Member.RootId!.Value;
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, token).ConfigureAwait(false)).Contains(root))
                throw new InvalidOperationException("Another namespace owner prevents this remote update.");
            await ValidateNamespaceRemoteEntryCrossKindAsync(plan.OperationId, plan.InstanceId, plan.BatchId, plan.BatchFingerprint,
                plan.BatchEntryCount, plan.Ordinal, plan.ChangeId, 4, transaction, token).ConfigureAwait(false);
            var retained = new MirrorPulseNamespaceRemoteUpdate(plan, null, null);
            await WriteRemoteUpdateCoreAsync(retained, transaction, token, insert: true).ConfigureAwait(false);
            await using var pending = _connection.CreateCommand(); pending.Transaction = transaction;
            pending.CommandText = "INSERT INTO namespace_remote_update_pending(root_id,operation_id) VALUES($root,$id);";
            pending.Parameters.AddWithValue("$root", root.ToString()); pending.Parameters.AddWithValue("$id", plan.OperationId.ToString("D"));
            await pending.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteUpdate?> ReadNamespaceRemoteUpdateAsync(InstanceId instanceId,
        string batchId, int ordinal, CancellationToken token = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, ordinal);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT operation_id FROM namespace_remote_updates WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;";
            query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
            query.Parameters.AddWithValue("$ordinal", ordinal);
            var id = await query.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            var result = id is null ? null : await ReadRemoteUpdateCoreAsync(ParseNamespaceMemberId(id), transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote update entry lost its original owner.");
            if (result is not null && (result.Plan.InstanceId != instanceId || result.Plan.BatchId != batchId || result.Plan.Ordinal != ordinal))
                throw new InvalidDataException("The remote update lookup changed its original entry.");
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseNamespaceRemoteUpdate>> ReadPendingNamespaceRemoteUpdatesAsync(InstanceId instanceId,
        string batchId, CancellationToken token = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, 0);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT u.operation_id FROM namespace_remote_updates u LEFT JOIN namespace_remote_update_completions c ON c.operation_id=u.operation_id
                    WHERE u.instance_id=$instance AND u.batch_id=$batch AND (u.finished<>1 OR c.operation_id IS NULL OR c.owner_fingerprint<>u.fingerprint)
                    ORDER BY u.ordinal LIMIT 65;
                    """;
                query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            if (ids.Count > 64) throw new InvalidDataException("The original batch has too many unresolved remote update owners.");
            var updates = new List<MirrorPulseNamespaceRemoteUpdate>(ids.Count);
            foreach (Guid id in ids)
                updates.Add(await ReadRemoteUpdateCoreAsync(id, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A pending remote update lost its original entry."));
            transaction.Commit(); return updates.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseNamespaceRemoteUpdate>> ReadPendingNamespaceRemoteUpdatesAsync(RootId rootId,
        CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root is required.", nameof(rootId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var pending = await ReadPendingRemoteUpdatesCoreAsync(transaction, token).ConfigureAwait(false);
            var result = pending.Where(update => update.Plan.Child.Context.Member.RootId == rootId).ToArray();
            transaction.Commit(); return Array.AsReadOnly(result);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanResumeNamespaceRemoteUpdateAsync(MirrorPulseNamespaceRemoteUpdatePlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadRemoteUpdateCoreAsync(plan.OperationId, transaction, token).ConfigureAwait(false);
            if (retained?.Plan != plan || retained.Outcome is not null) { transaction.Commit(); return false; }
            var roots = await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedRemoteUpdateId: plan.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !roots.Contains(plan.Child.Context.Member.RootId!.Value);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteUpdate> StartNamespaceRemoteUpdateAsync(Guid operationId,
        DateTimeOffset startedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRemoteUpdateCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote update is missing.");
            if (retained.StartedAt is { } existing)
            {
                if (existing != startedAt) throw new InvalidOperationException("The original SDK update start is immutable.");
                transaction.Commit(); return retained;
            }
            if (startedAt < retained.Plan.PreparedAt || retained.Outcome is not null)
                throw new InvalidOperationException("The SDK update start must follow its pending original admission.");
            await RequireRemoteUpdateCurrentAsync(retained.Plan, transaction, token).ConfigureAwait(false);
            await RequireRemoteUpdateOwnFenceAsync(retained.Plan, transaction, token).ConfigureAwait(false);
            retained = retained with { StartedAt = startedAt };
            await WriteRemoteUpdateCoreAsync(retained, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    /// <remarks>The Host must hold its namespace actor and native object/parent guards, validate the original
    /// public SDK prefix and decoded conflict or exact projection, and inspect both actual protected objects.</remarks>
    public async Task<MirrorPulseNamespaceRemoteUpdate> SettleNamespaceRemoteUpdateAsync(
        MirrorPulseNamespaceRemoteUpdateOutcome outcome, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRemoteUpdateCoreAsync(outcome.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote update is missing.");
            ValidateRemoteUpdateOutcome(retained, outcome);
            if (retained.Outcome is not null)
            {
                if (retained.Outcome != outcome) throw new InvalidOperationException("The remote update outcome is immutable.");
                transaction.Commit(); return retained;
            }
            await RequireRemoteUpdateCurrentAsync(retained.Plan, transaction, token).ConfigureAwait(false);
            await RequireRemoteUpdateOwnFenceAsync(retained.Plan, transaction, token).ConfigureAwait(false);
            retained = retained with { Outcome = outcome };
            await WriteRemoteUpdateCoreAsync(retained, transaction, token).ConfigureAwait(false);
            await using (var completion = _connection.CreateCommand())
            {
                completion.Transaction = transaction;
                completion.CommandText = "INSERT INTO namespace_remote_update_completions(operation_id,owner_fingerprint) VALUES($id,$hash);";
                completion.Parameters.AddWithValue("$id", retained.Plan.OperationId.ToString("D"));
                completion.Parameters.AddWithValue("$hash", System.Security.Cryptography.SHA256.HashData(RemoteUpdatePayload(retained)));
                await completion.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using var release = _connection.CreateCommand(); release.Transaction = transaction;
            release.CommandText = "DELETE FROM namespace_remote_update_pending WHERE root_id=$root AND operation_id=$id;";
            release.Parameters.AddWithValue("$root", retained.Plan.Child.Context.Member.RootId!.Value.ToString());
            release.Parameters.AddWithValue("$id", retained.Plan.OperationId.ToString("D"));
            if (await release.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The remote update lost its original root fence.");
            transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireRemoteUpdateCurrentAsync(MirrorPulseNamespaceRemoteUpdatePlan plan, SqliteTransaction transaction, CancellationToken token)
    {
        if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(plan.Child.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != plan.Child ||
            await ReadNamespaceMemberProtectionSnapshotCoreAsync(plan.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != plan.Parent)
            throw new InvalidOperationException("The original remote update lost its current object, position, identity, protection or parent.");
    }

    private async Task RequireRemoteUpdateOwnFenceAsync(MirrorPulseNamespaceRemoteUpdatePlan plan, SqliteTransaction transaction, CancellationToken token)
    {
        if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedRemoteUpdateId: plan.OperationId).ConfigureAwait(false))
            .Contains(plan.Child.Context.Member.RootId!.Value))
            throw new InvalidOperationException("Another namespace owner prevents original remote update execution or settlement.");
    }
}
