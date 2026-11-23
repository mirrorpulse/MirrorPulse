using System.Buffers;

namespace MirrorPulse.Core.Packaging;

public sealed record DownloadedAdapterPackage(Uri SourceUri, string PackagePath, long Length);

/// <summary>
/// Downloads an official `.mpadapter` release over HTTPS into a caller-owned path.
/// </summary>
public sealed class OfficialReleaseDownloader
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public OfficialReleaseDownloader(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<DownloadedAdapterPackage> DownloadAsync(
        Uri packageUri,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!IsDownloadUri(packageUri))
        {
            throw new ArgumentException("Official Adapter releases must be downloaded over HTTPS.", nameof(packageUri));
        }

        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".mpadapter", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Official Adapter releases must use the .mpadapter extension.", nameof(destinationPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await GetResponseAsync(packageUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumPackageBytes)
                throw new InvalidDataException("The Adapter update exceeds the download limit.");
            long length;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                length = await CopyBoundedAsync(input, output, cancellationToken).ConfigureAwait(false);
            }

            if (response.Content.Headers.ContentLength is { } expectedLength && expectedLength != length)
                throw new InvalidDataException("The Adapter update download is incomplete.");

            File.Move(temporary, destination, overwrite: true);
            return new DownloadedAdapterPackage(packageUri, destination, new FileInfo(destination).Length);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static bool IsDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Length > 0 && !uri.IsLoopback && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.OriginalString.Length <= 2048;

    private async Task<HttpResponseMessage> GetResponseAsync(Uri location, CancellationToken cancellationToken)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            HttpResponseMessage response = await _httpClient.GetAsync(location, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not { } target || !Uri.TryCreate(location, target, out Uri? next) || !IsDownloadUri(next))
                    throw new InvalidDataException("The Adapter update redirected to an unsupported location.");
                location = next;
            }
        }

        throw new InvalidDataException("The Adapter update exceeded the download redirect limit.");
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long length = 0;
            int bytes;
            while ((bytes = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                length += bytes;
                if (length > MaximumPackageBytes) throw new InvalidDataException("The Adapter update exceeds the download limit.");
                await output.WriteAsync(buffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
            }

            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
