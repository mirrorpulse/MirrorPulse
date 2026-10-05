using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.State;

public sealed record MirrorPulseAdapterTopology(
    IReadOnlyList<InstalledAdapter> Installations,
    IReadOnlyList<AdapterInstance> Instances,
    IReadOnlyList<RootRegistration> Roots);

public sealed partial class MirrorPulseProductCatalog : IInstalledAdapterCatalog
{
    private static readonly JsonSerializerOptions TopologyJsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new CatalogManifestConverter(),
            new StringValueConverter<AdapterId>(AdapterId.Parse),
            new StringValueConverter<InstallId>(InstallId.Parse),
            new StringValueConverter<InstanceId>(InstanceId.Parse),
            new StringValueConverter<RootId>(RootId.Parse),
            new StringValueConverter<Sha256Digest>(Sha256Digest.Parse),
            new StringValueConverter<WorkerSessionId>(WorkerSessionId.Parse),
        },
    };

    /// <summary>Atomically replaces the MP-owned install, instance and root inventory.</summary>
    public async Task SaveAdapterTopologyAsync(
        MirrorPulseAdapterTopology topology,
        CancellationToken cancellationToken = default)
    {
        ValidateTopology(topology);
        string payload = JsonSerializer.Serialize(topology, TopologyJsonOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO adapter_topology (id, payload) VALUES (1, $payload)
                ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;
                """;
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MirrorPulseAdapterTopology> ReadAdapterTopologyAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return new([], [], []);
            }

            try
            {
                return DeserializeTopology(payload);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The Adapter topology is invalid JSON.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads a WAL-consistent inventory from another current-user process without taking the Host writer lock.</summary>
    public static async Task<MirrorPulseAdapterTopology> ReadAdapterTopologySnapshotAsync(
        Configuration.MirrorPulseStoragePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.ProductCatalogDatabasePath))
        {
            return new([], [], []);
        }

        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ProductCatalogDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(settings.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand table = connection.CreateCommand())
        {
            table.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'adapter_topology';";
            if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return new([], [], []);
            }
        }

        await using SqliteCommand query = connection.CreateCommand();
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
        string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? new([], [], []) : DeserializeTopology(payload);
    }

    public static async Task<IReadOnlyList<MirrorPulseInstanceRuntimeState>> ReadRuntimeSnapshotAsync(
        Configuration.MirrorPulseStoragePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.ProductCatalogDatabasePath))
        {
            return [];
        }

        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ProductCatalogDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(settings.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT instance_id, phase, requires_full_rescan, last_successful_sync_utc, last_error_code,
                   transfer_operation, transfer_bytes, transfer_total, transfer_updated_utc
            FROM instance_runtime ORDER BY instance_id;
            """;
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var states = new List<MirrorPulseInstanceRuntimeState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            MirrorPulseTransferProgress? progress = reader.IsDBNull(5) || reader.IsDBNull(6) ||
                reader.IsDBNull(8)
                ? null
                : new MirrorPulseTransferProgress(
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture));
            states.Add(new MirrorPulseInstanceRuntimeState(
                InstanceId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3),
                    System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4), progress));
        }

        return states;
    }

    public async ValueTask<InstalledAdapter?> FindAsync(
        InstallId installId,
        CancellationToken cancellationToken = default)
    {
        MirrorPulseAdapterTopology topology = await ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        return topology.Installations.SingleOrDefault(adapter => adapter.InstallId == installId);
    }

    /// <summary>Changes one instance's startup selection and its retained first-level namespace atomically.</summary>
    public async Task<MirrorPulseAdapterTopology> SetInstanceEnabledAsync(
        InstanceId instanceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                throw new FileNotFoundException("The Adapter instance is not installed.");
            }

            MirrorPulseAdapterTopology current = DeserializeTopology(payload);
            if (!current.Instances.Any(instance => instance.InstanceId == instanceId))
            {
                throw new FileNotFoundException("The Adapter instance is not installed.");
            }

            AdapterInstance[] instances = current.Instances.Select(instance =>
                instance.InstanceId != instanceId ? instance : new AdapterInstance(
                    instance.AdapterId, instance.InstallId, instance.InstanceId, instance.DisplayName,
                    instance.Configuration, instance.CredentialReferences, instance.FileCacheDirectory,
                    instance.TransferCacheDirectory, enabled,
                    enabled ? AdapterLifecycleState.Enabled : AdapterLifecycleState.Disabled,
                    null, instance.CreatedAt)).ToArray();
            RootRegistration[] roots = current.Roots.Select(root =>
            {
                if (root.InstanceId != instanceId ||
                    root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
                {
                    return root;
                }

                return new RootRegistration(root.AdapterId, root.InstanceId, root.RootId,
                    root.UniquenessKey, root.Label, root.DirectoryName, root.CustomEntry,
                    enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled,
                    root.RegisteredAt, root.IdentityScope);
            }).ToArray();
            var next = new MirrorPulseAdapterTopology(current.Installations, instances, roots);
            ValidateTopology(next);
            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = "UPDATE adapter_topology SET payload = $payload WHERE id = 1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pins one instance to a different installed version of the same Adapter.</summary>
    public async Task<MirrorPulseAdapterTopology> SelectInstanceInstallationAsync(
        InstanceId instanceId,
        InstallId installId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                throw new FileNotFoundException("The Adapter instance is not installed.");
            }

            MirrorPulseAdapterTopology current = DeserializeTopology(payload);
            AdapterInstance instance = current.Instances.SingleOrDefault(item => item.InstanceId == instanceId)
                ?? throw new FileNotFoundException("The Adapter instance is not installed.");
            InstalledAdapter selected = current.Installations.SingleOrDefault(item => item.InstallId == installId)
                ?? throw new FileNotFoundException("The selected Adapter version is not installed.");
            if (selected.AdapterId != instance.AdapterId)
            {
                throw new InvalidDataException("An instance cannot select another Adapter's installation.");
            }

            AdapterInstance[] instances = current.Instances.Select(item =>
                item.InstanceId != instanceId ? item : new AdapterInstance(
                    item.AdapterId, installId, item.InstanceId, item.DisplayName,
                    item.Configuration, item.CredentialReferences, item.FileCacheDirectory,
                    item.TransferCacheDirectory, item.Enabled, item.LifecycleState,
                    null, item.CreatedAt)).ToArray();
            var next = new MirrorPulseAdapterTopology(current.Installations, instances, current.Roots);
            ValidateTopology(next);
            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = "UPDATE adapter_topology SET payload = $payload WHERE id = 1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateTopology(MirrorPulseAdapterTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(topology.Installations);
        ArgumentNullException.ThrowIfNull(topology.Instances);
        ArgumentNullException.ThrowIfNull(topology.Roots);
        var installations = new Dictionary<InstallId, InstalledAdapter>();
        foreach (InstalledAdapter adapter in topology.Installations)
        {
            Diagnostic[] errors = AdapterManifestValidator.Validate(adapter.Manifest)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (!installations.TryAdd(adapter.InstallId, adapter) || errors.Length > 0)
            {
                throw new InvalidDataException("The Adapter topology contains a duplicate or invalid installation: " +
                    string.Join(", ", errors.Select(error => error.Code)));
            }
        }

        var instances = new Dictionary<InstanceId, AdapterInstance>();
        foreach (AdapterInstance instance in topology.Instances)
        {
            if (!instances.TryAdd(instance.InstanceId, instance) ||
                !installations.TryGetValue(instance.InstallId, out InstalledAdapter? adapter) ||
                adapter.AdapterId != instance.AdapterId)
            {
                throw new InvalidDataException("An Adapter instance has no matching installation or repeats an ID.");
            }
        }

        var rootIds = new HashSet<RootId>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RootRegistration root in topology.Roots)
        {
            if (!rootIds.Add(root.RootId) ||
                !instances.TryGetValue(root.InstanceId, out AdapterInstance? instance) ||
                instance.AdapterId != root.AdapterId)
            {
                throw new InvalidDataException("An Adapter root has no matching instance or repeats an ID.");
            }

            if ((root.State is RootRegistrationState.Active or RootRegistrationState.Disabled) &&
                (!string.Equals(root.Label, root.DirectoryName, StringComparison.Ordinal) ||
                 !labels.Add(root.Label)))
            {
                throw new InvalidDataException("The Adapter topology contains a duplicate first-level Label.");
            }
        }
    }

    private static MirrorPulseAdapterTopology DeserializeTopology(string payload)
    {
        try
        {
            MirrorPulseAdapterTopology topology = JsonSerializer.Deserialize<MirrorPulseAdapterTopology>(
                payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The Adapter topology is empty.");
            ValidateTopology(topology);
            return topology;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Adapter topology is invalid JSON.", exception);
        }
    }

    private sealed record ManifestDocument(
        int SchemaVersion,
        AdapterId AdapterId,
        string Publisher,
        string Version,
        ProtocolVersionRange Protocol,
        Dictionary<string, string> Entrypoints,
        AdapterInstallPolicy InstallPolicy,
        AdapterInstancePolicy InstancePolicy,
        AdapterCapabilities Capabilities,
        string[] Locales,
        string MinimumMirrorPulseVersion,
        AdapterRootDefinition[] RootDefinitions,
        AdapterLocaleMetadata[] LocaleMetadata,
        AdapterConfigurationField[]? ConfigurationFields);

    private sealed class CatalogManifestConverter : JsonConverter<AdapterManifest>
    {
        public override AdapterManifest Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            ManifestDocument document = JsonSerializer.Deserialize<ManifestDocument>(ref reader, options)
                ?? throw new JsonException("The Adapter manifest is missing.");
            return new AdapterManifest(document.SchemaVersion, document.AdapterId,
                document.Publisher, document.Version, document.Protocol, document.Entrypoints,
                document.InstallPolicy, document.InstancePolicy, document.Capabilities,
                document.Locales, document.MinimumMirrorPulseVersion,
                document.RootDefinitions, document.LocaleMetadata,
                document.ConfigurationFields);
        }

        public override void Write(Utf8JsonWriter writer, AdapterManifest value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new ManifestDocument(value.SchemaVersion, value.AdapterId,
                value.Publisher, value.Version, value.Protocol,
                new Dictionary<string, string>(value.Entrypoints, StringComparer.OrdinalIgnoreCase),
                value.InstallPolicy, value.InstancePolicy, value.Capabilities, value.Locales.ToArray(),
                value.MinimumMirrorPulseVersion, value.RootDefinitions.ToArray(),
                value.LocaleMetadata.Values.ToArray(), value.ConfigurationFields.ToArray()), options);
    }

    private sealed class StringValueConverter<T>(Func<string, T> parse) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            parse(reader.GetString() ?? throw new JsonException("An Adapter identifier is missing."));

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value?.ToString());
    }
}
