using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace MirrorPulse.Core.Tests;

internal sealed class SignedWebDavV2HttpFixture : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly string _authorization;
    private bool _disposed;
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, bool> Directories { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> Locks { get; } = new(StringComparer.Ordinal);
    public bool WeakTags { get; set; }
    public bool FailedPropstat { get; set; }
    public bool FailedOwnPropstat { get; set; }
    public bool InvalidLease { get; set; }
    public bool PartialMutation { get; set; }
    public bool DropMutationAck { get; set; }
    public bool LocksSupported { get; set; } = true;
    public bool OmitMutationTag { get; set; }
    public bool OversizedProof { get; set; }
    public Action? BeforeMutation { get; set; }
    public Action? AfterMutation { get; set; }
    public Action? BeforeGet { get; set; }
    public Uri? DirectoryRedirect { get; set; }
    public int MutationRequests { get; private set; }
    public int Requests { get; private set; }
    public int Puts { get; private set; }
    public Uri Endpoint { get; }

    public SignedWebDavV2HttpFixture(string label, string authorization)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Endpoint = new($"http://localhost:{port}/dav/");
        _authorization = authorization;
        Files["same.txt"] = Encoding.UTF8.GetBytes(label);
        Files["second.txt"] = Encoding.UTF8.GetBytes("second-" + label);
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
        _listener.Start();
        _loop = RunAsync();
    }

    public static string Revision(byte[] bytes) => "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";

    public bool TryExternalAdd(string path, byte[] bytes) => !Locks.Keys.Any(key => path.StartsWith(key + "/", StringComparison.Ordinal)) && Files.TryAdd(path, bytes);

    private async Task RunAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                try { await HandleAsync(context); }
                finally { context.Response.Close(); }
            }
        }
        catch (HttpListenerException) when (_disposed) { }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        Requests++;
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        if (request.Headers["Authorization"] != _authorization) { response.StatusCode = 401; return; }
        string path = Uri.UnescapeDataString(request.Url!.AbsolutePath[Endpoint.AbsolutePath.Length..]).TrimEnd('/');
        if (request.HttpMethod == "GET") { BeforeGet?.Invoke(); BeforeGet = null; }
        if (request.HttpMethod == "PROPFIND")
        {
            string prefix = path.Length == 0 ? "" : path + "/";
            var files = Files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !pair.Key[prefix.Length..].Contains('/'));
            string xml = "<d:multistatus xmlns:d=\"DAV:\">" + (FailedOwnPropstat ?
                "<d:response><d:href>" + request.Url.AbsolutePath + "</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat></d:response>" : "") +
                string.Join("", files.OrderBy(item => item.Key, StringComparer.Ordinal).Select(pair =>
                "<d:response><d:href>" + SecurityElement.Escape(Endpoint.AbsolutePath + string.Join('/', pair.Key.Split('/').Select(Uri.EscapeDataString))) +
                "</d:href><d:propstat><d:prop><d:getetag>" + SecurityElement.Escape(Revision(pair.Value)) +
                "</d:getetag><d:getcontentlength>" + pair.Value.Length +
                "</d:getcontentlength></d:prop><d:status>HTTP/1.1 " + (FailedPropstat ? "403 Forbidden" : "200 OK") + "</d:status></d:propstat></d:response>")) +
                string.Join("", Directories.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key != path && !key[prefix.Length..].Contains('/')).Select(key =>
                    "<d:response><d:href>" + SecurityElement.Escape(Endpoint.AbsolutePath + key + "/") +
                    "</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getetag>\"directory\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>")) +
                "</d:multistatus>";
            byte[] body = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = 207;
            response.ContentType = "application/xml";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            return;
        }
        if (request.HttpMethod == "PUT")
        {
            MutationRequests++;
            BeforeMutation?.Invoke();
            BeforeMutation = null;
            bool exists = Files.TryGetValue(path, out byte[]? previous);
            if ((request.Headers["If-None-Match"] == "*" && exists) || (request.Headers["If-Match"] is { } expected && (!exists || expected != Revision(previous!))))
            { response.StatusCode = 412; return; }
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer);
            byte[] bytes = buffer.ToArray();
            Files[path] = bytes;
            Puts++;
            response.StatusCode = PartialMutation ? 207 : 201;
            if (!OmitMutationTag) response.Headers["ETag"] = Revision(bytes);
            AfterMutation?.Invoke();
            AfterMutation = null;
            if (DropMutationAck) response.Abort();
            return;
        }
        if (request.HttpMethod == "MKCOL")
        {
            MutationRequests++;
            if (Files.ContainsKey(path) || !Directories.TryAdd(path, true)) { response.StatusCode = 405; return; }
            response.StatusCode = PartialMutation ? 207 : 201;
            if (!OmitMutationTag) response.Headers["ETag"] = "\"directory\"";
            AfterMutation?.Invoke();
            AfterMutation = null;
            return;
        }
        bool isDirectory = Directories.ContainsKey(path);
        if (isDirectory && request.HttpMethod == "HEAD" && !request.Url.AbsolutePath.EndsWith('/'))
        { response.StatusCode = 301; response.RedirectLocation = DirectoryRedirect?.AbsoluteUri ?? request.Url.AbsoluteUri + "/"; return; }
        if (!Files.TryGetValue(path, out byte[]? content) && !isDirectory) { response.StatusCode = 404; return; }
        string revision = isDirectory ? "\"directory\"" : Revision(content!);
        if (WeakTags) revision = "W/" + revision;
        response.Headers["ETag"] = revision;
        if (request.HttpMethod == "HEAD") { response.ContentLength64 = content?.Length ?? 0; return; }
        if (request.HttpMethod == "LOCK")
        {
            if (!LocksSupported) { response.StatusCode = 405; return; }
            Assert.AreEqual("infinity", request.Headers["Depth"]);
            if (request.Headers["If-Match"] != revision) { response.StatusCode = 412; return; }
            string lockToken = "opaquelocktoken:" + Guid.NewGuid().ToString("N");
            Locks[path] = lockToken;
            string xml = "<d:prop xmlns:d=\"DAV:\"><d:lockdiscovery><d:activelock><d:lockscope><d:exclusive/></d:lockscope><d:locktype><d:write/></d:locktype><d:depth>infinity</d:depth><d:locktoken><d:href>" + lockToken +
                "</d:href></d:locktoken><d:lockroot><d:href>" + request.Url.AbsoluteUri + "</d:href></d:lockroot><d:timeout>" +
                (InvalidLease ? "Infinite" : "Second-30") + "</d:timeout></d:activelock></d:lockdiscovery></d:prop>";
            byte[] body = Encoding.UTF8.GetBytes(xml);
            response.Headers["Lock-Token"] = "<" + lockToken + ">";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            return;
        }
        if (request.HttpMethod == "UNLOCK") { Locks.TryRemove(path, out _); response.StatusCode = 204; return; }
        if (request.HttpMethod is "MOVE" or "DELETE")
        {
            MutationRequests++;
            BeforeMutation?.Invoke();
            BeforeMutation = null;
            if (!isDirectory && Files.TryGetValue(path, out byte[]? current)) { content = current; revision = Revision(current); }
            if (request.Headers["If-Match"] != revision) { response.StatusCode = 412; return; }
            if (isDirectory && (!Locks.TryGetValue(path, out string? lockToken) || !request.Headers["If"]!.Contains(lockToken, StringComparison.Ordinal)))
            { response.StatusCode = 423; return; }
            if (PartialMutation) { response.StatusCode = 207; return; }
            if (request.HttpMethod == "MOVE")
            {
                Assert.AreEqual("F", request.Headers["Overwrite"]);
                string destination = Uri.UnescapeDataString(new Uri(request.Headers["Destination"]!).AbsolutePath[Endpoint.AbsolutePath.Length..]).TrimEnd('/');
                if (Files.ContainsKey(destination) || Directories.ContainsKey(destination)) { response.StatusCode = 412; return; }
                if (isDirectory) { Directories[destination] = true; Directories.TryRemove(path, out _); }
                else { Files[destination] = content!; Files.TryRemove(path, out _); }
                response.StatusCode = 201;
                if (OmitMutationTag) response.Headers.Remove("ETag");
            }
            else { Files.TryRemove(path, out _); Directories.TryRemove(path, out _); response.StatusCode = 204; }
            Locks.TryRemove(path, out _);
            AfterMutation?.Invoke();
            AfterMutation = null;
            if (DropMutationAck) response.Abort();
            return;
        }
        if (request.HttpMethod == "GET")
        {
            if (request.Headers["If-Match"] != revision) { response.StatusCode = 412; return; }
            if (request.Headers["Range"] is null)
            {
                response.ContentLength64 = OversizedProof ? content!.Length + 1 : content!.Length;
                await response.OutputStream.WriteAsync(content);
                if (OversizedProof) await response.OutputStream.WriteAsync(new byte[] { 1 });
                return;
            }
            string[] range = request.Headers["Range"]!["bytes=".Length..].Split('-');
            int first = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
            int last = int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture);
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {first}-{last}/{content!.Length}";
            response.ContentLength64 = last - first + 1;
            await response.OutputStream.WriteAsync(content.AsMemory(first, last - first + 1));
            return;
        }
        response.StatusCode = 405;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}
