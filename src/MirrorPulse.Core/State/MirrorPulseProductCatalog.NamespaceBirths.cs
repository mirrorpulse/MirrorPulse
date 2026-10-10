using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespaceBirthOrigin { ControlledCreation, Import, RemotePopulation }

/// <summary>An immutable intent retained before creating an object under an owned protected parent.</summary>
/// <remarks>
/// The child has no native binding or original DACL yet. The parent evidence refers to its
/// pre-protection original; its verified operation records the protection at admission.
/// Neither this intent nor its presence proves that a child was created, protected, accepted
/// remotely or added to a sealed original tree. Native execution requires a separate current
/// parent check and durable birth observation. Never replay creation merely because this exists.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthIntent(int Version, Guid OperationId, RootId RootId,
    Guid ParentEvidenceId, Guid ParentPermissionOperationId, MirrorPulseLocalFileBinding ParentLocalObject,
    string RelativePath, bool IsDirectory, MirrorPulseNamespaceBirthOrigin Origin, string RoleSid,
    DateTimeOffset PreparedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reserves one child name against concurrent birth intents without touching the filesystem.</summary>
    /// <remarks>Exact replay preserves historical admission even after the parent's role has rotated.</remarks>
    public async Task<MirrorPulseNamespaceBirthIntent> PrepareNamespaceBirthAsync(MirrorPulseNamespaceBirthIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateNamespaceBirth(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var retained = await ReadNamespaceBirthCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != intent) throw new InvalidOperationException("An original birth intent is immutable.");
                transaction.Commit();
                return retained;
            }
            var parent = await ValidateNamespaceBirthParentAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            var history = await ReadPermissionChangesCoreAsync(intent.ParentEvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (history.Count == 0 || history[^1].Intent.OperationId != parent.Intent.OperationId)
                throw new InvalidOperationException("Birth admission requires the parent's latest verified protection.");
            var original = await ReadPermissionBaselineCoreAsync(intent.ParentEvidenceId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original parent evidence is missing.");
            await ValidatePermissionTreeAdmissionAsync(original, parent.Intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The birth admission payload is not bounded.", nameof(intent));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_intents(operation_id,parent_evidence_id,parent_operation_id,
                    relative_path_key,payload,fingerprint)
                VALUES($operation,$parent,$protection,$path,$payload,$fingerprint);
                INSERT INTO namespace_birth_reservations(parent_evidence_id,child_name_key,operation_id)
                VALUES($parent,$child,$operation);
                """;
            insert.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$parent", intent.ParentEvidenceId.ToString("D"));
            insert.Parameters.AddWithValue("$protection", intent.ParentPermissionOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$path", intent.RelativePath.ToUpperInvariant());
            insert.Parameters.AddWithValue("$child", intent.RelativePath[(intent.RelativePath.LastIndexOf('/') + 1)..].ToUpperInvariant());
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return intent;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads exact historical birth admission without inventing a child's current binding.</summary>
    public async Task<MirrorPulseNamespaceBirthIntent?> ReadNamespaceBirthAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadNamespaceBirthCoreAsync(operationId, null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthIntent?> ReadNamespaceBirthCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseNamespaceBirthIntent intent;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id,parent_evidence_id,parent_operation_id,relative_path_key,payload,fingerprint
                FROM namespace_birth_intents WHERE operation_id=$operation;
                """;
            query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[4];
            byte[] fingerprint = (byte[])reader[5];
            if (payload.Length > 131_072 || fingerprint.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The birth admission fingerprint is invalid.");
            try
            {
                intent = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthIntent>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The birth admission is empty.");
                ValidateNamespaceBirth(intent);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("The birth admission is invalid.", exception);
            }
            string pathKey = intent.RelativePath.ToUpperInvariant();
            if (intent.OperationId != operationId || intent.OperationId.ToString("D") != reader.GetString(0) ||
                intent.ParentEvidenceId.ToString("D") != reader.GetString(1) ||
                intent.ParentPermissionOperationId.ToString("D") != reader.GetString(2) ||
                pathKey != reader.GetString(3) ||
                !JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The birth admission's index or canonical payload is inconsistent.");
        }
        try { _ = await ValidateNamespaceBirthParentAsync(intent, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidDataException("The historical birth admission lost its original parent protection.", exception);
        }
        return intent;
    }

    private async Task<MirrorPulseNamespacePermissionChange> ValidateNamespaceBirthParentAsync(
        MirrorPulseNamespaceBirthIntent intent, SqliteTransaction? transaction, CancellationToken token)
    {
        var original = await ReadPermissionBaselineCoreAsync(intent.ParentEvidenceId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Original parent permissions are required before birth admission.");
        var parent = await ReadPermissionChangeCoreAsync(intent.ParentPermissionOperationId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Verified parent protection is required before birth admission.");
        string parentPath = parent.Intent.RelativePath;
        int separator = intent.RelativePath.LastIndexOf('/');
        string actualParent = separator < 0 ? string.Empty : intent.RelativePath[..separator];
        if (!original.IsDirectory || original.LocalObject != intent.ParentLocalObject ||
            parent.Intent.EvidenceId != original.EvidenceId || parent.Intent.LocalObject != original.LocalObject ||
            parent.Phase != MirrorPulseNamespacePermissionPhase.Verified ||
            parent.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore ||
            parent.Intent.RoleSid != intent.RoleSid || intent.PreparedAt < parent.Verification!.ObservedAt ||
            !string.Equals(parentPath, actualParent, StringComparison.Ordinal) ||
            (parent.Intent.RootId is null ? !intent.IsDirectory || separator >= 0 : parent.Intent.RootId != intent.RootId))
            throw new InvalidOperationException("Birth admission must name a direct child of its verified protected parent.");
        return parent;
    }

    private static void ValidateNamespaceBirth(MirrorPulseNamespaceBirthIntent intent)
    {
        ValidatePermissionBinding(intent.ParentLocalObject);
        ValidatePermissionSid(intent.RoleSid, role: true);
        // No child binding exists before native creation. Validate the name without fabricating one.
        ValidatePermissionRelativePath(intent.RelativePath);
        if (intent.Version != 1 || intent.OperationId == Guid.Empty || intent.RootId.Value == Guid.Empty ||
            intent.ParentEvidenceId == Guid.Empty || intent.ParentPermissionOperationId == Guid.Empty ||
            intent.PreparedAt == default || !Enum.IsDefined(intent.Origin) ||
            string.IsNullOrWhiteSpace(intent.RelativePath))
            throw new ArgumentException("The birth admission requires one exact relative child location.", nameof(intent));
    }
}
