using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The first actual object observed under a retained birth plan.</summary>
/// <remarks>
/// BirthDacl describes the object after creation under protection. It is not a pre-MirrorPulse
/// baseline or a restoration target. Observation proves neither protection verification,
/// current membership, remote acceptance nor permission to release the name reservation.
/// The Host supplies these facts from a retained native object and the public CfSharp identity.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthObservation(int Version, Guid OperationId,
    RootId RootId, string RelativePath, bool IsDirectory, MirrorPulseLocalFileBinding LocalObject,
    Guid ItemId, string RemoteId, string? RemoteRevision, bool IsPlaceholder, bool IsInSync,
    uint LinkCount, string OwnerSid, string BirthDacl, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains the first native object without inventing original permissions or accepting content.</summary>
    public async Task<MirrorPulseNamespaceBirthObservation> RecordNamespaceBirthObservationAsync(
        MirrorPulseNamespaceBirthObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateNamespaceBirthObservation(observation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            await ValidateNamespaceBirthObservationReferencesAsync(observation, transaction, cancellationToken).ConfigureAwait(false);
            var retained = await ReadNamespaceBirthObservationCoreAsync(observation.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != observation) throw new InvalidOperationException("The first actual birth observation is immutable.");
                transaction.Commit();
                return retained;
            }
            var birth = await ReadNamespaceBirthCoreAsync(observation.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The birth observation lost its original admission.");
            await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand original = _connection.CreateCommand())
            {
                original.Transaction = transaction;
                original.CommandText = """
                    SELECT 1 FROM namespace_permission_baselines
                    WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;
                    """;
                AddNamespaceBirthBindingParameters(original, observation.LocalObject);
                if (await original.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("A birth cannot adopt an object with pre-protection original evidence.");
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(observation, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The birth observation must be bounded.", nameof(observation));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_observations(operation_id,volume_serial,sync_root_file_id,
                    local_file_id,item_id,payload,fingerprint)
                VALUES($operation,$volume,$sync,$file,$item,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$operation", observation.OperationId.ToString("D"));
            AddNamespaceBirthBindingParameters(insert, observation.LocalObject);
            insert.Parameters.AddWithValue("$item", observation.ItemId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return observation;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads historical birth facts; missing observations remain missing after restart.</summary>
    public async Task<MirrorPulseNamespaceBirthObservation?> ReadNamespaceBirthObservationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthObservationCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthObservation?> ReadNamespaceBirthObservationCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseNamespaceBirthObservation observation;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id,volume_serial,sync_root_file_id,local_file_id,item_id,payload,fingerprint
                FROM namespace_birth_observations WHERE operation_id=$operation;
                """;
            query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[5];
            byte[] fingerprint = (byte[])reader[6];
            if (payload.Length is 0 or > 131_072 || fingerprint.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The birth observation fingerprint is invalid.");
            try
            {
                observation = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthObservation>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The birth observation is empty.");
                ValidateNamespaceBirthObservation(observation);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { throw new InvalidDataException("The birth observation is invalid.", exception); }
            if (observation.OperationId != operationId || operationId.ToString("D") != reader.GetString(0) ||
                observation.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(1) ||
                observation.LocalObject.SyncRootFileId.ToString("D") != reader.GetString(2) ||
                observation.LocalObject.LocalFileId.ToString("D") != reader.GetString(3) ||
                observation.ItemId.ToString("D") != reader.GetString(4) ||
                !JsonSerializer.SerializeToUtf8Bytes(observation, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The birth observation's index or canonical payload is inconsistent.");
        }
        try { await ValidateNamespaceBirthObservationReferencesAsync(observation, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The birth observation lost its original references.", exception); }
        return observation;
    }

    private async Task ValidateNamespaceBirthObservationReferencesAsync(MirrorPulseNamespaceBirthObservation observation,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var birth = await ReadNamespaceBirthCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var plan = await ReadNamespaceBirthPlanCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        var start = await ReadNamespaceBirthStartCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth start intent is missing.");
        var parent = await ReadPermissionBaselineCoreAsync(birth.ParentEvidenceId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The birth parent lost its original permission evidence.");
        if (observation.RootId != birth.RootId || observation.RelativePath != birth.RelativePath ||
            observation.IsDirectory != birth.IsDirectory || observation.ItemId != plan.ItemId ||
            observation.RemoteId != plan.RemoteId || observation.RemoteRevision != plan.RemoteRevision ||
            observation.OwnerSid != parent.OwnerSid || observation.ObservedAt < start.StartedAt ||
            observation.LocalObject.VolumeSerialNumber != birth.ParentLocalObject.VolumeSerialNumber ||
            observation.LocalObject.SyncRootFileId != birth.ParentLocalObject.SyncRootFileId ||
            observation.LocalObject.LocalFileId == birth.ParentLocalObject.LocalFileId ||
            observation.LocalObject.LocalFileId == birth.ParentLocalObject.SyncRootFileId ||
            birth.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation && observation.IsInSync)
            throw new InvalidOperationException("The actual birth must match its original parent, location, identity and start without inventing acceptance.");
        var conversion = await ReadNamespaceBirthConversionCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
        if (conversion is not null && (observation.LocalObject != conversion.LocalObject ||
            observation.OwnerSid != conversion.OwnerSid || observation.IsDirectory != conversion.IsDirectory ||
            observation.ObservedAt < conversion.PreparedAt))
            throw new InvalidOperationException("The placeholder must retain the actual object prepared before conversion.");
        await using SqliteCommand another = _connection.CreateCommand();
        another.Transaction = transaction;
        another.CommandText = """
            SELECT 1 FROM namespace_birth_conversions WHERE operation_id<>$operation AND
                volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;
            """;
        another.Parameters.AddWithValue("$operation", observation.OperationId.ToString("D"));
        AddNamespaceBirthBindingParameters(another, observation.LocalObject);
        if (await another.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A birth cannot adopt another operation's prepared ordinary object.");
    }

    private static void ValidateNamespaceBirthObservation(MirrorPulseNamespaceBirthObservation observation)
    {
        ValidatePermissionBinding(observation.LocalObject);
        ValidatePermissionRelativePath(observation.RelativePath);
        ValidatePermissionSid(observation.OwnerSid, role: false);
        ValidatePermissionDacl(observation.BirthDacl);
        if (observation.Version != 1 || observation.OperationId == Guid.Empty || observation.RootId.Value == Guid.Empty ||
            observation.ItemId == Guid.Empty || string.IsNullOrWhiteSpace(observation.RelativePath) ||
            string.IsNullOrWhiteSpace(observation.RemoteId) || observation.RemoteId.Length > 4096 ||
            observation.RemoteRevision is not null && (string.IsNullOrWhiteSpace(observation.RemoteRevision) || observation.RemoteRevision.Length > 4096) ||
            !observation.IsPlaceholder || observation.LinkCount != 1 || observation.ObservedAt == default)
            throw new ArgumentException("A birth observation requires one bounded Cloud Files object and descriptor.", nameof(observation));
    }

    private static void AddNamespaceBirthBindingParameters(SqliteCommand command, MirrorPulseLocalFileBinding binding)
    {
        command.Parameters.AddWithValue("$volume", binding.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sync", binding.SyncRootFileId.ToString("D"));
        command.Parameters.AddWithValue("$file", binding.LocalFileId.ToString("D"));
    }
}
