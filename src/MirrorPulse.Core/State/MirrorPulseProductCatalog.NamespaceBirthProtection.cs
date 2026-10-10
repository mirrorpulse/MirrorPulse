using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>One independently observed protection epoch of the original born object.</summary>
/// <remarks>
/// This retains the actual binding, owner and descriptor without inventing pre-protection
/// original permissions. The Host verifies the current parent, public Cloud Files identity,
/// inherited policy and native object before recording it. Historical verification does not
/// authorize an operation, release a reservation, acknowledge content or prove tree readiness.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthProtection(int Version, Guid ProtectionId, Guid BirthOperationId,
    Guid? PreviousProtectionId, Guid ParentPermissionOperationId, string RoleSid,
    MirrorPulseNamespacePermissionVerification Verification, bool IsPlaceholder, uint LinkCount);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Appends verified protection without replacing the first birth or an earlier epoch.</summary>
    public async Task<MirrorPulseNamespaceBirthProtection> RecordNamespaceBirthProtectionAsync(
        MirrorPulseNamespaceBirthProtection protection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ValidateNamespaceBirthProtection(protection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceBirthProtectionCoreAsync(protection.ProtectionId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != protection) throw new InvalidOperationException("A birth protection epoch is immutable.");
                transaction.Commit();
                return replay;
            }
            var birth = await ValidateNamespaceBirthProtectionReferencesAsync(protection, transaction, cancellationToken).ConfigureAwait(false);
            var latest = await ReadLatestNamespaceBirthProtectionCoreAsync(protection.BirthOperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (protection.PreviousProtectionId != latest?.ProtectionId ||
                latest is not null && protection.Verification.ObservedAt < latest.Verification.ObservedAt)
                throw new InvalidOperationException("Birth protection must extend the latest retained epoch.");
            if (latest is null) await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            var parent = await ReadPermissionChangeCoreAsync(protection.ParentPermissionOperationId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The birth protection parent is missing.");
            var currentParent = await ReadLatestPermissionChangeCoreAsync(birth.ParentEvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (currentParent?.Intent.OperationId != parent.Intent.OperationId)
                throw new InvalidOperationException("Birth protection requires the parent's latest verified protection.");
            var original = await ReadPermissionBaselineCoreAsync(birth.ParentEvidenceId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original parent evidence is missing.");
            await ValidatePermissionTreeAdmissionAsync(original, parent.Intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(protection, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("Birth protection must be bounded.", nameof(protection));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_protections(protection_id,birth_operation_id,previous_protection_id,
                    parent_permission_operation_id,payload,fingerprint)
                VALUES($id,$birth,$previous,$parent,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$id", protection.ProtectionId.ToString("D"));
            insert.Parameters.AddWithValue("$birth", protection.BirthOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", (object?)protection.PreviousProtectionId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$parent", protection.ParentPermissionOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return protection;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the latest historical epoch, not a fresh native protection check.</summary>
    public async Task<MirrorPulseNamespaceBirthProtection?> ReadLatestNamespaceBirthProtectionAsync(Guid birthOperationId,
        CancellationToken cancellationToken = default)
    {
        if (birthOperationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(birthOperationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadLatestNamespaceBirthProtectionCoreAsync(birthOperationId, null, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthProtection?> ReadLatestNamespaceBirthProtectionCoreAsync(Guid birthOperationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT protection_id FROM namespace_birth_protections WHERE birth_operation_id=$birth ORDER BY sequence DESC LIMIT 1;";
        query.Parameters.AddWithValue("$birth", birthOperationId.ToString("D"));
        object? id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (id is null) return null;
        if (id is not string value || !Guid.TryParseExact(value, "D", out Guid protectionId) || protectionId == Guid.Empty)
            throw new InvalidDataException("The latest birth protection index is invalid.");
        var protection = await ReadNamespaceBirthProtectionCoreAsync(protectionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The latest birth protection is missing.");
        if (protection.BirthOperationId != birthOperationId)
            throw new InvalidDataException("The latest birth protection belongs to another birth.");
        return protection;
    }

    private async Task<MirrorPulseNamespaceBirthProtection?> ReadNamespaceBirthProtectionCoreAsync(Guid protectionId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var row = await ReadNamespaceBirthProtectionRowAsync(protectionId, transaction, token).ConfigureAwait(false);
        if (row is null) return null;
        var protection = row.Value.Protection;
        try
        {
            _ = await ValidateNamespaceBirthProtectionReferencesAsync(protection, transaction, token).ConfigureAwait(false);
            if (protection.PreviousProtectionId is { } previousId)
            {
                var previous = await ReadNamespaceBirthProtectionRowAsync(previousId, transaction, token).ConfigureAwait(false);
                if (previous is null || previous.Value.Sequence >= row.Value.Sequence ||
                    previous.Value.Protection.BirthOperationId != protection.BirthOperationId ||
                    previous.Value.Protection.Verification.ObservedAt > protection.Verification.ObservedAt)
                    throw new InvalidDataException("The birth protection predecessor is inconsistent.");
                _ = await ValidateNamespaceBirthProtectionReferencesAsync(previous.Value.Protection, transaction, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("The historical birth protection lost its original references.", error);
        }
        return protection;
    }

    private async Task<(long Sequence, MirrorPulseNamespaceBirthProtection Protection)?> ReadNamespaceBirthProtectionRowAsync(
        Guid protectionId, SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT sequence,protection_id,birth_operation_id,previous_protection_id,parent_permission_operation_id,payload,fingerprint
            FROM namespace_birth_protections WHERE protection_id=$id;
            """;
        query.Parameters.AddWithValue("$id", protectionId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[5], fingerprint = (byte[])reader[6];
        if (payload.Length > 131_072 || fingerprint.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The birth protection fingerprint is invalid.");
        MirrorPulseNamespaceBirthProtection protection;
        try
        {
            protection = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthProtection>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The birth protection is empty.");
            ValidateNamespaceBirthProtection(protection);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The birth protection payload is invalid.", error);
        }
        if (reader.GetInt64(0) <= 0 || protection.ProtectionId != protectionId ||
            reader.GetString(1) != protectionId.ToString("D") || reader.GetString(2) != protection.BirthOperationId.ToString("D") ||
            (reader.IsDBNull(3) ? null : reader.GetString(3)) != protection.PreviousProtectionId?.ToString("D") ||
            reader.GetString(4) != protection.ParentPermissionOperationId.ToString("D") ||
            !JsonSerializer.SerializeToUtf8Bytes(protection, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The birth protection index or canonical payload is inconsistent.");
        return (reader.GetInt64(0), protection);
    }

    private async Task<MirrorPulseNamespaceBirthIntent> ValidateNamespaceBirthProtectionReferencesAsync(
        MirrorPulseNamespaceBirthProtection protection, SqliteTransaction? transaction, CancellationToken token)
    {
        var observation = await ReadNamespaceBirthObservationCoreAsync(protection.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("An original native birth observation is required before protection verification.");
        var birth = await ReadNamespaceBirthCoreAsync(protection.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original birth admission is missing.");
        var parent = await ReadPermissionChangeCoreAsync(protection.ParentPermissionOperationId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Verified direct-parent protection is required.");
        int separator = birth.RelativePath.LastIndexOf('/');
        string parentPath = separator < 0 ? string.Empty : birth.RelativePath[..separator];
        if (protection.Verification.LocalObject != observation.LocalObject || protection.Verification.OwnerSid != observation.OwnerSid ||
            protection.Verification.ObservedAt < observation.ObservedAt || parent.Intent.EvidenceId != birth.ParentEvidenceId ||
            parent.Intent.LocalObject != birth.ParentLocalObject || parent.Phase != MirrorPulseNamespacePermissionPhase.Verified ||
            parent.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore || protection.RoleSid != parent.Intent.RoleSid ||
            protection.Verification.ObservedAt < parent.Verification!.ObservedAt ||
            (parent.Intent.RootId is null ? !birth.IsDirectory : parent.Intent.RootId != birth.RootId) ||
            parent.Intent.RelativePath != parentPath)
            throw new InvalidOperationException("Birth protection must retain the original child and its exact verified parent.");
        return birth;
    }

    private static void ValidateNamespaceBirthProtection(MirrorPulseNamespaceBirthProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection.Verification);
        ValidatePermissionBinding(protection.Verification.LocalObject);
        ValidatePermissionSid(protection.Verification.OwnerSid, role: false);
        ValidatePermissionSid(protection.RoleSid, role: true);
        ValidatePermissionDacl(protection.Verification.Dacl);
        if (protection.Version != 1 || protection.ProtectionId == Guid.Empty || protection.BirthOperationId == Guid.Empty ||
            protection.ParentPermissionOperationId == Guid.Empty || protection.PreviousProtectionId == Guid.Empty ||
            protection.PreviousProtectionId == protection.ProtectionId || protection.Verification.ObservedAt == default ||
            !protection.IsPlaceholder || protection.LinkCount != 1)
            throw new ArgumentException("Birth protection requires an original, single-link placeholder and complete verification.", nameof(protection));
    }
}
