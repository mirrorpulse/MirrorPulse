using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<AdapterInstance> ConfigureInstanceAsync(
        InstanceId instanceId,
        string displayName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyDictionary<string, string> rootLabels,
        CancellationToken cancellationToken = default)
        => await ConfigureInstanceCoreAsync(instanceId, displayName, configuration, rootLabels,
            false, null, null, cancellationToken).ConfigureAwait(false);

    internal Task<AdapterInstance> ConfigureInstanceCredentialAsync(
        InstanceId instanceId, string displayName, IReadOnlyDictionary<string, string> configuration,
        IReadOnlyDictionary<string, string> rootLabels, string? expectedReference, string? newReference,
        CancellationToken cancellationToken)
        => ConfigureInstanceCoreAsync(instanceId, displayName, configuration, rootLabels,
            true, expectedReference, newReference, cancellationToken);

    private async Task<AdapterInstance> ConfigureInstanceCoreAsync(
        InstanceId instanceId, string displayName, IReadOnlyDictionary<string, string> configuration,
        IReadOnlyDictionary<string, string> rootLabels, bool replaceCredential,
        string? expectedReference, string? newReference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(rootLabels);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseAdapterTopology current = payload is null ? new([], [], []) : DeserializeTopology(payload);
            AdapterInstance instance = current.Instances.SingleOrDefault(item => item.InstanceId == instanceId)
                ?? throw new FileNotFoundException("The Adapter instance is not installed.");
            InstalledAdapter installation = current.Installations.Single(item => item.InstallId == instance.InstallId);
            if (replaceCredential)
            {
                instance.Configuration.TryGetValue("credentialReference", out string? currentReference);
                if (!string.Equals(currentReference, expectedReference, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The instance credential changed during this operation.");
                }

                var nextConfiguration = new Dictionary<string, string>(instance.Configuration, StringComparer.Ordinal);
                nextConfiguration.Remove("credentialReference");
                if (newReference is not null)
                {
                    nextConfiguration["credentialReference"] = newReference;
                }

                instance = new AdapterInstance(instance.AdapterId, instance.InstallId, instance.InstanceId,
                    instance.DisplayName, nextConfiguration, newReference is null ? [] : [newReference],
                    instance.FileCacheDirectory, instance.TransferCacheDirectory, instance.Enabled,
                    instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt);
            }
            IReadOnlyDictionary<string, string> validated = AdapterConfigurationFieldValidator.ApplyPatch(
                installation.Manifest, instance, configuration);
            if (rootLabels.Keys.Any(key => !installation.Manifest.RootDefinitions.Any(definition =>
                    string.Equals(definition.Key, key, StringComparison.Ordinal))))
            {
                throw new InvalidDataException("An instance root label refers to an undeclared Adapter root.");
            }

            AdapterInstance updated = new(
                instance.AdapterId, instance.InstallId, instance.InstanceId, displayName.Trim(),
                validated, instance.CredentialReferences, instance.FileCacheDirectory,
                instance.TransferCacheDirectory, instance.Enabled, instance.LifecycleState,
                null, instance.CreatedAt);
            AdapterInstance[] instances = current.Instances.Select(item =>
                item.InstanceId == instanceId ? updated : item).ToArray();
            RootRegistration[] roots = current.Roots.Select(root =>
            {
                if (root.InstanceId != instanceId ||
                    !rootLabels.TryGetValue(root.UniquenessKey, out string? label))
                    return root;
                string value = label.Trim();
                if (value.Length == 0)
                    throw new InvalidDataException("An Adapter root Label cannot be empty.");
                return new RootRegistration(root.AdapterId, root.InstanceId, root.RootId,
                    root.UniquenessKey, value, value, root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
            }).ToArray();
            var next = new MirrorPulseAdapterTopology(current.Installations, instances, roots);
            ValidateTopology(next);
            await ValidateManagedRootNamesAsync(next, cancellationToken).ConfigureAwait(false);
            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = "UPDATE adapter_topology SET payload = $payload WHERE id = 1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates the first runnable instance for an installed Adapter and registers all of its
    /// first-level roots in the same product-catalog transaction.
    /// </summary>
    public async Task<AdapterInstance> CreateInstanceAsync(
        InstallId installId,
        string displayName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyList<string> credentialReferences,
        string fileCacheDirectory,
        string transferCacheDirectory,
        bool enabled = true,
        IReadOnlyDictionary<string, string>? rootLabels = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialReferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileCacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferCacheDirectory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseAdapterTopology current = payload is null ? new([], [], []) : DeserializeTopology(payload);
            InstalledAdapter installation = current.Installations.SingleOrDefault(item => item.InstallId == installId)
                ?? throw new FileNotFoundException("The selected Adapter installation is not registered.");

            int existingInstances = current.Instances.Count(item => item.AdapterId == installation.AdapterId);
            int? maximumInstances = installation.Manifest.InstancePolicy.MaximumInstances;
            if (maximumInstances is not null && existingInstances >= maximumInstances.Value)
            {
                throw new InvalidDataException("The Adapter instance limit has been reached.");
            }

            int? maximumRoots = installation.Manifest.InstancePolicy.MaximumRootDefinitions;
            if (maximumRoots is not null && installation.Manifest.RootDefinitions.Count > maximumRoots.Value)
            {
                throw new InvalidDataException("The Adapter root-definition limit has been exceeded.");
            }

            InstanceId instanceId = InstanceId.New();
            AdapterInstance instance = new(
                installation.AdapterId,
                installation.InstallId,
                instanceId,
                displayName,
                configuration,
                credentialReferences,
                Path.GetFullPath(fileCacheDirectory),
                Path.GetFullPath(transferCacheDirectory),
                enabled,
                enabled ? AdapterLifecycleState.Enabled : AdapterLifecycleState.Disabled,
                null,
                DateTimeOffset.UtcNow);
            RootRegistration[] roots = AdapterRootRegistrationMapper.MapAll(
                installation.AdapterId,
                instanceId,
                installation.Manifest.RootDefinitions,
                enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled,
                identityScope: installation.Manifest.Protocol.Maximum >= 2 ? RootIdentityScope.InstanceRoot : RootIdentityScope.LegacyInstance)
                .Select(root =>
                {
                    if (rootLabels is null || !rootLabels.TryGetValue(root.UniquenessKey, out string? label))
                    {
                        return root;
                    }

                    return new RootRegistration(root.AdapterId, root.InstanceId, root.RootId,
                        root.UniquenessKey, label, label, root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
                }).ToArray();
            if (rootLabels is not null && rootLabels.Keys.Any(key =>
                !installation.Manifest.RootDefinitions.Any(definition =>
                    string.Equals(definition.Key, key, StringComparison.Ordinal))))
            {
                throw new InvalidDataException("An instance root label refers to an undeclared Adapter root.");
            }
            var next = new MirrorPulseAdapterTopology(
                [.. current.Installations],
                [.. current.Instances, instance],
                [.. current.Roots, .. roots]);
            ValidateTopology(next);
            await ValidateManagedRootNamesAsync(next, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(instance.FileCacheDirectory);
            Directory.CreateDirectory(instance.TransferCacheDirectory);

            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = """
                INSERT INTO adapter_topology (id, payload) VALUES (1, $payload)
                ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;
                """;
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return instance;
        }
        finally
        {
            _gate.Release();
        }
    }
}
