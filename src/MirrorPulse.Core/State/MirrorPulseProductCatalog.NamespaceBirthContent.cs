using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>First independently verified import copy on the original role-only ordinary object.</summary>
/// <remarks>
/// The Host supplies fresh same-object byte, owner, descriptor and link observations after flush.
/// This receipt proves no current bytes, conversion, membership, remote acceptance or journal ACK.
/// </remarks>
public sealed record MirrorPulseNamespaceBirthContentCompletion(int Version, Guid OperationId, Guid RecoveryId,
    MirrorPulseNamespaceImportContent Content, MirrorPulseNamespacePermissionVerification Verification,
    bool IsPlaceholder, uint LinkCount);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceBirthContentCompletion> RecordNamespaceBirthContentCompletionAsync(
        MirrorPulseNamespaceBirthContentCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateNamespaceBirthContentCompletion(completion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var retained = await ReadNamespaceBirthContentCompletionCoreAsync(completion.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != completion) throw new InvalidOperationException("The first import content completion is immutable.");
                transaction.Commit(); return retained;
            }
            _ = await RequireNamespaceBirthRecoveryExecutionCoreAsync(completion.RecoveryId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceBirthContentReferencesAsync(completion, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completion, TopologyJsonOptions);
            if (payload.Length > 262_144) throw new ArgumentException("The import content receipt must be bounded.", nameof(completion));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_birth_content_completions(operation_id,recovery_id,payload,fingerprint) VALUES($id,$recovery,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", completion.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$recovery", completion.RecoveryId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads original copy evidence without authorizing a rewrite of today's object.</summary>
    public async Task<MirrorPulseNamespaceBirthContentCompletion?> ReadNamespaceBirthContentCompletionAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A birth operation ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var completion = await ReadNamespaceBirthContentCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceBirthContentCompletion?> ReadNamespaceBirthContentCompletionCoreAsync(
        Guid operationId, SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseNamespaceBirthContentCompletion completion;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,recovery_id,payload,fingerprint FROM namespace_birth_content_completions WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[2], fingerprint = (byte[])reader[3];
            if (payload.Length > 262_144 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The import content receipt fingerprint is invalid.");
            try
            {
                completion = JsonSerializer.Deserialize<MirrorPulseNamespaceBirthContentCompletion>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The import content receipt is empty.");
                ValidateNamespaceBirthContentCompletion(completion);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { throw new InvalidDataException("The import content receipt is invalid.", error); }
            if (completion.OperationId != operationId || completion.OperationId.ToString("D") != reader.GetString(0) ||
                completion.RecoveryId.ToString("D") != reader.GetString(1) ||
                !JsonSerializer.SerializeToUtf8Bytes(completion, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The import content receipt index or canonical payload is inconsistent.");
        }
        try { await ValidateNamespaceBirthContentReferencesAsync(completion, transaction, token).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The import content receipt lost its original references.", error); }
        return completion;
    }

    private async Task ValidateNamespaceBirthContentReferencesAsync(MirrorPulseNamespaceBirthContentCompletion completion,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var plan = await ReadNamespaceBirthPlanCoreAsync(completion.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original import content plan is missing.");
        var action = await ReadNamespaceBirthRecoveryActionCoreAsync(completion.RecoveryId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The admitted original object and role handoff are missing.");
        if (plan.Content is null || plan.Content != completion.Content || action.BirthOperationId != completion.OperationId ||
            completion.Verification.LocalObject != action.LocalObject || completion.Verification.OwnerSid != action.OwnerSid ||
            completion.Verification.ObservedAt < action.PreparedAt ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, completion.Verification.Dacl))
            throw new InvalidOperationException("Import completion must retain its original input, object and admitted role-only protection after copying.");
    }

    private static void ValidateNamespaceBirthContentCompletion(MirrorPulseNamespaceBirthContentCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion.Content);
        ArgumentNullException.ThrowIfNull(completion.Verification);
        ValidatePermissionBinding(completion.Verification.LocalObject);
        ValidatePermissionSid(completion.Verification.OwnerSid, role: false);
        ValidatePermissionDacl(completion.Verification.Dacl);
        if (completion.Version != 1 || completion.OperationId == Guid.Empty || completion.RecoveryId == Guid.Empty ||
            completion.IsPlaceholder || completion.LinkCount != 1 || completion.Verification.ObservedAt == default ||
            completion.Content.Length < 0 || completion.Content.Sha256 is not { Length: 64 } ||
            completion.Content.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new ArgumentException("Import content completion requires a bounded digest and the original single-link ordinary object.", nameof(completion));
    }

    private static async Task EnsureNamespaceBirthContentSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_birth_content_completions (
                operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_conversions(operation_id),
                recovery_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_recovery_actions(recovery_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
