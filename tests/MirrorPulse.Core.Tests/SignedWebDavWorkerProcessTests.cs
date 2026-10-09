using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedWebDavWorkerProcessTests
{
    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task SignedWorkerPreservesEtagAndRejectsStaleUploadWithoutOverwritingRemote()
    {
        string? aggregateDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        if (string.IsNullOrWhiteSpace(aggregateDirectory))
        {
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");
        }

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(aggregateDirectory, "official-adapters.manifest.json")));
        JsonElement webDav = manifest.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("adapterId").GetString() == "com.mirrorpulse.adapter.webdav");
        string packageDirectory = Path.Combine(aggregateDirectory, "com.mirrorpulse.adapter.webdav");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-signed-webdav", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        await using var server = new WebDavFixture();
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installation = await catalog.InstallSignedAdapterAsync(
                Path.Combine(packageDirectory, webDav.GetProperty("package").GetString()!),
                Path.Combine(packageDirectory, webDav.GetProperty("signature").GetString()!),
                Path.Combine(root, "installed"), "win-x64");
            AdapterInstance instance = await catalog.CreateInstanceAsync(installation.InstallId,
                "WebDAV test", new Dictionary<string, string> { ["endpoint"] = server.Endpoint.AbsoluteUri },
                [], Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"));
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            string rootKey = topology.Roots.Single(binding => binding.InstanceId == instance.InstanceId).UniquenessKey;
            string diagnostics = Path.Combine(root, "diagnostics");
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                new WindowsCredentialManagerStore(), diagnosticsDirectory: diagnostics);
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(
                    instance.InstanceId, timeout.Token);
                if (state?.Phase == "Connected")
                {
                    break;
                }

                if (state?.Phase is "Worker failed" or "Worker error")
                {
                    Assert.Fail($"The signed WebDAV Worker failed: {state.LastErrorCode}. " +
                        await WorkerFailureTestDiagnostics.ReadAsync(diagnostics));
                }

                await Task.Delay(50, timeout.Token);
            }

            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor,
                new MirrorPulseAdapterDirectoryPageSource(supervisor));
            CloudProviderDirectoryPage top = await provider.FetchChildrenAsync(paths.SyncRootPath,
                ReadOnlyMemory<byte>.Empty, null, timeout.Token);
            CloudPlaceholderSpec adapterRoot = top.Children.Single();
            CloudProviderDirectoryPage page = await provider.FetchChildrenAsync(
                Path.Combine(paths.SyncRootPath, adapterRoot.Name), adapterRoot.Identity.Encode(),
                null, timeout.Token);
            CloudFilePlaceholderSpec note = page.Children.OfType<CloudFilePlaceholderSpec>()
                .Single(item => item.Name == "note.txt");
            Assert.AreEqual("\"v1\"", note.Identity.RemoteRevision);

            await using Stream read = await provider.OpenReadAsync(
                Path.Combine(paths.SyncRootPath, adapterRoot.Name, note.Name), note.Identity.Encode(),
                note.Length, 0, note.Length, timeout.Token);
            byte[] original = new byte[note.Length];
            await read.ReadExactlyAsync(original, timeout.Token);
            CollectionAssert.AreEqual(server.Content, original);

            byte[] replacement = Encoding.UTF8.GetBytes("new content");
            await using (var stale = new MemoryStream(replacement, writable: false))
            {
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () => await supervisor.UploadAsync(
                    new MirrorPulseWorkerUploadRequest(instance.InstanceId, "note.txt", "\"stale\"",
                        stale, replacement.Length, RootKey: rootKey), timeout.Token));
            }

            CollectionAssert.AreEqual(original, server.Content);
            Assert.AreEqual(0, server.SuccessfulPuts);

            await using (var current = new MemoryStream(replacement, writable: false))
            {
                string revision = await supervisor.UploadAsync(new MirrorPulseWorkerUploadRequest(
                    instance.InstanceId, "note.txt", note.Identity.RemoteRevision,
                    current, replacement.Length, RootKey: rootKey), timeout.Token);
                Assert.AreEqual("\"v2\"", revision);
            }

            CollectionAssert.AreEqual(replacement, server.Content);
            Assert.AreEqual(1, server.SuccessfulPuts);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class WebDavFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private byte[] _content = Encoding.UTF8.GetBytes("original content");
        private int _revision = 1;

        public WebDavFixture()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Endpoint = new Uri($"http://localhost:{port}/dav/");
            _listener.Prefixes.Add(Endpoint.AbsoluteUri);
            _listener.Start();
            _loop = RunAsync();
        }

        public Uri Endpoint { get; }

        public byte[] Content => _content.ToArray();

        public int SuccessfulPuts { get; private set; }

        private async Task RunAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    await HandleAsync(context);
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                if (request.HttpMethod == "PROPFIND" && request.Url?.AbsolutePath == "/dav/")
                {
                    string xml = $"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <d:multistatus xmlns:d="DAV:">
                          <d:response><d:href>/dav/</d:href><d:propstat><d:prop>
                            <d:resourcetype><d:collection/></d:resourcetype>
                          </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                          <d:response><d:href>/dav/note.txt</d:href><d:propstat><d:prop>
                            <d:resourcetype/><d:getcontentlength>{_content.Length}</d:getcontentlength>
                            <d:getetag>"v{_revision}"</d:getetag>
                          </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                        </d:multistatus>
                        """;
                    byte[] body = Encoding.UTF8.GetBytes(xml);
                    response.StatusCode = 207;
                    response.ContentType = "application/xml";
                    response.ContentLength64 = body.Length;
                    await response.OutputStream.WriteAsync(body);
                }
                else if (request.Url?.AbsolutePath == "/dav/note.txt" &&
                    request.HttpMethod is "HEAD" or "GET")
                {
                    response.StatusCode = 200;
                    response.Headers["ETag"] = $"\"v{_revision}\"";
                    response.ContentLength64 = _content.Length;
                    if (request.HttpMethod == "GET")
                    {
                        if (request.Headers["If-Match"] is { } expected && expected != $"\"v{_revision}\"")
                        {
                            response.StatusCode = 412;
                            response.ContentLength64 = 0;
                            return;
                        }

                        string? range = request.Headers["Range"];
                        if (range is null)
                        {
                            if (request.Headers["If-Match"] != $"\"v{_revision}\"")
                                throw new InvalidDataException("The fixture expects a conditional content proof.");
                            await response.OutputStream.WriteAsync(_content);
                            return;
                        }

                        if (!range.StartsWith("bytes=", StringComparison.Ordinal))
                            throw new InvalidDataException("The fixture expects a bounded range request.");
                        string[] bounds = range[6..].Split('-');
                        int start = int.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
                        int end = int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
                        response.StatusCode = 206;
                        response.Headers["Content-Range"] = $"bytes {start}-{end}/{_content.Length}";
                        response.ContentLength64 = end - start + 1;
                        await response.OutputStream.WriteAsync(_content.AsMemory(start, end - start + 1));
                    }
                }
                else if (request.Url?.AbsolutePath == "/dav/note.txt" && request.HttpMethod == "PUT")
                {
                    if (request.Headers["If-Match"] != $"\"v{_revision}\"")
                    {
                        response.StatusCode = 412;
                    }
                    else
                    {
                        using var content = new MemoryStream();
                        await request.InputStream.CopyToAsync(content);
                        _content = content.ToArray();
                        _revision++;
                        SuccessfulPuts++;
                        response.StatusCode = 204;
                    }
                }
                else
                {
                    response.StatusCode = 404;
                }
            }
            finally
            {
                response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _loop;
        }
    }
}
