using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>An immutable before-write admission for replacing one existing member's content.</summary>
/// <remarks>
/// The original protection, placement and identity are product evidence, not a fresh native
/// authorization. The Host validates the public identity encoding and retains actual object
/// and ancestor guards before any local operation. The admission reserves recovery ownership
/// without changing membership, writing bytes, accepting an upload or acknowledging a journal ID.
/// </remarks>
public sealed record MirrorPulseNamespaceReplacementIntent(int Version, Guid OperationId,
    MirrorPulseNamespaceMemberProtectionSnapshot Original, string OriginalPlaceholderIdentity,
    MirrorPulseNamespaceImportContent Content, DateTimeOffset PreparedAt)
{
    [JsonIgnore] public Guid MemberId => Original.Context.Member.MemberId;
    [JsonIgnore] public RootId RootId => Original.Context.Member.RootId!.Value;
}

/// <summary>A retained start intent committed before tightening access or changing the original object.</summary>
public sealed record MirrorPulseNamespaceReplacementStart(int Version, Guid OperationId, DateTimeOffset StartedAt);

/// <summary>Cancellation of an admission proven never started; it is not rollback evidence.</summary>
public sealed record MirrorPulseNamespaceReplacementCancellation(int Version, Guid OperationId, DateTimeOffset CancelledAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceReplacementIntent> PrepareNamespaceReplacementAsync(
        MirrorPulseNamespaceReplacementIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent); ValidateNamespaceReplacement(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadNamespaceReplacementCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (!SameNamespaceReplacementPayload(retained, intent)) throw new InvalidOperationException("The original replacement admission is immutable.");
                transaction.Commit(); return retained;
            }
            if (intent.OperationId == intent.MemberId)
                throw new InvalidOperationException("Replacement cannot reuse a member's identity.");
            await RequireReplacementIdAvailableCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken).ConfigureAwait(false);
            var births = new HashSet<RootId>();
            await AddUnregisteredBirthRootFencesAsync(births, transaction, cancellationToken).ConfigureAwait(false);
            if (births.Contains(intent.RootId)) throw new InvalidOperationException("An unresolved birth must finish before replacement admission.");
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The replacement member is not registered.");
            if (!SameNamespaceReplacementPayload(current, intent.Original))
                throw new InvalidOperationException("Replacement admission requires this member's unchanged current protection, location and identity.");
            byte[] payload = NamespaceReplacementPayload(intent);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_replacements(operation_id,member_id,root_id,payload,fingerprint)
                VALUES($id,$member,$root,$payload,$fingerprint);
                INSERT INTO namespace_replacement_pending(root_id,member_id,operation_id) VALUES($root,$member,$id);
                """;
            insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$root", intent.RootId.ToString());
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads original admission without inspecting today's object or refreshing execution authority.</summary>
    public async Task<MirrorPulseNamespaceReplacementIntent?> ReadNamespaceReplacementAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        RequireNamespaceReplacementId(operationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceReplacementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceReplacementStart> RecordNamespaceReplacementStartAsync(
        MirrorPulseNamespaceReplacementStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start); RequireNamespaceReplacementId(start.OperationId);
        if (start.Version != 1 || start.StartedAt == default) throw new ArgumentException("A versioned replacement start is required.", nameof(start));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceReplacementCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            if (start.StartedAt < intent.PreparedAt) throw new InvalidOperationException("Replacement cannot start before admission.");
            var retained = await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementStart>(start.OperationId, false,
                transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != start) throw new InvalidOperationException("The first replacement start is immutable.");
                transaction.Commit(); return retained;
            }
            if (await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementCancellation>(start.OperationId, true,
                transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A cancelled replacement cannot start.");
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken,
                allowedReplacement: intent.OperationId).ConfigureAwait(false);
            await RequireNamespaceReplacementPendingCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (current is null || !SameNamespaceReplacementPayload(current, intent.Original))
                throw new InvalidOperationException("The original replacement protection, placement or identity changed before start.");
            await InsertNamespaceReplacementStageCoreAsync(start.OperationId, start, false, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return start;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases only a never-started admission. Started work requires native recovery and cannot be cancelled as untouched.</summary>
    public async Task<MirrorPulseNamespaceReplacementCancellation> CancelUnstartedNamespaceReplacementAsync(
        MirrorPulseNamespaceReplacementCancellation cancellation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation); RequireNamespaceReplacementId(cancellation.OperationId);
        if (cancellation.Version != 1 || cancellation.CancelledAt == default) throw new ArgumentException("A versioned replacement cancellation is required.", nameof(cancellation));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceReplacementCoreAsync(cancellation.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            if (cancellation.CancelledAt < intent.PreparedAt) throw new InvalidOperationException("Cancellation cannot precede admission.");
            var retained = await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementCancellation>(cancellation.OperationId,
                true, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != cancellation) throw new InvalidOperationException("The original cancellation is immutable.");
                transaction.Commit(); return retained;
            }
            if (await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementStart>(cancellation.OperationId,
                false, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Started replacement work cannot release its fence without native recovery.");
            await RequireNamespaceReplacementPendingCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            await InsertNamespaceReplacementStageCoreAsync(cancellation.OperationId, cancellation, true, transaction, cancellationToken).ConfigureAwait(false);
            await using var release = _connection.CreateCommand(); release.Transaction = transaction;
            release.CommandText = "DELETE FROM namespace_replacement_pending WHERE root_id=$root AND operation_id=$id;";
            release.Parameters.AddWithValue("$root", intent.RootId.ToString()); release.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
            if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("Cancellation must release its own retained root fence.");
            transaction.Commit(); return cancellation;
        }
        finally { _gate.Release(); }
    }

    private static byte[] NamespaceReplacementPayload<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
    private static bool SameNamespaceReplacementPayload<T>(T left, T right) => NamespaceReplacementPayload(left).AsSpan().SequenceEqual(NamespaceReplacementPayload(right));
    private static void RequireNamespaceReplacementId(Guid id)
    { if (id == Guid.Empty) throw new ArgumentException("A stable replacement operation ID is required."); }

    private async Task RequireReplacementIdAvailableCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var ownership = _connection.CreateCommand(); ownership.Transaction = transaction;
        ownership.CommandText = """
            SELECT 1 FROM namespace_birth_intents WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_moves WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_retirements WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_member_retirement_batches WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_permission_changes WHERE operation_id=$id
            UNION ALL SELECT 1 FROM managed_root_rename_history WHERE operation_id=$id
            UNION ALL SELECT 1 FROM mutation_intents WHERE operation_id=$id
            UNION ALL SELECT 1 FROM worker_requests WHERE operation_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_plans WHERE plan_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_members WHERE operation_id=$id
            UNION ALL SELECT 1 FROM journal_coalescing_steps WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_replacements WHERE operation_id=$id
            UNION ALL SELECT 1 FROM namespace_replacement_restorations WHERE restoration_id=$id
            UNION ALL SELECT 1 FROM namespace_replacement_role_actions WHERE recovery_id=$id
            UNION ALL SELECT 1 FROM namespace_replacement_execution_facts WHERE action_id=$id LIMIT 1;
            """;
        ownership.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await ownership.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Replacement cannot reuse another operation's ownership or private input key.");
    }

    private static void ValidateNamespaceReplacement(MirrorPulseNamespaceReplacementIntent intent)
    {
        RequireNamespaceReplacementId(intent.OperationId); ArgumentNullException.ThrowIfNull(intent.Original);
        ArgumentNullException.ThrowIfNull(intent.Original.Context); ArgumentNullException.ThrowIfNull(intent.Original.Identity);
        ArgumentNullException.ThrowIfNull(intent.Content);
        var context = intent.Original.Context; var member = context.Member;
        ArgumentNullException.ThrowIfNull(member); ArgumentNullException.ThrowIfNull(context.Location);
        ArgumentNullException.ThrowIfNull(context.Verification); ArgumentNullException.ThrowIfNull(context.Protection);
        if (intent.Version != 1 || intent.PreparedAt == default || member is null || member.IsDirectory || member.MemberId == Guid.Empty ||
            member.RootId is not { } root || root.Value == Guid.Empty || context.Location.ParentMemberId is null ||
            intent.Original.Identity.MemberId != member.MemberId || intent.Original.Identity.ItemId == Guid.Empty ||
            intent.PreparedAt < member.RegisteredAt ||
            intent.PreparedAt < context.Verification.ObservedAt || intent.PreparedAt < context.Protection.VerifiedAt ||
            context.PositionVerification is { } position && intent.PreparedAt < position.ObservedAt)
            throw new ArgumentException("Replacement requires an existing protected file and its complete current admission snapshot.", nameof(intent));
        ValidatePermissionBinding(member.LocalObject); ValidatePermissionRelativePath(context.Location.RelativePath);
        ValidatePermissionSid(member.OwnerSid, role: false); ValidatePermissionSid(context.Protection.RoleSid, role: true);
        if (string.IsNullOrWhiteSpace(intent.Original.ExpectedDacl) || intent.Original.ExpectedDacl.Length > 65_536 ||
            string.IsNullOrWhiteSpace(intent.Original.Identity.RemoteId) || intent.Original.Identity.RemoteId.Length > 16_384)
            throw new ArgumentException("Replacement requires retained protection and public member identity.", nameof(intent));
        if (intent.Content.Length < 0 || intent.Content.Sha256 is not { Length: 64 } ||
            intent.Content.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new ArgumentException("Replacement requires the exact staged new-content length and SHA256 digest.", nameof(intent));
        if (intent.OriginalPlaceholderIdentity is not { Length: > 0 and <= 5464 })
            throw new ArgumentException("Replacement requires a bounded public identity encoding.", nameof(intent));
        byte[] identity;
        try { identity = Convert.FromBase64String(intent.OriginalPlaceholderIdentity); }
        catch (Exception error) when (error is FormatException or ArgumentNullException)
        { throw new ArgumentException("Replacement requires the bounded public identity encoding.", nameof(intent), error); }
        if (identity.Length is 0 or > 4096 || Convert.ToBase64String(identity) != intent.OriginalPlaceholderIdentity ||
            NamespaceReplacementPayload(intent).Length > 262_144)
            throw new ArgumentException("The original replacement evidence must use bounded canonical encoding.", nameof(intent));
    }

    private async Task<MirrorPulseNamespaceReplacementIntent?> ReadNamespaceReplacementCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,member_id,root_id,payload,fingerprint FROM namespace_replacements WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var intent = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementIntent>((byte[])reader[3], (byte[])reader[4], ValidateNamespaceReplacement);
        if (reader.GetString(0) != id.ToString("D") || intent.OperationId != id || reader.GetString(1) != intent.MemberId.ToString("D") || reader.GetString(2) != intent.RootId.ToString())
            throw new InvalidDataException("The original replacement payload and indexes disagree.");
        return intent;
    }

    private static T ReadNamespaceReplacementPayload<T>(byte[] payload, byte[] fingerprint, Action<T> validate)
    {
        if (payload.Length is 0 or > 262_144 || fingerprint.Length != 32 || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The original replacement fingerprint is invalid.");
        try
        {
            var value = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The replacement payload is empty.");
            validate(value);
            if (!NamespaceReplacementPayload(value).AsSpan().SequenceEqual(payload)) throw new InvalidDataException("The replacement payload is not canonical.");
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or NullReferenceException)
        { throw new InvalidDataException("The replacement payload is invalid.", error); }
    }

    private async Task<T?> ReadNamespaceReplacementStageCoreAsync<T>(Guid id, bool cancelled, SqliteTransaction transaction, CancellationToken token) where T : class
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = cancelled ? "SELECT payload,fingerprint FROM namespace_replacement_cancellations WHERE operation_id=$id;" :
            "SELECT payload,fingerprint FROM namespace_replacement_starts WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return ReadNamespaceReplacementPayload<T>((byte[])reader[0], (byte[])reader[1], value =>
        {
            bool valid = value switch
            {
                MirrorPulseNamespaceReplacementStart start => !cancelled && start.Version == 1 && start.OperationId == id && start.StartedAt != default,
                MirrorPulseNamespaceReplacementCancellation cancellation => cancelled && cancellation.Version == 1 && cancellation.OperationId == id && cancellation.CancelledAt != default,
                _ => false
            };
            if (!valid) throw new InvalidDataException("The replacement stage identity is inconsistent.");
        });
    }

    private async Task InsertNamespaceReplacementStageCoreAsync<T>(Guid id, T value, bool cancelled, SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = NamespaceReplacementPayload(value);
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = cancelled ? "INSERT INTO namespace_replacement_cancellations(operation_id,payload,fingerprint) VALUES($id,$payload,$fingerprint);" :
            "INSERT INTO namespace_replacement_starts(operation_id,payload,fingerprint) VALUES($id,$payload,$fingerprint);";
        insert.Parameters.AddWithValue("$id", id.ToString("D")); insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task RequireNamespaceReplacementPendingCoreAsync(MirrorPulseNamespaceReplacementIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        var pending = await ReadPendingNamespaceReplacementCoreAsync(intent.RootId, transaction, token).ConfigureAwait(false);
        if (pending is null || pending.OperationId != intent.OperationId || !SameNamespaceReplacementPayload(pending, intent))
            throw new InvalidDataException("The original replacement lost its recovery ownership.");
    }

    private async Task<MirrorPulseNamespaceReplacementIntent?> ReadPendingNamespaceReplacementCoreAsync(RootId root,
        SqliteTransaction transaction, CancellationToken token)
    {
        Guid? id = null; Guid? member = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT operation_id,member_id FROM namespace_replacement_pending WHERE root_id=$root;";
            query.Parameters.AddWithValue("$root", root.ToString());
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) { id = ParseNamespaceMemberId(reader.GetString(0)); member = ParseNamespaceMemberId(reader.GetString(1)); }
        }
        if (id is null)
        {
            await using var missing = _connection.CreateCommand(); missing.Transaction = transaction;
            missing.CommandText = "SELECT 1 FROM namespace_replacements p WHERE root_id=$root AND NOT EXISTS(SELECT 1 FROM namespace_replacement_cancellations c WHERE c.operation_id=p.operation_id) LIMIT 1;";
            missing.Parameters.AddWithValue("$root", root.ToString());
            if (await missing.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An unresolved replacement lost its root fence.");
            return null;
        }
        await using (var unresolved = _connection.CreateCommand())
        {
            unresolved.Transaction = transaction;
            unresolved.CommandText = "SELECT operation_id FROM namespace_replacements p WHERE root_id=$root AND NOT EXISTS(SELECT 1 FROM namespace_replacement_cancellations c WHERE c.operation_id=p.operation_id);";
            unresolved.Parameters.AddWithValue("$root", root.ToString());
            await using var reader = await unresolved.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != id.Value.ToString("D") || await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidDataException("Replacement recovery ownership must cover exactly one unresolved admission per root.");
        }
        var intent = await ReadNamespaceReplacementCoreAsync(id.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending replacement lost its original admission.");
        if (intent.RootId != root || intent.MemberId != member || await ReadNamespaceReplacementStageCoreAsync<MirrorPulseNamespaceReplacementCancellation>(id.Value,
            true, transaction, token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("The pending replacement ownership is inconsistent.");
        _ = await ReadNamespaceReplacementExecutionCoreAsync(id.Value, transaction, token).ConfigureAwait(false);
        return intent;
    }

    private async Task AddNamespaceReplacementRootFencesAsync(HashSet<RootId> roots, SqliteTransaction transaction, CancellationToken token)
    {
        var candidates = new List<RootId>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT DISTINCT root_id FROM namespace_replacements UNION SELECT root_id FROM namespace_replacement_pending;";
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string stored = reader.GetString(0);
                if (!RootId.TryParse(stored, out var root) || root.ToString() != stored) throw new InvalidDataException("The replacement root index is invalid.");
                candidates.Add(root);
            }
        }
        foreach (var root in candidates)
            if (await ReadPendingNamespaceReplacementCoreAsync(root, transaction, token).ConfigureAwait(false) is not null) roots.Add(root);
    }

    private static async Task EnsureNamespaceReplacementSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacements (
                operation_id TEXT NOT NULL PRIMARY KEY,
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                root_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_replacement_pending (
                root_id TEXT NOT NULL PRIMARY KEY,
                member_id TEXT NOT NULL UNIQUE REFERENCES namespace_members(member_id),
                operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_replacements(operation_id)
            );
            CREATE INDEX IF NOT EXISTS namespace_replacements_root ON namespace_replacements(root_id);
            CREATE TABLE IF NOT EXISTS namespace_replacement_starts (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_replacement_cancellations (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false); transaction.Commit();
    }
}
