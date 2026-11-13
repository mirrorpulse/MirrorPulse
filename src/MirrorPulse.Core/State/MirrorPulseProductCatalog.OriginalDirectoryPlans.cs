using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] DirectoryPlanSchema =
    [
        ("original_directory_plans", """
            CREATE TABLE original_directory_plans(
                admission_id TEXT NOT NULL PRIMARY KEY,
                manifest BLOB NOT NULL CHECK(length(manifest) BETWEEN 1 AND 1048576),
                manifest_hash BLOB NOT NULL CHECK(length(manifest_hash)=32))
            """),
        ("original_directory_plan_members", """
            CREATE TABLE original_directory_plan_members(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
                admission_id TEXT NOT NULL REFERENCES original_directory_plans(admission_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 2047),
                owner_hash BLOB NOT NULL CHECK(length(owner_hash)=32),
                receipt BLOB NULL CHECK(length(receipt) BETWEEN 1 AND 1048576),
                receipt_hash BLOB NULL CHECK(length(receipt_hash)=32),
                UNIQUE(admission_id,ordinal),
                CHECK((receipt IS NULL)=(receipt_hash IS NULL)))
            """)
    ];

    private static async Task EnsureOriginalDirectoryPlanSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var (name, sql) in DirectoryPlanSchema)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", name);
            var existing = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 106) throw new InvalidDataException("Schema106 lost its original directory plan ledger.");
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string stored || NormalizeRetiredTransferIndexSql(stored) != NormalizeRetiredTransferIndexSql(sql))
            {
                throw new InvalidDataException("The original directory plan ledger has an unexpected schema.");
            }
        }
        if (originalVersion < 106)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT 1 FROM original_directory_plans UNION ALL SELECT 1 FROM original_directory_plan_members LIMIT 1;";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply sealed original directory plans.");
            command.CommandText = "PRAGMA user_version=106;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public async Task SealOriginalDirectoryPlanAsync(MirrorPulseOriginalDirectoryPlan plan, CancellationToken token = default)
    {
        byte[] payload = EncodeDirectoryRetirementEvidence(plan);
        var frozen = AdapterProtocolJson.Decode<MirrorPulseOriginalDirectoryPlan>(payload);
        frozen.Validate();
        byte[] hash = SHA256.HashData(payload);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            await SealDirectoryPlanCoreAsync(frozen, payload, hash, transaction, token).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task SealDirectoryPlanCoreAsync(MirrorPulseOriginalDirectoryPlan frozen, byte[] payload, byte[] hash,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT manifest_hash FROM original_directory_plans WHERE admission_id=$admission;";
        command.Parameters.AddWithValue("$admission", frozen.AdmissionId.ToString("D"));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is byte[] prior)
        {
            if (!prior.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("The sealed original directory plan changed.");
            var retained = await ReadDirectoryPlanStepCoreAsync(frozen.Steps[^1].Operation.OperationId, transaction, token).ConfigureAwait(false);
            if (retained is null || retained.Plan.AdmissionId != frozen.AdmissionId)
                throw new InvalidDataException("The sealed original directory plan lost its owner.");
            return;
        }

        command.CommandText = "INSERT INTO original_directory_plans(admission_id,manifest,manifest_hash) VALUES($admission,$manifest,$hash);";
        command.Parameters.AddWithValue("$manifest", payload);
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = """
                INSERT INTO original_directory_plan_members(operation_id,admission_id,ordinal,owner_hash)
                VALUES($operation,$admission,$ordinal,$owner) ON CONFLICT(operation_id) DO NOTHING;
                """;
        command.Parameters.AddWithValue("$admission", frozen.AdmissionId.ToString("D"));
        var operationId = command.Parameters.Add("$operation", SqliteType.Text);
        var stepOrdinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var ownerHash = command.Parameters.Add("$owner", SqliteType.Blob);
        for (int ordinal = 0; ordinal < frozen.Steps.Count; ordinal++)
        {
            var step = frozen.Steps[ordinal];
            var owner = await RequireOriginalDirectoryOwnerAsync(frozen.InstanceId.Value, step.OperationKind, step.Operation,
                step.Operation.Preconditions!.ExpectedRevision!, transaction, token).ConfigureAwait(false);
            if (owner.State != MirrorPulseMutationState.Prepared ||
                owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted || owner.AcceptedRevision is not null ||
                owner.Intent.ContentLength is not null || owner.Intent.ContentSha256 is not null || owner.Intent.UploadBinding is not null)
                throw new InvalidOperationException("A started mutation cannot join a new original directory plan.");
            await RequireUnownedRemoteObservationOriginalAsync(step.Operation.OperationId, transaction, token).ConfigureAwait(false);
            operationId.Value = step.Operation.OperationId.ToString("D");
            stepOrdinal.Value = ordinal;
            ownerHash.Value = DirectoryRetirementOwnerHash(owner.Intent);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("An original directory operation already belongs to a sealed plan.");
        }
    }

    /// <summary>Builds a final Worker request from the sealed originals and previously accepted catalog receipts.</summary>
    public async Task<AdapterDirectoryRetirementRequest> ReadOriginalDirectoryPlanRetirementAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var step = await RequireDirectoryPlanStepAsync(operationId, transaction, token).ConfigureAwait(false);
            var request = await BuildDirectoryPlanRetirementAsync(step, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return request;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reserves the original file effect before a typed Worker dispatch.</summary>
    public async Task<MirrorPulseOriginalDirectoryLeaf> StartOriginalDirectoryLeafAsync(Guid operationId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var step = await RequireDirectoryPlanStepAsync(operationId, transaction, token).ConfigureAwait(false);
            var leaf = await BuildDirectoryPlanLeafAsync(step, transaction, token).ConfigureAwait(false);
            if (step.Owner.State == MirrorPulseMutationState.Prepared)
            {
                if (step.Owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted)
                    throw new InvalidOperationException("The directory leaf has an uncertain earlier execution.");
                await SetDirectoryRetirementStateAsync(operationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing,
                    null, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
            return leaf;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists a verified Worker receipt for a previously started leaf operation.</summary>
    public async Task<AdapterOwnedChildEffectReceipt> AcceptOriginalDirectoryLeafAsync(Guid operationId,
        AdapterOwnedChildEffectReceipt receipt, CancellationToken token = default)
    {
        var frozen = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(EncodeDirectoryRetirementEvidence(receipt));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var step = await RequireDirectoryPlanStepAsync(operationId, transaction, token).ConfigureAwait(false);
            var leaf = await BuildDirectoryPlanLeafAsync(step, transaction, token).ConfigureAwait(false);
            RequirePlanReceipt(step, frozen, leaf.RequestFingerprint);
            foreach (var transition in frozen.Parents)
            {
                var predecessor = leaf.ParentPredecessors.SingleOrDefault(value => value.Parent == transition.OriginalParent);
                if (predecessor is not null) AdapterDirectoryEffects.RequirePredecessor(transition, predecessor.Receipt);
                else if (transition.PreviousReceiptFingerprint is not null ||
                    transition.BeforeRevision != transition.OriginalParent.OriginalRevision)
                    throw new InvalidDataException("The original leaf receipt changes its first parent transition.");
            }
            if (step.Receipt is { } accepted)
            {
                if (AdapterDirectoryEffects.Fingerprint(accepted) != AdapterDirectoryEffects.Fingerprint(frozen))
                    throw new InvalidDataException("The accepted original directory leaf receipt changed.");
                transaction.Commit();
                return accepted;
            }
            if (step.Owner.State != MirrorPulseMutationState.Executing)
                throw new InvalidOperationException("The original directory leaf has not started.");
            await StoreDirectoryPlanReceiptAsync(step, frozen, transaction, token).ConfigureAwait(false);
            await SetDirectoryRetirementStateAsync(operationId, MirrorPulseMutationState.Executing,
                MirrorPulseMutationState.RemoteAccepted, frozen.AcceptedRevision, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return frozen;
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> HasTypedDirectoryMutationOwnerAsync(Guid operationId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM original_directory_plan_members WHERE operation_id=$operation
                UNION ALL SELECT 1 FROM original_directory_retirements WHERE operation_id=$operation LIMIT 1;
                """;
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireNotOriginalDirectoryPlanMemberAsync(Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM original_directory_plan_members WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("The original operation belongs to its sealed directory plan.");
    }

    private sealed record DirectoryPlanStep(MirrorPulseOriginalDirectoryPlan Plan, string Fingerprint, int Ordinal,
        MirrorPulseMutationRecord Owner, AdapterOwnedChildEffectReceipt? Receipt)
    {
        public MirrorPulseOriginalDirectoryStep Step => Plan.Steps[Ordinal];
    }

    private async Task<DirectoryPlanStep> RequireDirectoryPlanStepAsync(Guid operationId, SqliteTransaction transaction,
        CancellationToken token) => await ReadDirectoryPlanStepCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original directory operation requires a sealed plan.");

    private async Task<DirectoryPlanStep?> ReadDirectoryPlanStepCoreAsync(Guid operationId, SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.manifest,p.manifest_hash,m.ordinal,m.admission_id
            FROM original_directory_plan_members m JOIN original_directory_plans p USING(admission_id)
            WHERE m.operation_id=$operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        MirrorPulseOriginalDirectoryPlan plan;
        string fingerprint;
        int ordinal;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            var hash = (byte[])reader[1];
            plan = DecodeDirectoryRetirementEvidence<MirrorPulseOriginalDirectoryPlan>((byte[])reader[0], hash);
            fingerprint = Convert.ToHexString(hash);
            ordinal = reader.GetInt32(2);
            plan.Validate();
            if (ordinal < 0 || ordinal >= plan.Steps.Count || plan.Steps[ordinal].Operation.OperationId != operationId ||
                reader.GetString(3) != plan.AdmissionId.ToString("D"))
                throw new InvalidDataException("The original directory plan index changed.");
        }
#if HOST_ATOMIC_DIRECTORY_CANDIDATE
        await RequireNoAtomicDirectoryBatchAsync(plan.AdmissionId, transaction, token).ConfigureAwait(false);
#endif
        return await ReadDirectoryPlanMemberAsync(plan, fingerprint, ordinal, transaction, token).ConfigureAwait(false);
    }

    private async Task<DirectoryPlanStep> ReadDirectoryPlanMemberAsync(MirrorPulseOriginalDirectoryPlan plan, string fingerprint,
        int ordinal, SqliteTransaction transaction, CancellationToken token, MirrorPulseMutationState? atomicOwnerState = null)
    {
        var step = plan.Steps[ordinal];
        var operation = step.Operation;
        var owner = await RequireOriginalDirectoryOwnerAsync(plan.InstanceId.Value, step.OperationKind, operation,
            operation.Preconditions!.ExpectedRevision!, transaction, token).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT owner_hash,receipt,receipt_hash FROM original_directory_plan_members
            WHERE operation_id=$operation AND admission_id=$admission AND ordinal=$ordinal;
            """;
        command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$admission", plan.AdmissionId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        AdapterOwnedChildEffectReceipt? receipt;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) ||
                !DirectoryRetirementOwnerHash(owner.Intent).AsSpan().SequenceEqual((byte[])reader[0]))
                throw new InvalidDataException("The original directory plan member lost its owning mutation.");
            receipt = reader.IsDBNull(1) ? null :
                DecodeDirectoryRetirementEvidence<AdapterOwnedChildEffectReceipt>((byte[])reader[1], (byte[])reader[2]);
        }
        var loaded = new DirectoryPlanStep(plan, fingerprint, ordinal, owner, receipt);
        if (atomicOwnerState is { } required)
        {
            if (receipt is not null || owner.State != required || owner.AcceptedRevision is not null ||
                owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.Started)
                throw new InvalidDataException("The original directory batch and member state disagree.");
            return loaded;
        }
        if (receipt is null)
        {
            if (owner.State is not (MirrorPulseMutationState.Prepared or MirrorPulseMutationState.Executing) ||
                owner.AcceptedRevision is not null ||
                owner.ExecutionEvidence != (owner.State == MirrorPulseMutationState.Prepared
                    ? MirrorPulseMutationExecutionEvidence.NeverStarted : MirrorPulseMutationExecutionEvidence.Started))
                throw new InvalidDataException("The original directory plan member lost its receipt.");
            return loaded;
        }
        if (owner.State != MirrorPulseMutationState.RemoteAccepted || owner.AcceptedRevision != receipt.AcceptedRevision ||
            owner.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.Started)
            throw new InvalidDataException("The original directory plan receipt and owner disagree.");
        if (step.Directory is null)
        {
            RequirePlanReceipt(loaded, receipt, DirectoryPlanLeafFingerprint(loaded));
        }
        else
        {
            var retirement = await ReadOriginalDirectoryRetirementCoreAsync(operation.OperationId, transaction, token).ConfigureAwait(false);
            if (retirement?.Receipt is not { } original || retirement.Request.Retirement.SealedManifestFingerprint != fingerprint ||
                AdapterDirectoryEffects.Fingerprint(original) != AdapterDirectoryEffects.Fingerprint(receipt))
                throw new InvalidDataException("A child directory has no matching retained final retirement receipt.");
        }
        return loaded;
    }

    private async Task RequireDirectoryPlanPrefixAsync(DirectoryPlanStep step, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) FROM original_directory_plan_members
            WHERE admission_id=$admission AND ordinal<$ordinal AND receipt IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$admission", step.Plan.AdmissionId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", step.Ordinal);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not long count || count != step.Ordinal)
            throw new InvalidOperationException("An earlier original directory plan operation is still pending.");
    }

    private async Task StoreDirectoryPlanReceiptAsync(DirectoryPlanStep step, AdapterOwnedChildEffectReceipt receipt,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = EncodeDirectoryRetirementEvidence(receipt);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE original_directory_plan_members SET receipt=$receipt,receipt_hash=$hash
            WHERE admission_id=$admission AND ordinal=$ordinal AND operation_id=$operation AND receipt IS NULL;
            """;
        command.Parameters.AddWithValue("$admission", step.Plan.AdmissionId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", step.Ordinal);
        command.Parameters.AddWithValue("$operation", step.Step.Operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$receipt", payload);
        command.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The original directory plan receipt changed during acceptance.");
    }
}
