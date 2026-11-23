using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Host;

public enum AdapterUpdateAvailability { Unsupported, Offline, Current, Available, AlreadyInstalled }

public sealed record AdapterUpdateCheck(AdapterUpdateAvailability Availability, string CurrentVersion,
    string? AvailableVersion = null, Guid? OfferId = null, string? Source = null);

public sealed record AdapterUpdateInstallation(string InstallId, string Version);

/// <summary>Checks the selected deployment's Worker and owns the approval to acquire its offered package.</summary>
public sealed class AdapterUpdateCoordinator(MirrorPulseProductCatalog catalog,
    AdapterInstanceProcessSupervisor workers, string dataRoot, string runtimeIdentifier)
{
    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
    private readonly Dictionary<Guid, PendingUpdate> _offers = [];

    public async Task<AdapterUpdateCheck> CheckAsync(InstanceId instanceId, CancellationToken cancellationToken = default)
    {
        var (instance, installed) = await ReadDeploymentAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!installed.IsSigned || installed.Manifest.Presentation is null)
            return new(AdapterUpdateAvailability.Unsupported, installed.Version);
        if (!instance.Enabled) return new(AdapterUpdateAvailability.Offline, installed.Version);

        JsonElement response;
        try
        {
            response = await workers.CheckUpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return new(AdapterUpdateAvailability.Unsupported, installed.Version);
        }
        catch (AdapterWorkerOperationException exception) when (exception.FailureCode is "Offline" or "Disconnected")
        {
            return new(AdapterUpdateAvailability.Offline, installed.Version);
        }

        AdapterPackageUpdateOffer? offer = ParseOffer(response, installed);
        if (offer is null) return new(AdapterUpdateAvailability.Current, installed.Version);

        MirrorPulseAdapterTopology current = await catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        RequireCurrentDeployment(current, instanceId, installed.InstallId);
        if (current.Installations.Any(item => item.AdapterId == installed.AdapterId && item.Version == offer.Version))
            return new(AdapterUpdateAvailability.AlreadyInstalled, installed.Version, offer.Version);

        Guid offerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var update = new PendingUpdate(instanceId, installed.InstallId, offer, now.AddMinutes(5));
        lock (_offers)
        {
            foreach (Guid expired in _offers.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                _offers.Remove(expired);
            if (_offers.Count >= 128) _offers.Remove(_offers.MinBy(pair => pair.Value.ExpiresAt).Key);
            _offers.Add(offerId, update);
        }

        return new(AdapterUpdateAvailability.Available, installed.Version, offer.Version, offerId, DescribeSource(offer.Location));
    }

    public async Task<AdapterUpdateInstallation> InstallAsync(Guid offerId, CancellationToken cancellationToken = default)
    {
        PendingUpdate update;
        lock (_offers)
        {
            if (!_offers.Remove(offerId, out PendingUpdate? pending) || pending.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ArgumentException("The Adapter update offer has expired. Check for updates again.", nameof(offerId));
            update = pending;
        }

        MirrorPulseAdapterTopology current = await catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        RequireCurrentDeployment(current, update.InstanceId, update.InstallId);
        InstalledAdapter? existing = current.Installations.FirstOrDefault(item =>
            item.AdapterId.ToString() == update.Offer.AdapterId && item.Version == update.Offer.Version);
        if (existing is not null) return new(existing.InstallId.ToString(), existing.Version);

        Uri location = update.Offer.Location;
        var binding = new MirrorPulseProductCatalog.AdapterUpdateBinding(update.InstanceId, update.InstallId,
            new(update.Offer.AdapterId, update.Offer.Version), location.IsFile ? AdapterInstallSource.LocalFile : AdapterInstallSource.OfficialRelease,
            DescribeSource(location));
        string installationRoot = Path.Combine(dataRoot, "adapters", "installed");
        InstalledAdapter installed;
        if (location.IsFile)
        {
            string package = location.LocalPath;
            RequireLocalFile(package);
            if (File.Exists(package + ".signature.json")) RequireLocalFile(package + ".signature.json");
            installed = await catalog.InstallSignedAdapterUpdateAsync(package, installationRoot, runtimeIdentifier,
                binding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string directory = Path.Combine(dataRoot, "adapters", "downloads", offerId.ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string package = Path.Combine(directory, "update.mpadapter");
                var downloader = new OfficialReleaseDownloader(DownloadClient);
                await downloader.DownloadAsync(location, package, cancellationToken).ConfigureAwait(false);
                installed = await catalog.InstallSignedAdapterUpdateAsync(package, installationRoot, runtimeIdentifier,
                    binding, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new IOException("The Adapter update could not be downloaded.");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return new(installed.InstallId.ToString(), installed.Version);
    }

    internal static AdapterPackageUpdateOffer? ParseOffer(JsonElement response, InstalledAdapter installed)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("offer", out JsonElement offer))
            throw new InvalidDataException("The Adapter returned an invalid update result.");
        if (offer.ValueKind == JsonValueKind.Null) return null;

        string? adapterId = ReadString(offer, "adapterId");
        string? version = ReadString(offer, "version");
        string? locationText = ReadString(offer, "location");
        if (adapterId is null || adapterId != installed.AdapterId.ToString() ||
            !AdapterPackageVersion.TryParse(version, out AdapterPackageVersion? newer) || newer.IsLegacyFourPart ||
            !AdapterPackageVersion.TryParse(installed.Version, out AdapterPackageVersion? previous) || newer.CompareTo(previous) <= 0 ||
            locationText is null || locationText.Length > 2048 || locationText.Any(char.IsControl) ||
            !Uri.TryCreate(locationText, UriKind.Absolute, out Uri? location) ||
            !(OfficialReleaseDownloader.IsDownloadUri(location) || IsLocalPackage(location)))
            throw new InvalidDataException("The Adapter update offer has an invalid identity, version or location.");
        return new(adapterId, version!, location);
    }

    private static bool IsLocalPackage(Uri location) => location.IsFile && !location.IsUnc && location.Host.Length == 0 &&
        location.Query.Length == 0 && location.Fragment.Length == 0 && location.LocalPath is { Length: >= 4 } path &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' && !path.AsSpan(2).Contains(':') &&
        !path.Any(char.IsControl) && string.Equals(Path.GetExtension(path), ".mpadapter", StringComparison.OrdinalIgnoreCase);

    private static void RequireLocalFile(string path)
    {
        string root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("The Adapter update must be a regular local package file.");
        for (string? directory = Path.GetDirectoryName(path); directory is not null && directory != root;
            directory = Path.GetDirectoryName(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Adapter update traverses a redirected directory.");
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string DescribeSource(Uri location) => location.IsFile ? location.LocalPath : location.GetLeftPart(UriPartial.Path);

    private async Task<(AdapterInstance Instance, InstalledAdapter Installation)> ReadDeploymentAsync(InstanceId instanceId,
        CancellationToken cancellationToken)
    {
        MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        AdapterInstance instance = topology.Instances.SingleOrDefault(item => item.InstanceId == instanceId) ??
            throw new ArgumentException("The Adapter deployment does not exist.", nameof(instanceId));
        return (instance, topology.Installations.Single(item => item.InstallId == instance.InstallId));
    }

    private static void RequireCurrentDeployment(MirrorPulseAdapterTopology topology, InstanceId instanceId, InstallId installId)
    {
        if (!topology.Instances.Any(item => item.InstanceId == instanceId && item.InstallId == installId && item.Enabled))
            throw new InvalidDataException("The Adapter deployment changed. Check for updates again.");
    }

    private sealed record PendingUpdate(InstanceId InstanceId, InstallId InstallId, AdapterPackageUpdateOffer Offer, DateTimeOffset ExpiresAt);
}
