using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MirrorPulse.CfSharp.CrashProbe;

/// <summary>A test-only HTTP origin for the two ordinary source files of the GUID transfer fixture.</summary>
/// <remarks>No Cloud Files calls, credentials, arbitrary paths, directories, or unconditional mutations are supported.</remarks>
public sealed class CliTransferWebDavFixture : IAsyncDisposable
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly string[] Names = ["reverse.bin", "forward-moved.bin"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();
    private readonly string _source;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private bool _disposed;
    public Uri Endpoint { get; }
    public int Reads { get; private set; }
    public int Puts { get; private set; }
    public int Deletes { get; private set; }
    public int ConditionalRejections { get; private set; }

    public CliTransferWebDavFixture(string directory)
    {
        string fixture = CliTransferFixtureLayout.RequireFixtureDirectory(directory);
        _source = Path.Combine(fixture, "source", "right");
        foreach (string path in new[] { fixture, Path.Combine(fixture, "source"), _source })
            if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The WebDAV fixture requires its original ordinary private source directory.");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        Endpoint = new($"http://localhost:{port}/dav/");
        _listener.Prefixes.Add(Endpoint.AbsoluteUri); _listener.Start();
        _loop = ServeAsync();
    }

    public static async Task<int> RunAsync(string directory)
    {
        await using var fixture = new CliTransferWebDavFixture(directory);
        Console.WriteLine(JsonSerializer.Serialize(new { endpoint = fixture.Endpoint.AbsoluteUri }));
        await Console.Out.FlushAsync();
        string? stop = await Console.In.ReadLineAsync();
        if (stop != "stop") throw new InvalidDataException("The owned WebDAV fixture requires its explicit stop command.");
        await fixture.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "webdav-server.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            fixture.Reads,
            fixture.Puts,
            fixture.Deletes,
            fixture.ConditionalRejections,
            stopped = true,
            fileBacked = true
        }, JsonOptions));
        return 0;
    }

    private async Task ServeAsync()
    {
        try
        {
            for (int count = 0; count < 4096; count++)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                try { await HandleAsync(context); }
                finally { context.Response.Close(); }
            }
            throw new InvalidDataException("The synthetic HTTP request budget was exhausted.");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpListenerException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request; var response = context.Response;
        string path = request.Url!.AbsolutePath;
        if (request.Url.Query.Length != 0 || !path.StartsWith("/dav/", StringComparison.Ordinal)) { response.StatusCode = 400; return; }
        string name = path[5..];
        if (name.Length != 0 && !Names.Contains(name, StringComparer.Ordinal)) { response.StatusCode = 404; return; }
        if (request.HttpMethod == "OPTIONS") { response.Headers["DAV"] = "1"; response.Headers["Allow"] = "OPTIONS, PROPFIND, HEAD, GET, PUT, DELETE"; return; }
        if (request.HttpMethod == "PROPFIND")
        {
            if (request.Headers["Depth"] is not ("0" or "1")) { response.StatusCode = 403; return; }
            var entries = name.Length == 0 ? Names : [name];
            var xml = new StringBuilder("<d:multistatus xmlns:d=\"DAV:\">");
            if (name.Length == 0) xml.Append("<d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
            if (name.Length != 0 || request.Headers["Depth"] == "1")
                foreach (string entry in entries)
                {
                    byte[]? bytes = await ReadAsync(entry); if (bytes is null) continue;
                    xml.Append("<d:response><d:href>/dav/").Append(entry).Append("</d:href><d:propstat><d:prop><d:resourcetype/><d:getetag>")
                        .Append(SecurityElement.Escape(Revision(bytes))).Append("</d:getetag><d:getcontentlength>")
                        .Append(bytes.Length.ToString(CultureInfo.InvariantCulture))
                        .Append("</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
                }
            byte[] body = Encoding.UTF8.GetBytes(xml.Append("</d:multistatus>").ToString());
            response.StatusCode = 207; response.ContentType = "application/xml"; response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body); return;
        }
        if (name.Length == 0) { response.StatusCode = 405; return; }
        byte[]? content = await ReadAsync(name); string? tag = content is null ? null : Revision(content);
        if (request.HttpMethod == "PUT")
        {
            if (!(request.Headers["If-None-Match"] == "*" && content is null && request.Headers["If-Match"] is null ||
                tag is not null && request.Headers["If-Match"] == tag && request.Headers["If-None-Match"] is null))
            { ConditionalRejections++; response.StatusCode = 412; return; }
            if (request.ContentLength64 is < 0 or > MaximumBytes) { response.StatusCode = 413; return; }
            byte[] bytes = new byte[(int)request.ContentLength64]; await request.InputStream.ReadExactlyAsync(bytes);
            await File.WriteAllBytesAsync(FilePath(name), bytes); Puts++;
            response.StatusCode = content is null ? 201 : 204; response.Headers["ETag"] = Revision(bytes); return;
        }
        if (content is null) { response.StatusCode = 404; return; }
        response.Headers["ETag"] = tag;
        if (request.HttpMethod == "HEAD") { response.ContentLength64 = content.Length; return; }
        if (request.Headers["If-Match"] != tag) { ConditionalRejections++; response.StatusCode = 412; return; }
        if (request.HttpMethod == "DELETE") { File.Delete(FilePath(name)); Deletes++; response.StatusCode = 204; return; }
        if (request.HttpMethod != "GET") { response.StatusCode = 405; return; }
        Reads++; int first = 0, last = content.Length - 1;
        if (request.Headers["Range"] is { } range)
        {
            string[] parts = range.StartsWith("bytes=", StringComparison.Ordinal) ? range[6..].Split('-') : [];
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out first) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || first < 0 || last < first || last >= content.Length)
            { response.StatusCode = 416; return; }
            response.StatusCode = 206; response.Headers["Content-Range"] = FormattableString.Invariant($"bytes {first}-{last}/{content.Length}");
        }
        response.ContentLength64 = last - first + 1;
        await response.OutputStream.WriteAsync(content.AsMemory(first, last - first + 1));
    }

    private string FilePath(string name)
    {
        string path = Path.Combine(_source, name);
        if (!Names.Contains(name, StringComparer.Ordinal) || File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The synthetic HTTP file was redirected.");
        return path;
    }

    private async Task<byte[]?> ReadAsync(string name)
    {
        string path = FilePath(name);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The synthetic HTTP file exceeds its byte budget.");
        return await File.ReadAllBytesAsync(path);
    }

    private static string Revision(byte[] bytes) => '"' + Convert.ToHexString(SHA256.HashData(bytes)) + '"';

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync(); _listener.Close();
        await _loop;
        _shutdown.Dispose();
    }
}
