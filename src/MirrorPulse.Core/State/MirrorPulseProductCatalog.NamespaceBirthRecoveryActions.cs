using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>An immutable before-write recovery intent for the original staged local file.</summary>
/// <remarks>
/// The original birth, start and ordinary binding remain unchanged. Descriptor targets are
/// owned intent, not evidence that a write occurred. The Host separately derives policy,
/// retains namespace admission, checks the original native object and records actual facts.
/// Preparing an action grants no access, releases no reservation and acknowledges no journal.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthRecoveryAction(int Version, Guid RecoveryId, Guid BirthOperationId,
    Guid? PreviousRecoveryId, MirrorPulseLocalFileBinding LocalObject, string OwnerSid, string RoleSid,
    Guid ParentPermissionOperationId, string ExpectedDacl, string RoleOnlyDacl, string ConvertedDacl, DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceBornParent? BornParent { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceBirthRecoveryAction> PrepareNamespaceBirthRecoveryActionAsync(
        MirrorPulseNamespaceBirthRecoveryAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateNamespaceBirthRecoveryAction(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceBirthRecoveryActionCoreAsync(action.RecoveryId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != action) throw new InvalidOperationException("The original recovery action is immutable.");
                transaction.Commit();
                return replay;
            }
            var birth = await ValidateNamespaceBirthRecoveryActionReferencesAsync(action, null, transaction, cancellationToken).ConfigureAwait(false);
            if (await ReadNamespaceBirthObservationRowAsync(birth.OperationId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A staged-birth recovery action cannot replace an already observed birth.");
            var latest = await ReadLatestNamespaceBirthRecoveryActionCoreAsync(birth.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (action.PreviousRecoveryId != latest?.RecoveryId)
                throw new InvalidOperationException("Recovery must extend the latest original action.");
            _ = await ValidateNamespaceBirthLineageAsync(birth, null, true, RecoveryParentClaim(action), transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(action, TopologyJsonOptions);
            if (payload.Length > 262_144) throw new ArgumentException("The recovery action must be bounded.", nameof(action));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_recovery_actions(recovery_id,birth_operation_id,previous_recovery_id,
                    parent_permission_operation_id,parent_birth_operation_id,parent_birth_protection_id,payload,fingerprint)
                VALUES($id,$birth,$previous,$parent,$born,$epoch,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$id", action.RecoveryId.ToString("D"));
            insert.Parameters.AddWithValue("$birth", action.BirthOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", (object?)action.PreviousRecoveryId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$parent", action.BornParent is null ? action.ParentPermissionOperationId.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$born", (object?)action.BornParent?.BirthOperationId.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$epoch", (object?)action.BornParent?.ProtectionId.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return action;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthRecoveryAction?> ReadNamespaceBirthRecoveryActionAsync(Guid recoveryId,
        CancellationToken cancellationToken = default)
    {
        if (recoveryId == Guid.Empty) throw new ArgumentException("A recovery ID is required.", nameof(recoveryId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthRecoveryActionCoreAsync(recoveryId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthRecoveryAction?> ReadLatestNamespaceBirthRecoveryActionAsync(Guid birthOperationId,
        CancellationToken cancellationToken = default)
    {
        if (birthOperationId == Guid.Empty) throw new ArgumentException("A birth ID is required.", nameof(birthOperationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadLatestNamespaceBirthRecoveryActionCoreAsync(birthOperationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthRecoveryAction?> ReadLatestNamespaceBirthRecoveryActionCoreAsync(Guid birthOperationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT recovery_id FROM namespace_birth_recovery_actions WHERE birth_operation_id=$birth ORDER BY sequence DESC LIMIT 1;";
        query.Parameters.AddWithValue("$birth", birthOperationId.ToString("D"));
        object? id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (id is null) return null;
        if (id is not string text || !Guid.TryParseExact(text, "D", out Guid recoveryId) || text != recoveryId.ToString("D") || recoveryId == Guid.Empty)
            throw new InvalidDataException("The recovery action index is invalid.");
        var action = await ReadNamespaceBirthRecoveryActionCoreAsync(recoveryId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The indexed recovery action is missing.");
        if (action.BirthOperationId != birthOperationId) throw new InvalidDataException("The recovery index belongs to another birth.");
        return action;
    }

    private async Task<MirrorPulseNamespaceBirthRecoveryAction?> ReadNamespaceBirthRecoveryActionCoreAsync(Guid recoveryId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var row = await ReadNamespaceBirthRecoveryActionRowAsync(recoveryId, transaction, token).ConfigureAwait(false);
        if (row is null) return null;
        try { _ = await ValidateNamespaceBirthRecoveryActionReferencesAsync(row.Value.Action, row.Value.Sequence, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The recovery action lost its original references.", exception); }
        return row.Value.Action;
    }

    private async Task<(long Sequence, MirrorPulseNamespaceBirthRecoveryAction Action)?> ReadNamespaceBirthRecoveryActionRowAsync(
        Guid recoveryId, SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT sequence,recovery_id,birth_operation_id,previous_recovery_id,parent_permission_operation_id,
                parent_birth_operation_id,parent_birth_protection_id,payload,fingerprint
            FROM namespace_birth_recovery_actions WHERE recovery_id=$id;
            """;
        query.Parameters.AddWithValue("$id", recoveryId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        long sequence = reader.GetInt64(0);
        byte[] payload = (byte[])reader[7], fingerprint = (byte[])reader[8];
        if (sequence <= 0 || payload.Length > 262_144 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The recovery action fingerprint or sequence is invalid.");
        MirrorPulseNamespaceBirthRecoveryAction action;
        try
        {
            action = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthRecoveryAction>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The recovery action is empty.");
            ValidateNamespaceBirthRecoveryAction(action);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidDataException("The recovery action payload is invalid.", exception); }
        if (action.RecoveryId != recoveryId || reader.GetString(1) != recoveryId.ToString("D") ||
            reader.GetString(2) != action.BirthOperationId.ToString("D") ||
            (reader.IsDBNull(3) ? null : reader.GetString(3)) != action.PreviousRecoveryId?.ToString("D") ||
            (reader.IsDBNull(4) ? null : reader.GetString(4)) != (action.BornParent is null ? action.ParentPermissionOperationId.ToString("D") : null) ||
            (reader.IsDBNull(5) ? null : reader.GetString(5)) != action.BornParent?.BirthOperationId.ToString("D") ||
            (reader.IsDBNull(6) ? null : reader.GetString(6)) != action.BornParent?.ProtectionId.ToString("D") ||
            !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(action, TopologyJsonOptions)))
            throw new InvalidDataException("The recovery action disagrees with its canonical indexes.");
        return (sequence, action);
    }

    private async Task<MirrorPulseNamespaceBirthIntent> ValidateNamespaceBirthRecoveryActionReferencesAsync(
        MirrorPulseNamespaceBirthRecoveryAction action, long? sequence, SqliteTransaction? transaction, CancellationToken token)
    {
        var birth = await ReadNamespaceBirthCoreAsync(action.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth is missing.");
        var original = await ReadNamespaceBirthConversionCoreAsync(action.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Recovery cannot invent the original ordinary binding.");
        var current = action;
        var seen = new HashSet<Guid>();
        while (true)
        {
            if (!seen.Add(current.RecoveryId) || current.BirthOperationId != birth.OperationId ||
                birth.IsDirectory || birth.Origin == MirrorPulseNamespaceBirthOrigin.RemotePopulation ||
                current.LocalObject != original.LocalObject || current.OwnerSid != original.OwnerSid || current.PreparedAt < original.PreparedAt)
                throw new InvalidOperationException("Recovery requires the original staged local file, owner, binding and time.");
            _ = await ValidateNamespaceBirthLineageAsync(birth, null, false, RecoveryParentClaim(current), transaction, token).ConfigureAwait(false);
            if (current.PreviousRecoveryId is not { } previousId)
            {
                if (current.ExpectedDacl != original.BirthDacl)
                    throw new InvalidOperationException("First recovery requires the recorded ordinary birth descriptor.");
                return birth;
            }
            var previous = await ReadNamespaceBirthRecoveryActionRowAsync(previousId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The preceding recovery intent is missing.");
            if (sequence is { } currentSequence && previous.Sequence >= currentSequence ||
                previous.Action.BirthOperationId != birth.OperationId || previous.Action.LocalObject != original.LocalObject ||
                previous.Action.OwnerSid != original.OwnerSid || current.PreparedAt < previous.Action.PreparedAt ||
                !new[] { previous.Action.ExpectedDacl, previous.Action.RoleOnlyDacl, previous.Action.ConvertedDacl }
                    .Any(owned => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(owned, current.ExpectedDacl)))
                throw new InvalidOperationException("Recovery must retain the preceding action and one of its owned descriptor states.");
            current = previous.Action;
            sequence = previous.Sequence;
        }
    }

    private static NamespaceBirthParentClaim RecoveryParentClaim(MirrorPulseNamespaceBirthRecoveryAction action) =>
        new(action.ParentPermissionOperationId, action.BornParent, action.RoleSid, action.PreparedAt);

    private static void ValidateNamespaceBirthRecoveryAction(MirrorPulseNamespaceBirthRecoveryAction action)
    {
        ValidatePermissionBinding(action.LocalObject);
        ValidatePermissionSid(action.OwnerSid, role: false);
        ValidatePermissionSid(action.RoleSid, role: true);
        ValidatePermissionDacl(action.ExpectedDacl);
        ValidatePermissionDacl(action.RoleOnlyDacl);
        ValidatePermissionDacl(action.ConvertedDacl);
        if (action.Version != 1 || action.RecoveryId == Guid.Empty || action.BirthOperationId == Guid.Empty ||
            action.PreviousRecoveryId == Guid.Empty || action.PreviousRecoveryId == action.RecoveryId || action.PreparedAt == default ||
            (action.BornParent is null ? action.ParentPermissionOperationId == Guid.Empty :
                action.ParentPermissionOperationId != Guid.Empty || action.BornParent.BirthOperationId == Guid.Empty || action.BornParent.ProtectionId == Guid.Empty))
            throw new ArgumentException("A recovery action requires its immutable IDs, direct parent, role and bounded descriptors.", nameof(action));
    }

    private static async Task EnsureNamespaceBirthRecoveryActionSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_birth_recovery_actions (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                recovery_id TEXT NOT NULL UNIQUE,
                birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_conversions(operation_id),
                previous_recovery_id TEXT NULL REFERENCES namespace_birth_recovery_actions(recovery_id),
                parent_permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
                parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
                parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                CHECK(previous_recovery_id IS NULL OR previous_recovery_id<>recovery_id),
                CHECK((parent_permission_operation_id IS NOT NULL AND parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL)
                   OR (parent_permission_operation_id IS NULL AND parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
            );
            CREATE INDEX IF NOT EXISTS namespace_birth_recovery_actions_by_birth
                ON namespace_birth_recovery_actions(birth_operation_id,sequence DESC);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
