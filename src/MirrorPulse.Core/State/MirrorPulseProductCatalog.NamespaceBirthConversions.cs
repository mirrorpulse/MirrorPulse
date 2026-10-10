using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Actual ordinary-object facts retained before its first Cloud Files conversion.</summary>
/// <remarks>
/// Both descriptors belong to a protected birth, not pre-MirrorPulse original permissions.
/// The expected converted descriptor is an intent, not a verified write or restoration target.
/// This preparation authorizes neither blind conversion replay nor adoption by current path.
/// The Host supplies the native binding, owner and single-link observation under its parent fence.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthConversionPreparation(int Version, Guid OperationId,
    MirrorPulseLocalFileBinding LocalObject, bool IsDirectory, string OwnerSid, string BirthDacl,
    string ExpectedConvertedDacl, uint LinkCount, DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceBirthConversionPreparation> PrepareNamespaceBirthConversionAsync(
        MirrorPulseNamespaceBirthConversionPreparation preparation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ValidateNamespaceBirthConversion(preparation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var birth = await ValidateNamespaceBirthConversionReferencesAsync(preparation, transaction, cancellationToken).ConfigureAwait(false);
            var retained = await ReadNamespaceBirthConversionCoreAsync(preparation.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != preparation) throw new InvalidOperationException("The original ordinary birth preparation is immutable.");
                transaction.Commit();
                return retained;
            }
            await ValidateNamespaceBirthCurrentParentAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand known = _connection.CreateCommand())
            {
                known.Transaction = transaction;
                known.CommandText = """
                    SELECT 1 FROM namespace_permission_baselines
                    WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file
                    UNION ALL
                    SELECT 1 FROM namespace_birth_observations
                    WHERE operation_id=$operation OR
                        (volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file);
                    """;
                AddNamespaceBirthBindingParameters(known, preparation.LocalObject);
                known.Parameters.AddWithValue("$operation", preparation.OperationId.ToString("D"));
                if (await known.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("Conversion preparation cannot adopt an original object or backfill facts after a placeholder observation.");
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(preparation, TopologyJsonOptions);
            if (payload.Length > 262_144) throw new ArgumentException("The conversion preparation must be bounded.", nameof(preparation));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_conversions(operation_id,volume_serial,sync_root_file_id,local_file_id,payload,fingerprint)
                VALUES($operation,$volume,$sync,$file,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$operation", preparation.OperationId.ToString("D"));
            AddNamespaceBirthBindingParameters(insert, preparation.LocalObject);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return preparation;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceBirthConversionPreparation?> ReadNamespaceBirthConversionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthConversionCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthConversionPreparation?> ReadNamespaceBirthConversionCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseNamespaceBirthConversionPreparation preparation;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id,volume_serial,sync_root_file_id,local_file_id,payload,fingerprint
                FROM namespace_birth_conversions WHERE operation_id=$operation;
                """;
            query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[4], fingerprint = (byte[])reader[5];
            if (payload.Length is 0 or > 262_144 || fingerprint.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The ordinary birth preparation fingerprint is invalid.");
            try
            {
                preparation = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthConversionPreparation>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The ordinary birth preparation is empty.");
                ValidateNamespaceBirthConversion(preparation);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { throw new InvalidDataException("The ordinary birth preparation is invalid.", exception); }
            if (preparation.OperationId != operationId || operationId.ToString("D") != reader.GetString(0) ||
                preparation.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(1) ||
                preparation.LocalObject.SyncRootFileId.ToString("D") != reader.GetString(2) ||
                preparation.LocalObject.LocalFileId.ToString("D") != reader.GetString(3) ||
                !JsonSerializer.SerializeToUtf8Bytes(preparation, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The ordinary birth preparation's index or canonical payload is inconsistent.");
        }
        try { _ = await ValidateNamespaceBirthConversionReferencesAsync(preparation, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The ordinary birth preparation lost its original references.", exception); }
        return preparation;
    }

    private async Task<MirrorPulseNamespaceBirthIntent> ValidateNamespaceBirthConversionReferencesAsync(
        MirrorPulseNamespaceBirthConversionPreparation preparation, SqliteTransaction? transaction, CancellationToken token)
    {
        var birth = await ReadNamespaceBirthCoreAsync(preparation.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var start = await ReadNamespaceBirthStartCoreAsync(preparation.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth start is missing.");
        var parent = await ReadPermissionBaselineCoreAsync(birth.ParentEvidenceId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original parent permission evidence is missing.");
        if (birth.Origin == MirrorPulseNamespaceBirthOrigin.RemotePopulation || preparation.IsDirectory != birth.IsDirectory ||
            preparation.OwnerSid != parent.OwnerSid || preparation.PreparedAt < start.StartedAt ||
            preparation.LocalObject.VolumeSerialNumber != birth.ParentLocalObject.VolumeSerialNumber ||
            preparation.LocalObject.SyncRootFileId != birth.ParentLocalObject.SyncRootFileId ||
            preparation.LocalObject.LocalFileId == birth.ParentLocalObject.LocalFileId ||
            preparation.LocalObject.LocalFileId == birth.ParentLocalObject.SyncRootFileId)
            throw new InvalidOperationException("Ordinary birth preparation requires its original local admission, parent, owner, kind and start.");
        return birth;
    }

    private static void ValidateNamespaceBirthConversion(MirrorPulseNamespaceBirthConversionPreparation preparation)
    {
        ValidatePermissionBinding(preparation.LocalObject);
        ValidatePermissionSid(preparation.OwnerSid, role: false);
        ValidatePermissionDacl(preparation.BirthDacl);
        ValidatePermissionDacl(preparation.ExpectedConvertedDacl);
        if (preparation.Version != 1 || preparation.OperationId == Guid.Empty || preparation.LinkCount != 1 || preparation.PreparedAt == default)
            throw new ArgumentException("Ordinary birth preparation requires one actual single-link object and bounded descriptors.", nameof(preparation));
    }
}
