using System.Text;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // This is a schema-only baseline for an atomically created empty file. Later migrations
    // remain authoritative, including future versions newer than this baseline.
    private const long FreshCatalogSchemaVersion = 99;

    private static async Task CreateFreshCatalogSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var resource = typeof(MirrorPulseProductCatalog).Assembly
            .GetManifestResourceStream("MirrorPulse.Core.State.CatalogSchema99.sql")
            ?? throw new InvalidDataException("The product's empty catalog schema resource is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string sql = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        // Every DDL statement and the baseline version share one FULL-durability transaction.
        // No synchronous/journal policy is relaxed, and no user or execution rows are copied.
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT 1 FROM sqlite_master LIMIT 1;";
        if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("An empty product catalog acquired schema objects before bootstrap.");
        schema.CommandText = "PRAGMA user_version;";
        if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not long version || version != 0)
            throw new InvalidDataException("An empty product catalog acquired a schema version before bootstrap.");
        schema.CommandText = sql;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
