using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespaceBirthOrigin { ControlledCreation, Import, RemotePopulation, TransferTarget }

/// <summary>The stable direct-parent birth and the separate protection epoch used at admission.</summary>
public sealed record MirrorPulseNamespaceBornParent(Guid BirthOperationId, Guid ProtectionId);

/// <summary>An immutable intent retained before creating an object under an owned protected parent.</summary>
/// <remarks>
/// The child has no native binding or original DACL yet. The parent evidence refers to its
/// pre-protection original, or BornParent names a directory created under protection. Neither
/// a born directory nor its ancestor is substituted for a pre-protection original.
/// Neither this intent nor its presence proves that a child was created, protected, accepted
/// remotely or added to a sealed original tree. Native execution requires a separate current
/// parent check and durable birth observation. Never replay creation merely because this exists.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthIntent(int Version, Guid OperationId, RootId RootId,
    Guid ParentEvidenceId, Guid ParentPermissionOperationId, MirrorPulseLocalFileBinding ParentLocalObject,
    string RelativePath, bool IsDirectory, MirrorPulseNamespaceBirthOrigin Origin, string RoleSid,
    DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceBornParent? BornParent { get; init; }

    /// <summary>Present only on version 3 births owned by an immutable public SDK batch entry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceRemoteBirthReference? RemoteEntry { get; init; }

    /// <summary>Present only on version4 target births atomically owned by an accepted and verified transfer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceTransferBirthReference? TransferEntry { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reserves one child name against concurrent birth intents without touching the filesystem.</summary>
    /// <remarks>Exact replay preserves historical admission even after the parent's role has rotated.</remarks>
    public async Task<MirrorPulseNamespaceBirthIntent> PrepareNamespaceBirthAsync(MirrorPulseNamespaceBirthIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateNamespaceBirth(intent);
        if (intent.TransferEntry is not null)
            throw new InvalidOperationException("A transfer target birth requires its atomic original transfer admission.");
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
            await RequireNotNamespaceTransferOwnerAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceBirthCurrentParentAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceBirthUnoccupiedMemberNameAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            if (intent.RemoteEntry is not null)
                await ValidateNamespaceRemoteBirthParentAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The birth admission payload is not bounded.", nameof(intent));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_birth_intents(operation_id,parent_evidence_id,parent_operation_id,
                    parent_birth_operation_id,parent_birth_protection_id,relative_path_key,payload,fingerprint)
                VALUES($operation,$original,$protection,$born,$epoch,$path,$payload,$fingerprint);
                INSERT INTO namespace_birth_reservations(parent_kind,parent_id,child_name_key,operation_id)
                VALUES($kind,$parent,$child,$operation);
                """;
            insert.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$original", intent.BornParent is null ? intent.ParentEvidenceId.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$protection", intent.BornParent is null ? intent.ParentPermissionOperationId.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$born", (object?)intent.BornParent?.BirthOperationId.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$epoch", (object?)intent.BornParent?.ProtectionId.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$kind", intent.BornParent is null ? 0 : 1);
            insert.Parameters.AddWithValue("$parent", NamespaceBirthParentId(intent).ToString("D"));
            insert.Parameters.AddWithValue("$path", intent.RelativePath.ToUpperInvariant());
            insert.Parameters.AddWithValue("$child", intent.RelativePath[(intent.RelativePath.LastIndexOf('/') + 1)..].ToUpperInvariant());
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertNamespaceRemoteBirthReferenceAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
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
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var birth = await ReadNamespaceBirthCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return birth;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthIntent?> ReadNamespaceBirthCoreAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NamespaceRecoveryReadSnapshot? snapshot = null;
        if (transaction is not null) _namespaceRecoveryReadSnapshots.TryGetValue(transaction, out snapshot);
        if (snapshot?.ValidatedBirths.TryGetValue(operationId, out var retained) == true) return retained;
        var intent = await ReadNamespaceBirthRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (intent is null) return null;
        try { _ = await ValidateNamespaceBirthParentAsync(intent, transaction, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidDataException("The historical birth admission lost its direct parent protection.", exception);
        }
        if (snapshot is not null) snapshot.ValidatedBirths[operationId] = intent;
        return intent;
    }

    private async Task<MirrorPulseNamespaceBirthIntent?> ReadNamespaceBirthRowAsync(Guid operationId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var history = transaction is null ? null : _namespaceHistoryReads.GetOrCreateValue(transaction);
        if (history?.BirthIntentRows.TryGetValue(operationId, out var retained) == true)
        {
            await ValidateNamespaceBirthReferenceIndexesAsync(retained, transaction, token).ConfigureAwait(false);
            return retained;
        }
        MirrorPulseNamespaceBirthIntent intent;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id,parent_evidence_id,parent_operation_id,relative_path_key,payload,fingerprint,
                    parent_birth_operation_id,parent_birth_protection_id
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
                (intent.BornParent is null ? intent.ParentEvidenceId.ToString("D") : null) != (reader.IsDBNull(1) ? null : reader.GetString(1)) ||
                (intent.BornParent is null ? intent.ParentPermissionOperationId.ToString("D") : null) != (reader.IsDBNull(2) ? null : reader.GetString(2)) ||
                intent.BornParent?.BirthOperationId.ToString("D") != (reader.IsDBNull(6) ? null : reader.GetString(6)) ||
                intent.BornParent?.ProtectionId.ToString("D") != (reader.IsDBNull(7) ? null : reader.GetString(7)) ||
                pathKey != reader.GetString(3) ||
                !JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The birth admission's index or canonical payload is inconsistent.");
        }
        await ValidateNamespaceBirthReferenceIndexesAsync(intent, transaction, token).ConfigureAwait(false);
        history?.BirthIntentRows.Add(operationId, intent);
        return intent;
    }

    private Task<NamespaceBirthParent> ValidateNamespaceBirthParentAsync(
        MirrorPulseNamespaceBirthIntent intent, SqliteTransaction? transaction, CancellationToken token)
        => ValidateNamespaceBirthLineageAsync(intent, null, false, transaction, token);

    private static void ValidateNamespaceBirth(MirrorPulseNamespaceBirthIntent intent)
    {
        ValidatePermissionBinding(intent.ParentLocalObject);
        ValidatePermissionSid(intent.RoleSid, role: true);
        // No child binding exists before native creation. Validate the name without fabricating one.
        ValidatePermissionRelativePath(intent.RelativePath);
        if (!((intent.Version is 1 or 3 or 4) && intent.BornParent is null && intent.ParentEvidenceId != Guid.Empty && intent.ParentPermissionOperationId != Guid.Empty ||
            (intent.Version is 2 or 3 or 4) && intent.ParentEvidenceId == Guid.Empty && intent.ParentPermissionOperationId == Guid.Empty &&
            intent.BornParent is { BirthOperationId: var parent, ProtectionId: var epoch } && parent != Guid.Empty && epoch != Guid.Empty && parent != intent.OperationId) ||
            intent.OperationId == Guid.Empty || intent.RootId.Value == Guid.Empty ||
            intent.PreparedAt == default || !Enum.IsDefined(intent.Origin) ||
            string.IsNullOrWhiteSpace(intent.RelativePath))
            throw new ArgumentException("The birth admission requires one exact relative child location.", nameof(intent));
        if ((intent.Version == 3) != (intent.RemoteEntry is not null))
            throw new ArgumentException("A remote birth requires its explicit versioned original batch owner.", nameof(intent));
        if (intent.RemoteEntry is not null) ValidateNamespaceRemoteBirthReference(intent);
        if ((intent.Version == 4) != (intent.TransferEntry is not null) ||
            (intent.Origin == MirrorPulseNamespaceBirthOrigin.TransferTarget) != (intent.TransferEntry is not null) ||
            intent.TransferEntry is { } transfer && (transfer.Version != 1 || transfer.OperationId == Guid.Empty || transfer.OperationId == intent.OperationId ||
                transfer.TargetVerificationSha256 is not { Length: 64 } || transfer.TargetVerificationSha256.Any(character => !char.IsAsciiHexDigitUpper(character)) || intent.IsDirectory))
            throw new ArgumentException("A transfer target requires its explicit original verified-copy owner.", nameof(intent));
    }

    private async Task ValidateNamespaceBirthCurrentParentAsync(MirrorPulseNamespaceBirthIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        _ = await ValidateNamespaceBirthLineageAsync(intent, null, true, transaction, token).ConfigureAwait(false);
    }

    private static Guid NamespaceBirthParentId(MirrorPulseNamespaceBirthIntent birth) => birth.BornParent?.BirthOperationId ?? birth.ParentEvidenceId;
}
