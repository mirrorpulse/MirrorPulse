using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Independent native conversion facts for one retained staged recovery action.</summary>
/// <remarks>
/// These historical facts retain the original object and planned public identity. They do not
/// establish current membership, tree readiness, content acceptance or journal acknowledgement.
/// Native policy derivation, fresh independent inspection and namespace admission belong to
/// the Host. An action alone never proves that its descriptor targets were applied.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthRecoveryVerification(int Version, Guid RecoveryId, Guid BirthOperationId,
    MirrorPulseNamespacePermissionVerification Verification, Guid ItemId, string RemoteId, string? RemoteRevision,
    bool IsPlaceholder, bool IsInSync, uint LinkCount);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceBirthRecoveryVerification> RecordNamespaceBirthRecoveryVerificationAsync(
        MirrorPulseNamespaceBirthRecoveryVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ValidateNamespaceBirthRecoveryVerification(verification);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceBirthRecoveryVerificationCoreAsync(verification.RecoveryId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != verification) throw new InvalidOperationException("The original recovery verification is immutable.");
                transaction.Commit();
                return replay;
            }
            var action = await ValidateNamespaceBirthRecoveryVerificationReferencesAsync(verification, transaction, cancellationToken).ConfigureAwait(false);
            var latest = await ReadLatestNamespaceBirthRecoveryActionCoreAsync(action.BirthOperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (latest?.RecoveryId != action.RecoveryId)
                throw new InvalidOperationException("New recovery facts require the latest action.");
            var birth = await ReadNamespaceBirthCoreAsync(action.BirthOperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original birth disappeared during verification.");
            _ = await ValidateNamespaceBirthLineageAsync(birth, null, true,
                RecoveryParentClaim(action) with { ClaimedAt = verification.Verification.ObservedAt }, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(verification, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The recovery verification must be bounded.", nameof(verification));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_recovery_verifications(recovery_id,birth_operation_id,payload,fingerprint)
                VALUES($id,$birth,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$id", verification.RecoveryId.ToString("D"));
            insert.Parameters.AddWithValue("$birth", verification.BirthOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return verification;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthRecoveryVerification?> ReadNamespaceBirthRecoveryVerificationAsync(Guid recoveryId,
        CancellationToken cancellationToken = default)
    {
        if (recoveryId == Guid.Empty) throw new ArgumentException("A recovery ID is required.", nameof(recoveryId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthRecoveryVerificationCoreAsync(recoveryId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthRecoveryVerification?> ReadNamespaceBirthRecoveryVerificationCoreAsync(Guid recoveryId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseNamespaceBirthRecoveryVerification verification;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT recovery_id,birth_operation_id,payload,fingerprint FROM namespace_birth_recovery_verifications WHERE recovery_id=$id;";
            query.Parameters.AddWithValue("$id", recoveryId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[2], fingerprint = (byte[])reader[3];
            if (payload.Length > 131_072 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The recovery verification fingerprint is invalid.");
            try
            {
                verification = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthRecoveryVerification>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The recovery verification is empty.");
                ValidateNamespaceBirthRecoveryVerification(verification);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { throw new InvalidDataException("The recovery verification payload is invalid.", exception); }
            if (verification.RecoveryId != recoveryId || reader.GetString(0) != recoveryId.ToString("D") ||
                reader.GetString(1) != verification.BirthOperationId.ToString("D") ||
                !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(verification, TopologyJsonOptions)))
                throw new InvalidDataException("The recovery verification disagrees with its canonical indexes.");
        }
        try { _ = await ValidateNamespaceBirthRecoveryVerificationReferencesAsync(verification, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The recovery verification lost its original references.", exception); }
        return verification;
    }

    private async Task<MirrorPulseNamespaceBirthRecoveryAction> ValidateNamespaceBirthRecoveryVerificationReferencesAsync(
        MirrorPulseNamespaceBirthRecoveryVerification verification, SqliteTransaction? transaction, CancellationToken token)
    {
        var action = await ReadNamespaceBirthRecoveryActionCoreAsync(verification.RecoveryId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original recovery action is missing.");
        var plan = await ReadNamespaceBirthPlanCoreAsync(action.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        if (verification.BirthOperationId != action.BirthOperationId || verification.Verification.LocalObject != action.LocalObject ||
            verification.Verification.OwnerSid != action.OwnerSid || verification.Verification.ObservedAt < action.PreparedAt ||
            verification.ItemId != plan.ItemId || verification.RemoteId != plan.RemoteId || verification.RemoteRevision != plan.RemoteRevision ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.ConvertedDacl, verification.Verification.Dacl))
            throw new InvalidOperationException("Recovery verification requires the original object, owner, public identity and converted descriptor.");
        return action;
    }

    private static void ValidateNamespaceBirthRecoveryVerification(MirrorPulseNamespaceBirthRecoveryVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification.Verification);
        ValidatePermissionBinding(verification.Verification.LocalObject);
        ValidatePermissionSid(verification.Verification.OwnerSid, role: false);
        ValidatePermissionDacl(verification.Verification.Dacl);
        if (verification.Version != 1 || verification.RecoveryId == Guid.Empty || verification.BirthOperationId == Guid.Empty ||
            verification.ItemId == Guid.Empty || verification.Verification.ObservedAt == default || !verification.IsPlaceholder ||
            verification.IsInSync || verification.LinkCount != 1 || string.IsNullOrEmpty(verification.RemoteId) || verification.RemoteId.Length > 16_384 ||
            verification.RemoteRevision is { Length: > 16_384 })
            throw new ArgumentException("Recovery verification requires one dirty placeholder and bounded original identity facts.", nameof(verification));
    }

    private static async Task EnsureNamespaceBirthRecoveryVerificationSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_birth_recovery_verifications (
                recovery_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_birth_recovery_actions(recovery_id),
                birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_conversions(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
