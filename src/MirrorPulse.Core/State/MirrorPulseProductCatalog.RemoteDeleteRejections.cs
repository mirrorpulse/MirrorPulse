using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Owns one original preserving SDK Delete attempt; it grants no native or source mutation authority.</summary>
public sealed record MirrorPulseRemoteDeleteRejectionAdmission(int Version, Guid OperationId,
    InstanceId InstanceId, RootId RootId, string RootKey, string RootDirectoryName, Guid RootMemberId,
    string BatchId, string BatchFingerprint, int BatchEntryCount, int Ordinal, string EntryFingerprint,
    string ChangeId, Guid ItemId, string RelativePath, bool IsDirectory,
    string PackageVersion, string PackageSource, MirrorPulseRemoteBirthPolicy Policy, DateTimeOffset StartedAt);

/// <summary>The actual durable SDK MissingItem conflict, not a successful Delete or source acknowledgement.</summary>
public sealed record MirrorPulseRemoteDeleteRejectionOutcome(Guid OperationId, Guid ConflictId,
    DateTimeOffset ConflictDetectedAt, DateTimeOffset ObservedAt);

public sealed record MirrorPulseRemoteDeleteRejection(MirrorPulseRemoteDeleteRejectionAdmission Admission,
    MirrorPulseRemoteDeleteRejectionOutcome? Outcome);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseRemoteDeleteRejection> PrepareRemoteDeleteRejectionAsync(
        MirrorPulseRemoteDeleteRejectionAdmission original, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original); ValidateRemoteDeleteRejectionAdmission(original);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var existing = await ReadRemoteDeleteRejectionByOriginalCoreAsync(original.InstanceId, original.BatchId,
                original.Ordinal, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Admission != original) throw new InvalidDataException("The preserving Delete attempt changed its original entry or publication.");
                transaction.Commit(); return existing;
            }
            if (await ReadRemoteDeleteRejectionCoreAsync(original.OperationId, transaction, token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A preserving Delete operation ID already belongs to another original entry.");
            await RequireRemoteDeleteRejectionRootAsync(original, current: true, transaction, token).ConfigureAwait(false);
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, token).ConfigureAwait(false)).Contains(original.RootId))
                throw new InvalidOperationException("Another namespace owner prevents the original preserving Delete admission.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(original, TopologyJsonOptions);
            if (payload.Length > RemoteDeleteRejectionPayloadLimit) throw new InvalidDataException("The preserving Delete admission exceeds its bound.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO remote_delete_rejections(operation_id,instance_id,root_id,batch_id,ordinal,change_id,relative_path,admission_payload,admission_fingerprint)
                VALUES($operation,$instance,$root,$batch,$ordinal,$change,$path,$payload,$hash);
                """;
            insert.Parameters.AddWithValue("$operation", original.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$instance", original.InstanceId.ToString());
            insert.Parameters.AddWithValue("$root", original.RootId.ToString());
            insert.Parameters.AddWithValue("$batch", original.BatchId); insert.Parameters.AddWithValue("$ordinal", original.Ordinal);
            insert.Parameters.AddWithValue("$change", original.ChangeId); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$path", original.RelativePath);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false); transaction.Commit(); return new(original, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteDeleteRejection?> ReadRemoteDeleteRejectionAsync(InstanceId instance,
        string batchId, int ordinal, CancellationToken token = default)
    {
        ValidateRemoteBirthLookup(instance, batchId, ordinal);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRemoteDeleteRejectionByOriginalCoreAsync(instance, batchId, ordinal, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseRemoteDeleteRejection>> ReadPendingRemoteDeleteRejectionsAsync(
        RootId rootId, int limit = 2, CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty || limit is < 1 or > 64) throw new ArgumentException("A preserving Delete recovery requires its stable root and bounded page.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = new List<MirrorPulseRemoteDeleteRejection>();
            await foreach (var entry in ReadPendingRemoteDeleteRejectionsCoreAsync(transaction, token, rootId, limit).ConfigureAwait(false))
                result.Add(entry);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanResumeRemoteDeleteRejectionAsync(MirrorPulseRemoteDeleteRejectionAdmission original,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original); ValidateRemoteDeleteRejectionAdmission(original);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadRemoteDeleteRejectionCoreAsync(original.OperationId, transaction, token).ConfigureAwait(false);
            if (retained?.Admission != original || retained.Outcome is not null) { transaction.Commit(); return false; }
            var fenced = await ReadNamespaceRootFencesCoreAsync(true, transaction, token,
                admittedRemoteDeleteRejectionId: original.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !fenced.Contains(original.RootId);
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteDeleteRejection> CompleteRemoteDeleteRejectionAsync(
        MirrorPulseRemoteDeleteRejectionAdmission original, MirrorPulseRemoteDeleteRejectionOutcome outcome,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(outcome);
        ValidateRemoteDeleteRejectionAdmission(original); ValidateRemoteDeleteRejectionOutcome(original, outcome);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRemoteDeleteRejectionCoreAsync(original.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The actual SDK rejection lost its original preserving attempt.");
            if (retained.Admission != original) throw new InvalidDataException("An actual SDK rejection cannot settle another original entry.");
            if (retained.Outcome is not null)
            {
                if (retained.Outcome != outcome) throw new InvalidOperationException("The original SDK rejection outcome is immutable.");
                transaction.Commit(); return retained;
            }
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "SELECT operation_id FROM remote_delete_rejections WHERE conflict_id=$conflict;";
            update.Parameters.AddWithValue("$conflict", outcome.ConflictId.ToString("D"));
            if (await update.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Another original entry already owns this actual SDK conflict.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(outcome, TopologyJsonOptions);
            update.CommandText = """
                UPDATE remote_delete_rejections SET conflict_id=$conflict,outcome_payload=$payload,outcome_fingerprint=$hash
                WHERE operation_id=$operation AND outcome_payload IS NULL;
                """;
            update.Parameters.AddWithValue("$operation", original.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The original preserving attempt lost its actual result slot.");
            transaction.Commit(); return new(original, outcome);
        }
        finally { _gate.Release(); }
    }
}
