using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Names an original capture selected before initialization can modify local permissions.</summary>
public sealed record MirrorPulseNamespaceInitializationRoot(RootId RootId, Guid ManifestId);

/// <summary>Immutable initialization admission; this is neither permission evidence nor tree readiness.</summary>
public sealed record MirrorPulseNamespaceInitializationIntent(int Version, Guid OperationId,
    IReadOnlyList<MirrorPulseNamespaceInitializationRoot> Roots, DateTimeOffset PreparedAt);

/// <summary>A historical original-tree audit receipt. A later Host still needs a fresh current-role audit.</summary>
public sealed record MirrorPulseNamespaceInitializationCompletion(Guid OperationId, Guid AnchorMemberId,
    string RoleSid, int MemberCount, string CatalogFingerprint, DateTimeOffset CompletedAt);

public sealed record MirrorPulseNamespaceInitializationStatus(MirrorPulseNamespaceInitializationIntent Intent,
    MirrorPulseNamespaceInitializationCompletion? Completion);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Persists initialization admission and per-root dispatch fences in one short transaction.</summary>
    /// <remarks>Recovery and controlled local operations remain admissible. No capture or native write is performed.</remarks>
    public async Task<MirrorPulseNamespaceInitializationIntent> PrepareNamespaceInitializationAsync(
        MirrorPulseNamespaceInitializationIntent intent, CancellationToken cancellationToken = default)
    {
        ValidateNamespaceInitialization(intent);
        intent = intent with { Roots = Array.AsReadOnly(intent.Roots.ToArray()) };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceInitializationCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!SameNamespaceInitialization(replay, intent)) throw new InvalidOperationException("Initialization admission is immutable.");
                if (await ReadNamespaceInitializationCompletionCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false) is { } completed)
                    await RequireNamespaceInitializationTerminalAsync(replay, completed, transaction, cancellationToken).ConfigureAwait(false);
                else
                    await RequireNamespaceInitializationRootsAsync(replay, transaction, cancellationToken).ConfigureAwait(false);
                transaction.Commit(); return replay;
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_initialization_intents(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var root in intent.Roots)
            {
                await using SqliteCommand reserve = _connection.CreateCommand(); reserve.Transaction = transaction;
                reserve.CommandText = "INSERT INTO namespace_initialization_roots(root_id,operation_id,manifest_id) VALUES($root,$id,$manifest);";
                reserve.Parameters.AddWithValue("$root", root.RootId.Value.ToString("D"));
                reserve.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
                reserve.Parameters.AddWithValue("$manifest", root.ManifestId.ToString("D"));
                await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceInitializationIntent?> ReadNamespaceInitializationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var intent = await ReadNamespaceInitializationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (intent is not null)
            {
                if (await ReadNamespaceInitializationCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is { } completed)
                    await RequireNamespaceInitializationTerminalAsync(intent, completed, transaction, cancellationToken).ConfigureAwait(false);
                else
                    await RequireNamespaceInitializationRootsAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceInitializationStatus> ReadNamespaceInitializationStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An initialization operation ID is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var status = await ReadNamespaceInitializationStatusCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return status;
        }
        finally { _gate.Release(); }
    }

    private async Task AddNamespaceInitializationRootFencesAsync(HashSet<RootId> roots, SqliteTransaction transaction, CancellationToken token)
    {
        var operations = new HashSet<Guid>();
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            // The intent side detects lost pending indexes; the reservation side detects orphans.
            query.CommandText = """
                SELECT operation_id FROM namespace_initialization_intents
                UNION SELECT operation_id FROM namespace_initialization_roots
                UNION SELECT operation_id FROM namespace_initialization_completions;
                """;
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid operation) || operation == Guid.Empty)
                    throw new InvalidDataException("The initialization operation index is invalid.");
                operations.Add(operation);
            }
        }
        foreach (Guid operation in operations)
        {
            var intent = await ReadNamespaceInitializationCoreAsync(operation, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending initialization lost its original admission.");
            if (await ReadNamespaceInitializationCompletionCoreAsync(operation, transaction, token).ConfigureAwait(false) is { } completion)
            {
                await RequireNamespaceInitializationTerminalAsync(intent, completion, transaction, token).ConfigureAwait(false);
                continue;
            }
            await RequireNamespaceInitializationRootsAsync(intent, transaction, token).ConfigureAwait(false);
            foreach (var root in intent.Roots) roots.Add(root.RootId);
        }
    }

    private async Task<MirrorPulseNamespaceInitializationIntent?> ReadNamespaceInitializationCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_initialization_intents WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader.GetValue(0), fingerprint = (byte[])reader.GetValue(1);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The initialization admission fingerprint changed.");
        var intent = JsonSerializer.Deserialize<MirrorPulseNamespaceInitializationIntent>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The initialization admission is missing.");
        ValidateNamespaceInitialization(intent);
        if (intent.OperationId != operationId) throw new InvalidDataException("The initialization admission index changed.");
        return intent with { Roots = Array.AsReadOnly(intent.Roots.ToArray()) };
    }

    private async Task RequireNamespaceInitializationRootsAsync(MirrorPulseNamespaceInitializationIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        var actual = new List<MirrorPulseNamespaceInitializationRoot>();
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT root_id,manifest_id FROM namespace_initialization_roots WHERE operation_id=$id ORDER BY root_id;";
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!RootId.TryParse(reader.GetString(0), out RootId root) || !Guid.TryParseExact(reader.GetString(1), "D", out Guid manifest))
                throw new InvalidDataException("The initialization root reservation is invalid.");
            actual.Add(new(root, manifest));
        }
        if (!actual.SequenceEqual(intent.Roots.OrderBy(root => root.RootId.Value.ToString("D"), StringComparer.Ordinal)))
            throw new InvalidDataException("Initialization lost or changed a root reservation.");
    }

    private async Task RequireNamespaceInitializationTerminalAsync(MirrorPulseNamespaceInitializationIntent intent,
        MirrorPulseNamespaceInitializationCompletion completion, SqliteTransaction transaction, CancellationToken token)
    {
        if (completion.CompletedAt < intent.PreparedAt) throw new InvalidDataException("Initialization completion predates admission.");
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT COUNT(*) FROM namespace_initialization_roots WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        if ((long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0)
            throw new InvalidDataException("A completed initialization retains a pending root reservation.");
    }

    private static bool SameNamespaceInitialization(MirrorPulseNamespaceInitializationIntent left, MirrorPulseNamespaceInitializationIntent right) =>
        left.Version == right.Version && left.OperationId == right.OperationId && left.PreparedAt == right.PreparedAt && left.Roots.SequenceEqual(right.Roots);

    private static void ValidateNamespaceInitialization(MirrorPulseNamespaceInitializationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Version != 1 || intent.OperationId == Guid.Empty || intent.PreparedAt == default || intent.Roots is null ||
            intent.Roots.Count is < 1 or > 4096 || intent.Roots.Any(root => root is null || root.RootId.Value == Guid.Empty || root.ManifestId == Guid.Empty) ||
            intent.Roots.Select(root => root.RootId).Distinct().Count() != intent.Roots.Count ||
            intent.Roots.Select(root => root.ManifestId).Distinct().Count() != intent.Roots.Count)
            throw new ArgumentException("Initialization requires a bounded set of distinct stable roots and original capture IDs.", nameof(intent));
    }

    private static async Task EnsureNamespaceInitializationSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_initialization_intents (
                operation_id TEXT PRIMARY KEY, payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE TABLE IF NOT EXISTS namespace_initialization_roots (
                root_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
                manifest_id TEXT NOT NULL UNIQUE);
            CREATE INDEX IF NOT EXISTS namespace_initialization_owner ON namespace_initialization_roots(operation_id);
            CREATE TABLE IF NOT EXISTS namespace_initialization_completions (
                operation_id TEXT PRIMARY KEY REFERENCES namespace_initialization_intents(operation_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
