using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Imports the official signed packages declared by this edition's build profile.</summary>
    public async Task InstallEditionAdaptersAsync(string hostDirectory, string installationRoot,
        string runtimeIdentifier, CancellationToken cancellationToken = default)
    {
        if (_edition.DefaultAdapterIds.Count == 0)
            return;

        using RSA trustedKey = MirrorPulseOfficialAdapterTrust.CreatePublicKey();
        await InstallEditionAdaptersAsync(hostDirectory, installationRoot, runtimeIdentifier, trustedKey,
            MirrorPulseOfficialAdapterTrust.Signer, cancellationToken).ConfigureAwait(false);
    }

    internal async Task InstallEditionAdaptersAsync(string hostDirectory, string installationRoot,
        string runtimeIdentifier, RSA trustedKey, string signer, CancellationToken cancellationToken = default)
    {
        ValidateEditionTopology(await ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false));
        foreach (AdapterId adapterId in _edition.DefaultAdapterIds)
        {
            string path = Path.Combine(hostDirectory, "edition-adapters", $"adapter.{adapterId}.mpadapter");
            // Keep a read handle open through verification so the receipt binds to these bytes.
            await using var package = File.OpenRead(path);
            var digest = new Sha256Digest(Convert.ToHexString(
                await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false)));
            if (await HasEditionReceiptAsync(digest, cancellationToken).ConfigureAwait(false))
                continue;

            SignedProcessAdapterInstallation payload = await SignedProcessAdapterInstaller.InstallAsync(
                path, path + ".signature.json", installationRoot, runtimeIdentifier, trustedKey, signer,
                cancellationToken).ConfigureAwait(false);
            bool retained = false;
            try
            {
                if (payload.Manifest.AdapterId != adapterId || payload.PackageSha256 != digest)
                    throw new InvalidDataException("The bundled Adapter does not match the edition profile.");
                var installation = new InstalledAdapter(payload.Manifest, InstallId.New(), payload.InstallationDirectory,
                    digest, AdapterInstallSource.EditionBundle, _edition.Id, true, DateTimeOffset.UtcNow,
                    AdapterLifecycleState.Installed);
                retained = await AddInstallationAsync(installation, updateBinding: null, cancellationToken,
                    recordEditionReceipt: true).ConfigureAwait(false);
            }
            finally
            {
                if (!retained)
                    Directory.Delete(payload.InstallationDirectory, recursive: true);
            }
        }
    }

    private async Task<bool> HasEditionReceiptAsync(Sha256Digest digest, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand create = _connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS edition_adapter_receipts (
                    package_sha256 TEXT PRIMARY KEY NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand read = _connection.CreateCommand();
            read.CommandText = "SELECT 1 FROM edition_adapter_receipts WHERE package_sha256 = $digest;";
            read.Parameters.AddWithValue("$digest", digest.Hexadecimal);
            return await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateEditionTopology(MirrorPulseAdapterTopology topology)
    {
        if (_edition.DirectAdapterId is not { } adapterId)
            return;

        if (topology.Installations.Any(item => item.AdapterId != adapterId || !item.IsSigned ||
            item.Manifest.Presentation is null || item.Manifest.RootDefinitions.Count != 1))
            throw new InvalidDataException("This edition accepts only its signed single-root Adapter.");

        if (topology.Instances.Count > 1 || topology.Roots.Count > 1)
            throw new InvalidDataException("This edition supports one Adapter deployment and one root.");
    }

    public void EnsureEditionCanRemoveInstallation(MirrorPulseAdapterTopology topology, InstallId installId)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (_edition.DirectAdapterId is { } adapterId && topology.Installations.Any(item => item.InstallId == installId &&
            item.AdapterId == adapterId) && topology.Installations.Count(item => item.AdapterId == adapterId) == 1)
            throw new InvalidOperationException("This edition requires its bundled Adapter.");
    }
}
