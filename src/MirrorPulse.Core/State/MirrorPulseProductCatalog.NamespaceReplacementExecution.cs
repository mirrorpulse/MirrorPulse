using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Before-write permission intent for the admitted existing object; it proves no native change.</summary>
public sealed record MirrorPulseNamespaceReplacementAction(int Version, Guid OperationId, Guid ActionId,
    MirrorPulseLocalFileBinding LocalObject, string OwnerSid, string RoleSid,
    string RoleOnlyDacl, string RestoreDacl, DateTimeOffset PreparedAt);

/// <summary>First flushed and independently reread old-content backup under exclusive original-object protection.</summary>
/// <remarks>The Host retains the actual original handle and backup lease. This receipt cannot prove current bytes.</remarks>
public sealed record MirrorPulseNamespaceReplacementBackup(int Version, Guid OperationId, Guid ActionId,
    MirrorPulseNamespaceImportContent Content, MirrorPulseNamespacePermissionVerification Verification, uint LinkCount);

/// <summary>Durable intent committed after the old backup receipt and before changing the original object's bytes.</summary>
public sealed record MirrorPulseNamespaceReplacementWriteStart(int Version, Guid OperationId, Guid ActionId, DateTimeOffset StartedAt);

/// <summary>Historical execution facts from one short catalog transaction; this read grants no native authority.</summary>
public sealed record MirrorPulseNamespaceReplacementExecution(MirrorPulseNamespaceReplacementIntent Intent,
    MirrorPulseNamespaceReplacementStart? Start, MirrorPulseNamespaceReplacementCancellation? Cancellation,
    MirrorPulseNamespaceReplacementAction? Action, MirrorPulseNamespaceReplacementBackup? Backup,
    MirrorPulseNamespaceReplacementWriteStart? WriteStart)
{
    public MirrorPulseNamespaceReplacementContentCompletion? ContentCompletion { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseNamespaceReplacementAction> PrepareNamespaceReplacementActionAsync(
        MirrorPulseNamespaceReplacementAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RecordNamespaceReplacementFactAsync(action, action.OperationId, 1, action.ActionId, ValidateReplacementAction,
            execution =>
            {
                if (execution.Start is null) throw new InvalidOperationException("The durable replacement start must precede its permission intent.");
                RequireReplacementActionReferences(action, execution.Intent, execution.Start);
            }, cancellationToken);
    }

    public Task<MirrorPulseNamespaceReplacementBackup> RecordNamespaceReplacementBackupAsync(
        MirrorPulseNamespaceReplacementBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return RecordNamespaceReplacementFactAsync(backup, backup.OperationId, 2, backup.ActionId, ValidateReplacementBackup,
            execution => RequireReplacementBackupReferences(backup, execution.Action ??
                throw new InvalidOperationException("The original replacement action must precede its old-content backup.")), cancellationToken);
    }

    public Task<MirrorPulseNamespaceReplacementWriteStart> RecordNamespaceReplacementWriteStartAsync(
        MirrorPulseNamespaceReplacementWriteStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return RecordNamespaceReplacementFactAsync(start, start.OperationId, 3, start.ActionId, ValidateReplacementWriteStart,
            execution => RequireReplacementWriteStartReferences(start, execution.Backup ??
                throw new InvalidOperationException("The independently verified old-content backup must precede any write intent.")), cancellationToken);
    }

    public async Task<MirrorPulseNamespaceReplacementExecution?> ReadNamespaceReplacementExecutionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        RequireNamespaceReplacementId(operationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceReplacementExecutionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RecordNamespaceReplacementFactAsync<T>(T value, Guid operationId, int kind, Guid actionId,
        Action<T> validate, Action<MirrorPulseNamespaceReplacementExecution> requireReferences, CancellationToken token) where T : class
    {
        ArgumentNullException.ThrowIfNull(value); validate(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            var retained = await ReadNamespaceReplacementFactCoreAsync(operationId, kind, validate, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (!SameNamespaceReplacementPayload(retained, value)) throw new InvalidOperationException("The first replacement execution fact is immutable.");
                transaction.Commit(); return retained;
            }
            if (execution.Cancellation is not null) throw new InvalidOperationException("A cancelled replacement cannot execute.");
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, token).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(execution.Intent.RootId, null, transaction, token,
                allowedReplacement: operationId).ConfigureAwait(false);
            requireReferences(execution);
            if (kind == 1)
            {
                var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(execution.Intent.MemberId, transaction, token).ConfigureAwait(false);
                if (current is null || !SameNamespaceReplacementPayload(current, execution.Intent.Original))
                    throw new InvalidOperationException("The original replacement product evidence changed before its permission intent.");
            }
            byte[] payload = NamespaceReplacementPayload(value);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = kind == 4 ?
                "INSERT INTO namespace_replacement_content_completions(operation_id,action_id,payload,fingerprint) VALUES($id,$action,$payload,$fingerprint);" :
                "INSERT INTO namespace_replacement_execution_facts(operation_id,kind,action_id,payload,fingerprint) VALUES($id,$kind,$action,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", operationId.ToString("D"));
            if (kind != 4) insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$action", actionId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false); transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceReplacementExecution?> ReadNamespaceReplacementExecutionCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        var intent = await ReadNamespaceReplacementCoreAsync(id, transaction, token).ConfigureAwait(false);
        if (intent is null) return null;
        var start = await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementStart>(id, false, transaction, token).ConfigureAwait(false);
        var cancellation = await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementCancellation>(id, true, transaction, token).ConfigureAwait(false);
        var action = await ReadNamespaceReplacementFactCoreAsync<MirrorPulseNamespaceReplacementAction>(id, 1, ValidateReplacementAction, transaction, token).ConfigureAwait(false);
        var backup = await ReadNamespaceReplacementFactCoreAsync<MirrorPulseNamespaceReplacementBackup>(id, 2, ValidateReplacementBackup, transaction, token).ConfigureAwait(false);
        var write = await ReadNamespaceReplacementFactCoreAsync<MirrorPulseNamespaceReplacementWriteStart>(id, 3, ValidateReplacementWriteStart, transaction, token).ConfigureAwait(false);
        var completion = await ReadNamespaceReplacementFactCoreAsync<MirrorPulseNamespaceReplacementContentCompletion>(id, 4, ValidateReplacementContentCompletion, transaction, token).ConfigureAwait(false);
        try
        {
            if (start is not null && start.StartedAt < intent.PreparedAt || cancellation is not null &&
                (cancellation.CancelledAt < intent.PreparedAt || start is not null || action is not null || backup is not null || write is not null || completion is not null))
                throw new InvalidOperationException("The replacement execution contradicts its original admission or cancellation.");
            if (action is not null) RequireReplacementActionReferences(action, intent, start ?? throw new InvalidOperationException("The replacement action lost its start."));
            if (backup is not null) RequireReplacementBackupReferences(backup, action ?? throw new InvalidOperationException("The backup lost its original action."));
            if (write is not null) RequireReplacementWriteStartReferences(write, backup ?? throw new InvalidOperationException("The write intent lost its old-content backup."));
            if (completion is not null) RequireReplacementContentCompletionReferences(completion, intent,
                action ?? throw new InvalidOperationException("The completed copy lost its original action."),
                write ?? throw new InvalidOperationException("The completed copy lost its before-write intent."),
                backup ?? throw new InvalidOperationException("The completed copy lost its original old-content backup."));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("The original replacement execution chain is inconsistent.", error); }
        return new(intent, start, cancellation, action, backup, write) { ContentCompletion = completion };
    }

    private async Task<T?> ReadNamespaceReplacementFactCoreAsync<T>(Guid id, int kind, Action<T> validate,
        SqliteTransaction transaction, CancellationToken token) where T : class
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = kind == 4 ? "SELECT action_id,payload,fingerprint FROM namespace_replacement_content_completions WHERE operation_id=$id;" :
            "SELECT action_id,payload,fingerprint FROM namespace_replacement_execution_facts WHERE operation_id=$id AND kind=$kind;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        if (kind != 4) query.Parameters.AddWithValue("$kind", kind);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceReplacementPayload<T>((byte[])reader[1], (byte[])reader[2], validate);
        var identity = value switch
        {
            MirrorPulseNamespaceReplacementAction action when kind == 1 => (action.OperationId, action.ActionId),
            MirrorPulseNamespaceReplacementBackup backup when kind == 2 => (backup.OperationId, backup.ActionId),
            MirrorPulseNamespaceReplacementWriteStart start when kind == 3 => (start.OperationId, start.ActionId),
            MirrorPulseNamespaceReplacementContentCompletion completion when kind == 4 => (completion.OperationId, completion.ActionId),
            _ => throw new InvalidDataException("The replacement fact type is inconsistent.")
        };
        if (identity.OperationId != id || reader.GetString(0) != identity.ActionId.ToString("D"))
            throw new InvalidDataException("The replacement execution fact and its indexes disagree.");
        return value;
    }

    private static void RequireReplacementActionReferences(MirrorPulseNamespaceReplacementAction action,
        MirrorPulseNamespaceReplacementIntent intent, MirrorPulseNamespaceReplacementStart start)
    {
        var original = intent.Original.Context;
        if (action.OperationId != intent.OperationId || action.LocalObject != original.Member.LocalObject ||
            action.OwnerSid != original.Verification.OwnerSid || action.RoleSid != original.Protection.RoleSid ||
            action.RestoreDacl != intent.Original.ExpectedDacl || action.PreparedAt < start.StartedAt)
            throw new InvalidOperationException("Replacement permission intent requires the admitted original object, owner, role and restore descriptor.");
    }

    private static void RequireReplacementBackupReferences(MirrorPulseNamespaceReplacementBackup backup, MirrorPulseNamespaceReplacementAction action)
    {
        if (backup.OperationId != action.OperationId || backup.ActionId != action.ActionId ||
            backup.Verification.LocalObject != action.LocalObject || backup.Verification.OwnerSid != action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, backup.Verification.Dacl) ||
            backup.Verification.ObservedAt < action.PreparedAt)
            throw new InvalidOperationException("The first old-content backup requires the same exclusively protected original object and action.");
    }

    private static void RequireReplacementWriteStartReferences(MirrorPulseNamespaceReplacementWriteStart start, MirrorPulseNamespaceReplacementBackup backup)
    {
        if (start.OperationId != backup.OperationId || start.ActionId != backup.ActionId || start.StartedAt < backup.Verification.ObservedAt)
            throw new InvalidOperationException("A write intent must retain the original verified backup and follow its receipt.");
    }

    private static void ValidateReplacementAction(MirrorPulseNamespaceReplacementAction action)
    {
        ArgumentNullException.ThrowIfNull(action); ValidateReplacementFactIdentity(action.Version, action.OperationId, action.ActionId);
        ValidatePermissionBinding(action.LocalObject); ValidatePermissionSid(action.OwnerSid, role: false); ValidatePermissionSid(action.RoleSid, role: true);
        ValidatePermissionDacl(action.RoleOnlyDacl); ValidatePermissionDacl(action.RestoreDacl);
        if (action.PreparedAt == default || action.RoleOnlyDacl == action.RestoreDacl || NamespaceReplacementPayload(action).Length > 262_144)
            throw new ArgumentException("The replacement action requires distinct bounded role-only and restore descriptors and its preparation time.", nameof(action));
    }

    private static void ValidateReplacementBackup(MirrorPulseNamespaceReplacementBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup); ArgumentNullException.ThrowIfNull(backup.Content); ArgumentNullException.ThrowIfNull(backup.Verification);
        ValidateReplacementFactIdentity(backup.Version, backup.OperationId, backup.ActionId); ValidateReplacementContent(backup.Content);
        ValidatePermissionBinding(backup.Verification.LocalObject); ValidatePermissionSid(backup.Verification.OwnerSid, role: false);
        ValidatePermissionDacl(backup.Verification.Dacl);
        if (backup.LinkCount != 1 || backup.Verification.ObservedAt == default || NamespaceReplacementPayload(backup).Length > 262_144)
            throw new ArgumentException("The old-content backup requires one retained original link and an independently observed descriptor.", nameof(backup));
    }

    private static void ValidateReplacementWriteStart(MirrorPulseNamespaceReplacementWriteStart start)
    {
        ArgumentNullException.ThrowIfNull(start); ValidateReplacementFactIdentity(start.Version, start.OperationId, start.ActionId);
        if (start.StartedAt == default) throw new ArgumentException("The replacement write intent requires its original time.", nameof(start));
    }

    private static void ValidateReplacementFactIdentity(int version, Guid id, Guid actionId)
    {
        if (version != 1 || id == Guid.Empty || actionId == Guid.Empty || id == actionId)
            throw new ArgumentException("A replacement fact requires its version and distinct original operation and action IDs.");
    }

    private static void ValidateReplacementContent(MirrorPulseNamespaceImportContent content)
    {
        if (content.Length < 0 || content.Sha256 is not { Length: 64 } || content.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new ArgumentException("Replacement content requires a nonnegative length and canonical SHA256.", nameof(content));
    }

    private static async Task EnsureNamespaceReplacementExecutionSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacement_execution_facts (
                operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
                kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),
                action_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(operation_id,kind),
                UNIQUE(action_id,kind)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
