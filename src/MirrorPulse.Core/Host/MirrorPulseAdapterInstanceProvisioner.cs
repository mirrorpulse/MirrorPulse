using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Host;

/// <summary>Creates a configured Adapter instance while keeping its secret out of the product catalog.</summary>
public sealed class MirrorPulseAdapterInstanceProvisioner
{
    private static readonly Dictionary<string, string?> NoSecretChanges = new(StringComparer.Ordinal);
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly ISecureCredentialStore _credentials;
    private readonly string _dataRoot;

    public MirrorPulseAdapterInstanceProvisioner(
        MirrorPulseProductCatalog catalog,
        ISecureCredentialStore credentials,
        string dataRoot)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public async Task<AdapterInstance> CreateAsync(
        MirrorPulseCreateInstanceRequest request,
        CancellationToken cancellationToken = default) => await CreateAsync(request, null, cancellationToken).ConfigureAwait(false);

    internal async Task<AdapterInstance> CreateAsync(MirrorPulseCreateInstanceRequest request, InstanceId? reservedId,
        CancellationToken cancellationToken)
    {
        await _catalog.CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateCoreAsync(request, reservedId, cancellationToken).ConfigureAwait(false); }
        finally { _catalog.CredentialGate.Release(); }
    }

    private async Task<AdapterInstance> CreateCoreAsync(
        MirrorPulseCreateInstanceRequest request, InstanceId? reservedId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        ArgumentNullException.ThrowIfNull(request.RootLabels);
        InstallId installId = InstallId.Parse(request.InstallId);
        InstalledAdapter installation = await _catalog.FindAsync(installId, cancellationToken)
            .ConfigureAwait(false) ?? throw new FileNotFoundException(
                "The selected Adapter installation is not registered.");
        string instanceCacheRoot = Path.Combine(_dataRoot, "adapters", "instances", Guid.NewGuid().ToString("D"));
        if (installation.Manifest.Presentation is { } presentation)
        {
            if (request.Secret is not null)
                throw new InvalidDataException("Use named password fields for this Adapter.");
            var changes = request.SecretFields ?? NoSecretChanges;
            var signedConfiguration = SignedAdapterFormValidator.Apply(presentation, null, request.Configuration, changes);
            return await SaveSignedFormAsync(installation.AdapterId, null, changes, (bindings, token) =>
                _catalog.CreateSignedAdapterInstanceAsync(installId, request.DisplayName, signedConfiguration,
                    Path.Combine(instanceCacheRoot, "files"), Path.Combine(instanceCacheRoot, "transfers"),
                    request.Enabled, request.RootLabels, bindings, reservedId, token), cancellationToken).ConfigureAwait(false);
        }

        if (request.SecretFields is not null)
            throw new InvalidDataException("This Adapter does not declare named password fields.");

        var configuration = new Dictionary<string, string>(
            AdapterConfigurationFieldValidator.ValidateAndApplyDefaults(
                installation.Manifest, request.Configuration, request.Secret), StringComparer.Ordinal);

        var references = new List<string>();
        CredentialReference? credential = null;
        if (!string.IsNullOrEmpty(request.Secret))
        {
            string referenceId = Guid.NewGuid().ToString("D");
            credential = new CredentialReference(referenceId, CredentialKind.Password,
                installation.AdapterId.ToString(), CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
            configuration["credentialReference"] = referenceId;
            references.Add(referenceId);
        }

        try
        {
            if (credential is not null)
            {
                await _catalog.ScheduleCredentialCleanupAsync(credential, cancellationToken).ConfigureAwait(false);
                await SaveSecretAsync(credential, request.Secret!, cancellationToken).ConfigureAwait(false);
            }

            AdapterInstance instance = await _catalog.CreateInstanceAsync(installId, request.DisplayName,
                configuration, references, Path.Combine(instanceCacheRoot, "files"),
                Path.Combine(instanceCacheRoot, "transfers"), request.Enabled, request.RootLabels,
                cancellationToken).ConfigureAwait(false);
            await RecoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return instance;
        }
        catch
        {
            if (credential is not null)
            {
                await RecoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Rotates or removes a credential using fresh references and a recoverable cleanup outbox.</summary>
    public async Task<AdapterInstance> ConfigureAsync(
        InstanceId instanceId, string displayName, IReadOnlyDictionary<string, string> patch,
        IReadOnlyDictionary<string, string> rootLabels, string? secret = null, bool removeCredential = false,
        IReadOnlyDictionary<string, string?>? secretFields = null, Guid? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(rootLabels);
        if (removeCredential && secret is not null)
        {
            throw new InvalidDataException("Credential replacement and removal cannot be combined.");
        }

        if (secret is not null && secret.Length == 0)
        {
            throw new InvalidDataException("A replacement credential cannot be empty.");
        }

        await _catalog.CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
                .ConfigureAwait(false);
            AdapterInstance instance = topology.Instances.SingleOrDefault(item => item.InstanceId == instanceId)
                ?? throw new FileNotFoundException("The Adapter instance is not installed.");
            InstalledAdapter installation = topology.Installations.Single(item => item.InstallId == instance.InstallId);
            if (installation.Manifest.Presentation is { } presentation)
            {
                if (secret is not null || removeCredential)
                    throw new InvalidDataException("Use named password fields for this Adapter.");
                if (expectedRevision is null)
                    throw new InvalidDataException("An Adapter configuration revision is required.");
                if (expectedRevision != (instance.ConfigurationRevision ?? Guid.Empty))
                    throw new AdapterConfigurationRevisionException();

                var changes = secretFields ?? NoSecretChanges;
                var signedConfiguration = SignedAdapterFormValidator.Apply(presentation, instance, patch, changes);
                return await SaveSignedFormAsync(installation.AdapterId, instance, changes, (bindings, token) =>
                    _catalog.ConfigureSignedAdapterFormAsync(instanceId, displayName, signedConfiguration, rootLabels,
                        new(installation.InstallId, expectedRevision.Value, bindings), token),
                    cancellationToken).ConfigureAwait(false);
            }

            if (secretFields is not null || expectedRevision is not null)
                throw new InvalidDataException("This Adapter does not declare a signed configuration form.");
            if (secret is null && !removeCredential)
            {
                return await _catalog.ConfigureInstanceAsync(instanceId, displayName, patch, rootLabels,
                    cancellationToken).ConfigureAwait(false);
            }

            AdapterConfigurationFieldValidator.ApplyPatch(installation.Manifest, instance, patch);
            instance.Configuration.TryGetValue("credentialReference", out string? previousReference);
            CredentialReference? candidate = secret is null ? null : Reference(Guid.NewGuid().ToString("D"), instance);
            if (previousReference is not null)
            {
                await _catalog.ScheduleCredentialCleanupAsync(Reference(previousReference, instance), cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                if (candidate is not null)
                {
                    await _catalog.ScheduleCredentialCleanupAsync(candidate, cancellationToken).ConfigureAwait(false);
                    await SaveSecretAsync(candidate, secret!, cancellationToken).ConfigureAwait(false);
                }

                AdapterInstance updated = await _catalog.ConfigureInstanceCredentialAsync(instanceId, displayName,
                    patch, rootLabels, previousReference, candidate?.ReferenceId, cancellationToken).ConfigureAwait(false);
                await RecoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return updated;
            }
            catch
            {
                await RecoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _catalog.CredentialGate.Release(); }
    }

    private async Task<AdapterInstance> SaveSignedFormAsync(AdapterId adapterId, AdapterInstance? instance,
        IReadOnlyDictionary<string, string?> secretChanges,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<AdapterInstance>> commit,
        CancellationToken cancellationToken)
    {
        var bindings = instance?.CredentialBindings is { } previous
            ? new Dictionary<string, string>(previous, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach ((string key, string? value) in secretChanges)
            {
                if (bindings.Remove(key, out string? retired))
                    await _catalog.ScheduleCredentialCleanupAsync(Reference(retired, adapterId), cancellationToken)
                        .ConfigureAwait(false);
                if (value is null) continue;

                CredentialReference candidate = Reference(Guid.NewGuid().ToString("D"), adapterId);
                await _catalog.ScheduleCredentialCleanupAsync(candidate, cancellationToken).ConfigureAwait(false);
                await SaveSecretAsync(candidate, value, cancellationToken).ConfigureAwait(false);
                bindings.Add(key, candidate.ReferenceId);
            }

            return await commit(bindings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (secretChanges.Count > 0)
                await RecoverCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Retries interrupted cleanup without deleting any currently referenced credential.</summary>
    public async Task<int> RecoverPendingCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RecoverCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _catalog.CredentialGate.Release(); }
    }

    private async Task<int> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        var active = topology.Instances.SelectMany(instance => instance.CredentialReferences)
            .ToHashSet(StringComparer.Ordinal);
        int pending = 0;
        foreach (CredentialReference reference in await _catalog.ReadCredentialCleanupAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!active.Contains(reference.ReferenceId))
            {
                try { await _credentials.DeleteAsync(reference, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Keep only the opaque pointer for retry. Store exception text may contain secret material.
                    pending++;
                    continue;
                }
            }

            await _catalog.CompleteCredentialCleanupAsync(reference.ReferenceId, cancellationToken).ConfigureAwait(false);
        }

        return pending;
    }

    private async Task SaveSecretAsync(CredentialReference reference, string value, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { await _credentials.SaveAsync(reference, bytes, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new IOException("The credential could not be saved."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static CredentialReference Reference(string id, AdapterInstance instance)
        => Reference(id, instance.AdapterId);

    private static CredentialReference Reference(string id, AdapterId adapterId)
        => new(id, CredentialKind.Password, adapterId.ToString(), CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
}
