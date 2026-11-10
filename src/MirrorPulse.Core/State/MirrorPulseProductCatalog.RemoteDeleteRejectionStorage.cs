using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const int RemoteDeleteRejectionPayloadLimit = 1_048_576;

    private async Task<MirrorPulseRemoteDeleteRejection?> ReadRemoteDeleteRejectionByOriginalCoreAsync(InstanceId instance,
        string batch, int ordinal, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id FROM remote_delete_rejections WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;";
        query.Parameters.AddWithValue("$instance", instance.ToString()); query.Parameters.AddWithValue("$batch", batch);
        query.Parameters.AddWithValue("$ordinal", ordinal);
        object? id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (id is null) return null;
        var result = await ReadRemoteDeleteRejectionCoreAsync(ParseNamespaceMemberId((string)id), transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The indexed preserving Delete attempt is missing.");
        if (result.Admission.InstanceId != instance || result.Admission.BatchId != batch || result.Admission.Ordinal != ordinal)
            throw new InvalidDataException("The preserving Delete original-entry index changed.");
        return result;
    }

    private async Task<MirrorPulseRemoteDeleteRejection?> ReadRemoteDeleteRejectionCoreAsync(Guid operation,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseRemoteDeleteRejectionAdmission admission; MirrorPulseRemoteDeleteRejectionOutcome? outcome = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = """
                SELECT operation_id,instance_id,root_id,batch_id,ordinal,change_id,relative_path,admission_payload,admission_fingerprint,
                    conflict_id,outcome_payload,outcome_fingerprint FROM remote_delete_rejections WHERE operation_id=$operation;
                """;
            query.Parameters.AddWithValue("$operation", operation.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            admission = ReadNamespaceMovePayload<MirrorPulseRemoteDeleteRejectionAdmission>((byte[])reader[7], (byte[])reader[8],
                RemoteDeleteRejectionPayloadLimit, ValidateRemoteDeleteRejectionAdmission);
            if (admission.OperationId != operation || reader.GetString(0) != operation.ToString("D") ||
                reader.GetString(1) != admission.InstanceId.ToString() || reader.GetString(2) != admission.RootId.ToString() ||
                reader.GetString(3) != admission.BatchId || reader.GetInt64(4) != admission.Ordinal || reader.GetString(5) != admission.ChangeId ||
                reader.GetString(6) != admission.RelativePath || reader.IsDBNull(9) != reader.IsDBNull(10) || reader.IsDBNull(10) != reader.IsDBNull(11))
                throw new InvalidDataException("The preserving Delete indexes disagree with their original immutable evidence.");
            if (!reader.IsDBNull(10))
            {
                outcome = ReadNamespaceMovePayload<MirrorPulseRemoteDeleteRejectionOutcome>((byte[])reader[10], (byte[])reader[11],
                    4096, value => ValidateRemoteDeleteRejectionOutcome(admission, value));
                if (reader.GetString(9) != outcome.ConflictId.ToString("D"))
                    throw new InvalidDataException("The actual SDK conflict index changed its original owner.");
            }
        }
        await RequireRemoteDeleteRejectionRootAsync(admission, current: false, transaction, token).ConfigureAwait(false);
        return new(admission, outcome);
    }

    private async IAsyncEnumerable<MirrorPulseRemoteDeleteRejection> ReadPendingRemoteDeleteRejectionsCoreAsync(
        SqliteTransaction transaction, [EnumeratorCancellation] CancellationToken token, RootId? rootId = null, int limit = 4097)
    {
        var ids = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = rootId is null
                ? "SELECT operation_id FROM remote_delete_rejections WHERE outcome_payload IS NULL ORDER BY root_id,operation_id LIMIT $limit;"
                : "SELECT operation_id FROM remote_delete_rejections WHERE root_id=$root AND outcome_payload IS NULL ORDER BY operation_id LIMIT $limit;";
            if (rootId is not null) query.Parameters.AddWithValue("$root", rootId.Value.ToString());
            query.Parameters.AddWithValue("$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (ids.Count > 4096) throw new InvalidDataException("Pending preserving Delete discovery exceeds its recovery bound.");
        foreach (Guid id in ids)
        {
            var value = await ReadRemoteDeleteRejectionCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A pending preserving Delete lost its original immutable attempt.");
            if (value.Outcome is not null || rootId is not null && value.Admission.RootId != rootId)
                throw new InvalidDataException("Pending preserving Delete discovery changed its original root or result.");
            yield return value;
        }
    }

    private async Task RequireRemoteDeleteRejectionRootAsync(MirrorPulseRemoteDeleteRejectionAdmission original,
        bool current, SqliteTransaction transaction, CancellationToken token)
    {
        var root = await ReadNamespaceMemberCoreAsync(original.RootMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The preserving Delete lost its original registered root member.");
        if (!root.IsDirectory || root.RootId != original.RootId || root.ParentMemberId is null || original.StartedAt < root.RegisteredAt)
            throw new InvalidDataException("The preserving Delete cannot borrow another root, anchor or membership generation.");
        if (current && (await ReadNamespaceMemberLocationCoreAsync(root, transaction, token).ConfigureAwait(false)).RelativePath != original.RootDirectoryName)
            throw new InvalidDataException("The preserving Delete requires its actual current root mapping before admission.");
    }

    private async Task RequirePreservingDeleteResultBeforeDecisionAsync(MirrorPulseConflictRecord conflict,
        SqliteTransaction transaction, CancellationToken token)
    {
        var ids = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = """
                SELECT operation_id FROM remote_delete_rejections
                WHERE instance_id=$instance AND change_id=$change AND relative_path=$path AND outcome_payload IS NULL
                ORDER BY operation_id LIMIT 2;
                """;
            query.Parameters.AddWithValue("$instance", conflict.InstanceId.ToString());
            query.Parameters.AddWithValue("$change", conflict.ChangeId); query.Parameters.AddWithValue("$path", conflict.RelativePath.Replace('\\', '/'));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (ids.Count > 1) throw new InvalidDataException("A conflict has ambiguous pending original preserving Delete owners.");
        foreach (Guid id in ids)
        {
            var original = await ReadRemoteDeleteRejectionCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The preserving Delete lost its pending decision gate.");
            if (original.Outcome is not null) throw new InvalidDataException("The pending preserving Delete decision index changed.");
            if (conflict.DetectedAt >= original.Admission.StartedAt)
                throw new InvalidOperationException("The original preserving Delete must retain its actual SDK result before a conflict decision.");
        }
    }

    private static void ValidateRemoteDeleteRejectionAdmission(MirrorPulseRemoteDeleteRejectionAdmission value)
    {
        ValidateRemoteBirthLookup(value.InstanceId, value.BatchId, value.Ordinal);
        static bool Digest(string value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (value.Version != 1 || value.OperationId == Guid.Empty || value.RootId.Value == Guid.Empty || value.RootMemberId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.RootKey) || value.RootKey.Length > 128 || string.IsNullOrWhiteSpace(value.RootDirectoryName) ||
            value.RootDirectoryName.Length > 255 || value.RootDirectoryName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
            value.RootDirectoryName is "." or ".." || value.BatchEntryCount is < 1 or > 65_536 || value.Ordinal >= value.BatchEntryCount ||
            !Digest(value.BatchFingerprint) || !Digest(value.EntryFingerprint) || string.IsNullOrWhiteSpace(value.ChangeId) || value.ChangeId.Length > 65_536 ||
            value.ItemId == Guid.Empty || value.StartedAt == default || value.RelativePath is null || value.RelativePath.Length > 32_768 ||
            !value.RelativePath.StartsWith(value.RootDirectoryName + "/", StringComparison.Ordinal) ||
            value.RelativePath.IndexOfAny(['\\', ':', '\0']) >= 0 || value.RelativePath.Split('/').Any(part => part is "" or "." or "..") ||
            !MirrorPulseRemoteLibraryProvenance.SupportsPreservingDeleteRejection(value.PackageVersion, value.PackageSource) ||
            value.Policy is null || !value.Policy.PreserveUnsynchronizedLocalContent || value.Policy.EchoSuppressionObservationCount is < 1 or > 64 ||
            value.Policy.EchoSuppressionLifetimeTicks <= 0 || value.Policy.EchoSuppressionLifetimeTicks > TimeSpan.TicksPerHour)
            throw new ArgumentException("A non-mutating Delete attempt requires its complete original entry, stable root and qualified preserving policy.");
    }

    private static void ValidateRemoteDeleteRejectionOutcome(MirrorPulseRemoteDeleteRejectionAdmission original,
        MirrorPulseRemoteDeleteRejectionOutcome value)
    {
        if (value.OperationId != original.OperationId || value.ConflictId == Guid.Empty ||
            value.ConflictDetectedAt < original.StartedAt || value.ObservedAt < value.ConflictDetectedAt || value.ObservedAt == default)
            throw new ArgumentException("An SDK rejection requires its actual original conflict and observed-result order.");
    }
}
