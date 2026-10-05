using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Host;

/// <summary>Creates a configured Adapter instance while keeping its secret out of the product catalog.</summary>
public sealed class MirrorPulseAdapterInstanceProvisioner
{
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
        CancellationToken cancellationToken = default)
    {
        await _catalog.CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { _catalog.CredentialGate.Release(); }
    }

    private async Task<AdapterInstance> CreateCoreAsync(
        MirrorPulseCreateInstanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        ArgumentNullException.ThrowIfNull(request.RootLabels);
        InstallId installId = InstallId.Parse(request.InstallId);
        InstalledAdapter installation = await _catalog.FindAsync(installId, cancellationToken)
            .ConfigureAwait(false) ?? throw new FileNotFoundException(
                "The selected Adapter installation is not registered.");
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

            string instanceCacheRoot = Path.Combine(_dataRoot, "adapters", "instances",
                Guid.NewGuid().ToString("D"));
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
        CancellationToken cancellationToken = default)
    {
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
            if (secret is null && !removeCredential)
            {
                return await _catalog.ConfigureInstanceAsync(instanceId, displayName, patch, rootLabels,
                    cancellationToken).ConfigureAwait(false);
            }

            MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
                .ConfigureAwait(false);
            AdapterInstance instance = topology.Instances.Single(item => item.InstanceId == instanceId);
            InstalledAdapter installation = topology.Installations.Single(item => item.InstallId == instance.InstallId);
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
        => new(id, CredentialKind.Password, instance.AdapterId.ToString(), CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
}
