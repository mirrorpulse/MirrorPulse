using System.Net;
using System.Net.Http;
using MirrorPulse.Core.Packaging;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class OfficialReleaseDownloaderTests
{
    [TestMethod]
    public async Task DownloaderStreamsAnHttpsPackageToTheRequestedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mirrorpulse-release-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new StaticResponseHandler("package-content"));
            var destination = Path.Combine(root, "downloaded.mpadapter");
            var result = await new OfficialReleaseDownloader(client).DownloadAsync(
                new Uri("https://github.com/MirrorPulse/example/releases/download/v1.0.0/example.mpadapter"),
                destination);

            Assert.AreEqual(destination, result.PackagePath);
            Assert.AreEqual("package-content", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(15, result.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DownloaderRejectsNonHttpsUris()
    {
        using var client = new HttpClient(new StaticResponseHandler("package"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new OfficialReleaseDownloader(client).DownloadAsync(
                new Uri("http://example.test/package.mpadapter"),
                Path.Combine(Path.GetTempPath(), "package.mpadapter")));
    }

    [TestMethod]
    public async Task DownloaderRejectsAnInsecureRedirectBeforeOpeningItsTarget()
    {
        var root = Directory.CreateTempSubdirectory("mirrorpulse-release-");
        try
        {
            var handler = new InsecureRedirectHandler();
            using var client = new HttpClient(handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new OfficialReleaseDownloader(client).DownloadAsync(
                new Uri("https://example.test/package.mpadapter"), Path.Combine(root.FullName, "download.mpadapter")));
            Assert.AreEqual(1, handler.Calls);
            Assert.IsEmpty(root.EnumerateFileSystemInfos());
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class InsecureRedirectHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://example.test/package.mpadapter") },
            });
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _content;

        public StaticResponseHandler(string content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content),
                RequestMessage = request,
            });
    }
}
