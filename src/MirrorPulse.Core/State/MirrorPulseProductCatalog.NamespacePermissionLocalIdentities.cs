using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>One original object's immutable identity, retained before any local conversion.</summary>
/// <remarks>
/// This record supplies no accepted remote revision or synchronization proof. The Host must
/// inspect the retained original under the public CfSharp scope, preserve a known official
/// ItemId and RemoteId, and validate the public identity encoding before preparation. Existing
/// records must be reused across role rotation and restart; they cannot be inferred from a
/// later path or created to repair a historical application that lacks original evidence.
/// </remarks>
public sealed record MirrorPulseNamespacePermissionLocalIdentity(int Version, Guid EvidenceId,
    Guid PermissionOperationId, MirrorPulseLocalFileBinding LocalObject, Guid ItemId, string RemoteId,
    DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains identity once, before first protection's native conversion or ACL write.</summary>
    public async Task<MirrorPulseNamespacePermissionLocalIdentity> PrepareNamespacePermissionLocalIdentityAsync(
        MirrorPulseNamespacePermissionLocalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidatePermissionLocalIdentity(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseNamespacePermissionChange change = await ReadPermissionChangeCoreAsync(
                identity.PermissionOperationId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original permission operation is missing.");
            MirrorPulseNamespacePermissionBaseline original = await ReadPermissionBaselineCoreAsync(
                identity.EvidenceId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original permission evidence is missing.");
            if (original.IsDirectory || identity.LocalObject != original.LocalObject ||
                change.Intent.EvidenceId != original.EvidenceId || identity.PreparedAt < change.Intent.PreparedAt)
                throw new InvalidOperationException("Local identity does not belong to the original file and permission operation.");
            MirrorPulseNamespacePermissionLocalIdentity? retained = await ReadPermissionLocalIdentityCoreAsync(
                identity.EvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != identity) throw new InvalidOperationException("The original local identity is immutable.");
                transaction.Commit();
                return retained;
            }
            IReadOnlyList<MirrorPulseNamespacePermissionChange> history = await ReadPermissionChangesCoreAsync(
                identity.EvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (change.Phase != MirrorPulseNamespacePermissionPhase.Prepared ||
                change.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Protect || history.Count != 1 ||
                history[0].Intent.OperationId != identity.PermissionOperationId)
                throw new InvalidOperationException("Historical permission application cannot invent a missing original local identity.");
            await ValidatePermissionTreeAdmissionAsync(original, change.Intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(identity, TopologyJsonOptions);
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_permission_local_identities(evidence_id,operation_id,item_id,payload,fingerprint)
                VALUES($evidence,$operation,$item,$payload,$fingerprint);
                """;
            insert.Parameters.AddWithValue("$evidence", identity.EvidenceId.ToString("D"));
            insert.Parameters.AddWithValue("$operation", identity.PermissionOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$item", identity.ItemId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return identity;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads exact retained identity; a missing record remains missing on legacy replay.</summary>
    public async Task<MirrorPulseNamespacePermissionLocalIdentity?> ReadNamespacePermissionLocalIdentityAsync(
        Guid evidenceId, CancellationToken cancellationToken = default)
    {
        if (evidenceId == Guid.Empty) throw new ArgumentException("Original evidence is required.", nameof(evidenceId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPermissionLocalIdentityCoreAsync(evidenceId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespacePermissionLocalIdentity?> ReadPermissionLocalIdentityCoreAsync(Guid evidenceId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        byte[] payload;
        Guid operationId;
        MirrorPulseNamespacePermissionLocalIdentity identity;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT evidence_id,operation_id,item_id,payload,fingerprint FROM namespace_permission_local_identities WHERE evidence_id=$evidence;";
            query.Parameters.AddWithValue("$evidence", evidenceId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            payload = (byte[])reader[3];
            byte[] fingerprint = (byte[])reader[4];
            if (payload.Length > 32_768 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The original local identity fingerprint is invalid.");
            try
            {
                identity = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionLocalIdentity>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The original local identity is invalid.");
                ValidatePermissionLocalIdentity(identity);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("The original local identity is invalid.", exception);
            }
            if (identity.EvidenceId != evidenceId || identity.EvidenceId.ToString("D") != reader.GetString(0) ||
                identity.PermissionOperationId.ToString("D") != reader.GetString(1) || identity.ItemId.ToString("D") != reader.GetString(2) ||
                !JsonSerializer.SerializeToUtf8Bytes(identity, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original local identity index or canonical payload is inconsistent.");
            operationId = identity.PermissionOperationId;
        }
        MirrorPulseNamespacePermissionChange change = await ReadPermissionChangeCoreAsync(operationId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original local identity lost its permission operation.");
        MirrorPulseNamespacePermissionBaseline original = await ReadPermissionBaselineCoreAsync(evidenceId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original local identity lost its object evidence.");
        IReadOnlyList<MirrorPulseNamespacePermissionChange> history = await ReadPermissionChangesCoreAsync(
            evidenceId, token, transaction).ConfigureAwait(false);
        if (original.IsDirectory || original.LocalObject != identity.LocalObject || change.Intent.EvidenceId != identity.EvidenceId ||
            change.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Protect || identity.PreparedAt < change.Intent.PreparedAt ||
            history.Count == 0 || history[0].Intent.OperationId != operationId ||
            change.AppliedAt is not null && identity.PreparedAt > change.AppliedAt)
            throw new InvalidDataException("The original local identity does not belong to its retained permission evidence.");
        return identity;
    }

    private static void ValidatePermissionLocalIdentity(MirrorPulseNamespacePermissionLocalIdentity identity)
    {
        ValidatePermissionBinding(identity.LocalObject);
        if (identity.Version != 1 || identity.EvidenceId == Guid.Empty || identity.PermissionOperationId == Guid.Empty ||
            identity.ItemId == Guid.Empty || string.IsNullOrWhiteSpace(identity.RemoteId) || identity.RemoteId.Length > 4096 || identity.PreparedAt == default)
            throw new ArgumentException("The original local identity is incomplete or unsupported.", nameof(identity));
    }
}
