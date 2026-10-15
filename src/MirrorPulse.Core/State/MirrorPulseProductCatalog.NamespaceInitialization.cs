using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Names retained root provenance selected before initialization can modify local permissions.</summary>
/// <remarks>A born root has no original manifest; its ManifestId is empty and BornRootMemberId
/// references its actual registered birth. Version 1 admissions retain their original JSON shape.</remarks>
public sealed record MirrorPulseNamespaceInitializationRoot(RootId RootId, Guid ManifestId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BornRootMemberId { get; init; }
}

/// <summary>Immutable initialization admission; this is neither permission evidence nor tree readiness.</summary>
/// <remarks>Version 3 explicitly admits an empty Adapter inventory with a selected recovery route.
/// Versions 1 and 2 retain their nonempty root requirements and historical payloads.</remarks>
public sealed record MirrorPulseNamespaceInitializationIntent(int Version, Guid OperationId,
    IReadOnlyList<MirrorPulseNamespaceInitializationRoot> Roots, DateTimeOffset PreparedAt)
{
    /// <summary>The chosen recovery route; absent legacy values remain unclassified.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceInitializationInventoryKind? RequestedInventoryKind { get; init; }
}

public enum MirrorPulseNamespaceInitializationInventoryKind { OriginalInventory, CurrentInventory }

/// <summary>A historical inventory audit receipt. A later Host still needs a fresh current-role audit.</summary>
public sealed record MirrorPulseNamespaceInitializationCompletion(Guid OperationId, Guid AnchorMemberId,
    string RoleSid, int MemberCount, string CatalogFingerprint, DateTimeOffset CompletedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MirrorPulseNamespaceInitializationInventoryKind InventoryKind { get; init; }
}

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
            foreach (var root in intent.Roots.Where(root => root.BornRootMemberId is not null))
                await RequireNamespaceInitializationBornRootAsync(root, intent.PreparedAt, transaction, cancellationToken).ConfigureAwait(false);
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
                reserve.CommandText = "INSERT INTO namespace_initialization_roots(root_id,operation_id,manifest_id,born_member_id) VALUES($root,$id,$manifest,$born);";
                reserve.Parameters.AddWithValue("$root", root.RootId.Value.ToString("D"));
                reserve.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
                reserve.Parameters.AddWithValue("$manifest", root.ManifestId == Guid.Empty ? DBNull.Value : root.ManifestId.ToString("D"));
                reserve.Parameters.AddWithValue("$born", (object?)root.BornRootMemberId?.ToString("D") ?? DBNull.Value);
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
        query.CommandText = "SELECT root_id,manifest_id,born_member_id FROM namespace_initialization_roots WHERE operation_id=$id ORDER BY root_id;";
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!RootId.TryParse(reader.GetString(0), out RootId root) ||
                !reader.IsDBNull(1) && !Guid.TryParseExact(reader.GetString(1), "D", out _) ||
                !reader.IsDBNull(2) && !Guid.TryParseExact(reader.GetString(2), "D", out _))
                throw new InvalidDataException("The initialization root reservation is invalid.");
            actual.Add(new(root, reader.IsDBNull(1) ? Guid.Empty : Guid.ParseExact(reader.GetString(1), "D"))
            { BornRootMemberId = reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "D") });
        }
        if (!actual.SequenceEqual(intent.Roots.OrderBy(root => root.RootId.Value.ToString("D"), StringComparer.Ordinal)))
            throw new InvalidDataException("Initialization lost or changed a root reservation.");
    }

    private async Task RequireNamespaceInitializationTerminalAsync(MirrorPulseNamespaceInitializationIntent intent,
        MirrorPulseNamespaceInitializationCompletion completion, SqliteTransaction transaction, CancellationToken token)
    {
        if ((intent.Version == 3 ? completion.MemberCount != 1 : completion.MemberCount < 2) ||
            completion.CompletedAt < intent.PreparedAt || intent.RequestedInventoryKind is { } requested && requested != completion.InventoryKind)
            throw new InvalidDataException("Initialization completion disagrees with its admission chronology or requested inventory kind.");
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT COUNT(*) FROM namespace_initialization_roots WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        if ((long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0)
            throw new InvalidDataException("A completed initialization retains a pending root reservation.");
    }

    private static bool SameNamespaceInitialization(MirrorPulseNamespaceInitializationIntent left, MirrorPulseNamespaceInitializationIntent right) =>
        left.Version == right.Version && left.OperationId == right.OperationId && left.PreparedAt == right.PreparedAt &&
        left.RequestedInventoryKind == right.RequestedInventoryKind && left.Roots.SequenceEqual(right.Roots);

    private static void ValidateNamespaceInitialization(MirrorPulseNamespaceInitializationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Version is not (1 or 2 or 3) || intent.OperationId == Guid.Empty || intent.PreparedAt == default || intent.Roots is null ||
            intent.RequestedInventoryKind is { } requested && !Enum.IsDefined(requested) ||
            intent.Version == 2 && intent.RequestedInventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory ||
            intent.Version == 3 && (intent.Roots.Count != 0 || intent.RequestedInventoryKind is null) ||
            intent.Version != 3 && intent.Roots.Count == 0 || intent.Roots.Count > 4096 || intent.Roots.Any(root => root is null || root.RootId.Value == Guid.Empty ||
                (root.BornRootMemberId is { } born ? intent.Version != 2 || born == Guid.Empty || root.ManifestId != Guid.Empty : root.ManifestId == Guid.Empty)) ||
            intent.Roots.Select(root => root.RootId).Distinct().Count() != intent.Roots.Count ||
            intent.Roots.Where(root => root.BornRootMemberId is null).Select(root => root.ManifestId).Distinct().Count() != intent.Roots.Count(root => root.BornRootMemberId is null) ||
            intent.Roots.Where(root => root.BornRootMemberId is not null).Select(root => root.BornRootMemberId).Distinct().Count() != intent.Roots.Count(root => root.BornRootMemberId is not null))
            throw new ArgumentException("Initialization requires distinct stable roots and explicit retained capture or born-root provenance.", nameof(intent));
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
                manifest_id TEXT UNIQUE, born_member_id TEXT UNIQUE,
                CHECK ((manifest_id IS NOT NULL AND born_member_id IS NULL) OR
                    (manifest_id IS NULL AND born_member_id IS NOT NULL)));
            CREATE INDEX IF NOT EXISTS namespace_initialization_owner ON namespace_initialization_roots(operation_id);
            CREATE TABLE IF NOT EXISTS namespace_initialization_completions (
                operation_id TEXT PRIMARY KEY REFERENCES namespace_initialization_intents(operation_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await MigrateNamespaceInitializationRootSourcesAsync(connection, token).ConfigureAwait(false);
    }
}
