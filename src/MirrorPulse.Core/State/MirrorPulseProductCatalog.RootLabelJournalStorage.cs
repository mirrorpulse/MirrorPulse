using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static string RootLabelJournalDigest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions)));

    private static bool IsRootLabelJournalDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateRootLabelJournalName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 255 || path != path.Trim() || path.Contains('/') || path.Contains('\\'))
            throw new ArgumentException("A root journal observation requires an actual single managed-root name.");
        ValidatePermissionRelativePath(path);
    }

    private static void ValidateRootLabelJournalObservation(MirrorPulseRootLabelJournalObservation observation)
    {
        if (observation.OperationId == Guid.Empty || observation.Sequence <= 0 || observation.Kind != MirrorPulseWorkerChangeKind.Move ||
            !observation.IsDirectory || observation.ItemId is not { } item || item == Guid.Empty || observation.ObservedAt == default)
            throw new ArgumentException("Root Label journal policy accepts only an assigned exact original directory-move notification.");
        ValidateRootLabelJournalName(observation.RelativePath);
        ValidateRootLabelJournalName(observation.PreviousRelativePath);
    }

    private static void ValidateRootLabelJournalRecord(MirrorPulseRootLabelJournalAcknowledgement value)
    {
        if (value.Version != 1 || value.Witness is not { } witness || value.Observation is null || witness.RootId.Value == Guid.Empty ||
            witness.AnchorMemberId == Guid.Empty || witness.RootMemberId == Guid.Empty || witness.LabelOperationId == Guid.Empty ||
            witness.ProtectionId == Guid.Empty || witness.ItemId == Guid.Empty || witness.LocalObject is null || witness.MemberCount < 2 ||
            witness.NameEpoch <= 0 || string.IsNullOrWhiteSpace(witness.OwnerSid) || string.IsNullOrWhiteSpace(witness.RoleSid) ||
            witness.OwnerSid.Length > 184 || witness.RoleSid.Length > 184 || witness.AuditedAt == default ||
            string.IsNullOrEmpty(witness.PlaceholderIdentity) || witness.PlaceholderIdentity.Length > 65536 ||
            witness.RootObservedAt < witness.AuditedAt || value.StartedAt < witness.RootObservedAt || value.StartedAt < value.Observation.ObservedAt ||
            value.Observation.ItemId != witness.ItemId || value.AcknowledgedAt is { } acknowledged && acknowledged < value.StartedAt ||
            !IsRootLabelJournalDigest(witness.InventoryFingerprint) || !IsRootLabelJournalDigest(witness.LabelHistoryFingerprint) ||
            !IsRootLabelJournalDigest(witness.DirectoryReceiptFingerprint))
            throw new ArgumentException("The original root ACK must retain its complete immutable local-policy witness and ordered return.");
        ValidateRootLabelJournalName(witness.DirectoryName);
        ValidateRootLabelJournalObservation(value.Observation);
    }

    private async Task<MirrorPulseRootLabelJournalAcknowledgement?> ReadRootLabelJournalCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseRootLabelJournalAcknowledgement value;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint,root_id,member_id,label_operation_id,started_utc,status FROM root_label_journal_acknowledgements WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[0], fingerprint = (byte[])reader[1];
            if (payload.Length > 262_144 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The original root ACK fingerprint is invalid.");
            try
            {
                value = JsonSerializer.Deserialize<MirrorPulseRootLabelJournalAcknowledgement>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The original root ACK owner is empty.");
                ValidateRootLabelJournalRecord(value);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { throw new InvalidDataException("The original root ACK owner is invalid.", error); }
            if (value.Observation.OperationId != operationId || reader.GetString(2) != value.Witness.RootId.ToString() ||
                reader.GetString(3) != value.Witness.RootMemberId.ToString("D") || reader.GetString(4) != value.Witness.LabelOperationId.ToString("D") ||
                reader.GetString(5) != value.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ||
                reader.GetInt32(6) != (value.AcknowledgedAt is null ? 0 : 1) ||
                !JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original root ACK indexes disagree with its canonical owner.");
        }
        var witness = value.Witness;
        var history = await ReadManagedRootRenameHistoryCoreAsync(witness.LabelOperationId, token, transaction).ConfigureAwait(false);
        var move = await ReadNamespaceMemberMoveRowAsync(witness.LabelOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root ACK lost its original protected Label move.");
        var completion = await ReadNamespaceDirectoryMoveCompletionCoreAsync(witness.LabelOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root ACK lost its original whole-subtree receipt.");
        var member = await ReadNamespaceMemberRowAsync(witness.RootMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root ACK lost its immutable original member.");
        if (history.Intent.Phase != MirrorPulseRootRenamePhase.Completed || history.Intent.RootId != witness.RootId ||
            history.Proof is not { } proof || proof.ItemId != witness.ItemId || proof.LocalObject != witness.LocalObject || proof.PlaceholderIdentity != witness.PlaceholderIdentity ||
            history.Intent.TargetName != witness.DirectoryName || history.Intent.UpdatedAt != completion.Root.VerifiedAt ||
            witness.AuditedAt < history.Intent.UpdatedAt || RootLabelJournalDigest(history) != witness.LabelHistoryFingerprint ||
            move.Version != 3 || move.RootId != witness.RootId || move.MemberId != witness.RootMemberId || move.ItemId != witness.ItemId ||
            move.Source.ParentMemberId != witness.AnchorMemberId || move.LocalObject != witness.LocalObject ||
            !member.IsDirectory || member.RootId != witness.RootId || member.LocalObject != witness.LocalObject || member.OwnerSid != witness.OwnerSid ||
            await ReadRootLabelDirectoryDigestAsync(witness.LabelOperationId, transaction, token).ConfigureAwait(false) != witness.DirectoryReceiptFingerprint)
            throw new InvalidDataException("The original root ACK no longer matches its immutable Label, member and complete subtree facts.");
        // Later labels, name reuse and role rotation do not rewrite a meaning already
        // accepted before the first public ACK. This read grants no current native authority.
        return value;
    }

    private async Task WriteRootLabelJournalCoreAsync(MirrorPulseRootLabelJournalAcknowledgement value, bool insert,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 262_144) throw new ArgumentException("The complete original root ACK must be bounded.", nameof(value));
        await using var write = _connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = insert
            ? "INSERT INTO root_label_journal_acknowledgements(operation_id,root_id,member_id,label_operation_id,started_utc,status,payload,fingerprint) VALUES($id,$root,$member,$label,$started,$status,$payload,$hash);"
            : "UPDATE root_label_journal_acknowledgements SET status=$status,payload=$payload,fingerprint=$hash WHERE operation_id=$id AND status=0;";
        write.Parameters.AddWithValue("$id", value.Observation.OperationId.ToString("D"));
        write.Parameters.AddWithValue("$status", value.AcknowledgedAt is null ? 0 : 1);
        write.Parameters.AddWithValue("$payload", payload); write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (insert)
        {
            write.Parameters.AddWithValue("$root", value.Witness.RootId.ToString());
            write.Parameters.AddWithValue("$member", value.Witness.RootMemberId.ToString("D"));
            write.Parameters.AddWithValue("$label", value.Witness.LabelOperationId.ToString("D"));
            write.Parameters.AddWithValue("$started", value.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        if (await write.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The original root ACK owner was not retained.");
    }

    private async Task<string> ReadRootLabelDirectoryDigestAsync(Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT fingerprint FROM namespace_directory_move_completions WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is byte[] { Length: 32 } fingerprint
            ? Convert.ToHexString(fingerprint) : throw new InvalidDataException("The original root Label lost its complete subtree fingerprint.");
    }

    private async Task RequireRootLabelJournalTopologyAsync(RootId rootId, string directoryName,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        string? payload = (string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var root = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(root => root.RootId == rootId);
        if (root is null || root.DirectoryName != directoryName || root.Label != directoryName ||
            root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("Root ACK policy requires the same current local Label and enabled or explicitly disabled root.");
    }
}
