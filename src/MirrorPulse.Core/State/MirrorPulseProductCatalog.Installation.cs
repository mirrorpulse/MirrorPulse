using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<InstalledAdapter> RemoveInstallationAsync(
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
            MirrorPulseAdapterTopology current = payload is null ? new([], [], []) : DeserializeTopology(payload);
            InstalledAdapter installation = current.Installations.SingleOrDefault(item => item.InstallId == installId)
                ?? throw new FileNotFoundException("The selected Adapter installation is not registered.");
            if (current.Instances.Any(instance => instance.InstallId == installId))
                throw new InvalidOperationException("The Adapter installation is still referenced by an instance.");

            var next = new MirrorPulseAdapterTopology(
                current.Installations.Where(item => item.InstallId != installId).ToArray(),
                current.Instances, current.Roots);
            ValidateTopology(next);
            await ValidateManagedRootNamesAsync(next, cancellationToken).ConfigureAwait(false);
            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = "UPDATE adapter_topology SET payload = $payload WHERE id = 1;";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return installation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Verifies and installs an official Adapter with MP's built-in trust anchor.</summary>
    public async Task<InstalledAdapter> InstallSignedAdapterAsync(
        string packagePath,
        string signaturePath,
        string installationRoot,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default)
    {
        using RSA trustedKey = MirrorPulseOfficialAdapterTrust.CreatePublicKey();
        return await InstallSignedAdapterAsync(packagePath, signaturePath, installationRoot,
            runtimeIdentifier, trustedKey, MirrorPulseOfficialAdapterTrust.Signer,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies a signed package, then atomically records its installed payload in the Host catalog.</summary>
    public async Task<InstalledAdapter> InstallSignedAdapterAsync(
        string packagePath,
        string signaturePath,
        string installationRoot,
        string runtimeIdentifier,
        RSA trustedKey,
        string trustedSigner,
        CancellationToken cancellationToken = default)
    {
        SignedProcessAdapterInstallation installation = await SignedProcessAdapterInstaller.InstallAsync(
            packagePath, signaturePath, installationRoot, runtimeIdentifier, trustedKey,
            trustedSigner, cancellationToken).ConfigureAwait(false);
        try
        {
            AdapterManifest manifest = await AdapterPackageManifestReader.ReadAsync(
                Path.Combine(installation.InstallationDirectory, "manifest.json"), cancellationToken)
                .ConfigureAwait(false);
            if (manifest.AdapterId.ToString() != installation.AdapterId ||
                manifest.Version != installation.Version)
            {
                throw new InvalidDataException("The installed Adapter manifest does not match the signed package.");
            }

            await using var package = File.OpenRead(packagePath);
            byte[] hash = await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false);
            var adapter = new InstalledAdapter(manifest, InstallId.New(),
                installation.InstallationDirectory, new Sha256Digest(Convert.ToHexString(hash)),
                AdapterInstallSource.LocalFile, Path.GetFullPath(packagePath), true,
                DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            await AddInstallationAsync(adapter, cancellationToken).ConfigureAwait(false);
            return adapter;
        }
        catch
        {
            Directory.Delete(installation.InstallationDirectory, recursive: true);
            throw;
        }
    }

    /// <summary>Appends one installed package without replacing existing instances or first-level roots.</summary>
    public async Task AddInstallationAsync(
        InstalledAdapter installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id = 1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseAdapterTopology current = payload is null ? new([], [], []) : DeserializeTopology(payload);
            InstalledAdapter[] existing = current.Installations
                .Where(item => item.AdapterId == installation.AdapterId).ToArray();
            if (current.Installations.Any(item => item.InstallId == installation.InstallId ||
                string.Equals(item.InstallationDirectory, installation.InstallationDirectory,
                    StringComparison.OrdinalIgnoreCase)) ||
                !AdapterInstallationLimitValidator.Validate(installation.Manifest, existing.Length).Allowed ||
                existing.Any(item => !AdapterInstallationLimitValidator.Validate(
                    item.Manifest, existing.Length).Allowed))
            {
                throw new InvalidDataException("The Adapter installation ID, directory, or installation limit conflicts.");
            }

            var next = new MirrorPulseAdapterTopology(
                [.. current.Installations, installation], current.Instances, current.Roots);
            ValidateTopology(next);
            await ValidateManagedRootNamesAsync(next, cancellationToken).ConfigureAwait(false);
            await using SqliteCommand update = _connection.CreateCommand();
            update.CommandText = """
                INSERT INTO adapter_topology (id, payload) VALUES (1, $payload)
                ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;
                """;
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, TopologyJsonOptions));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
