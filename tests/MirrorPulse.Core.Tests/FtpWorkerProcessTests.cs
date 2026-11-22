using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Ftp.Worker;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class FtpWorkerProcessTests
{
    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task SignedFtpReleaseReadsAndConditionallyUploadsThroughHostAndCfSharp()
    {
        string? aggregateDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        if (string.IsNullOrWhiteSpace(aggregateDirectory))
        {
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");
        }

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(aggregateDirectory, "official-adapters.manifest.json")));
        JsonElement ftp = manifest.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("adapterId").GetString() == "com.mirrorpulse.adapter.ftp");
        string packageDirectory = Path.Combine(aggregateDirectory, "com.mirrorpulse.adapter.ftp");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-signed-ftp", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        await using var fixture = new LoopbackFtpFixture(FtpSecurityMode.Plain);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installation = await catalog.InstallSignedAdapterAsync(
                Path.Combine(packageDirectory, ftp.GetProperty("package").GetString()!),
                Path.Combine(packageDirectory, ftp.GetProperty("signature").GetString()!),
                Path.Combine(root, "installed"), "win-x64");
            AdapterInstance instance = await catalog.CreateInstanceAsync(installation.InstallId,
                "FTP fixture", new Dictionary<string, string>
                {
                    ["endpoint"] = $"ftp://127.0.0.1:{fixture.Port}/",
                    ["username"] = "user",
                    ["securityMode"] = "Plain",
                    ["allowPlaintext"] = "true",
                    ["credentialReference"] = "ftp-password",
                }, ["ftp-password"], Path.Combine(root, "cache", "files"),
                Path.Combine(root, "cache", "transfers"));
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            RootRegistration registration = topology.Roots.Single(binding => binding.InstanceId == instance.InstanceId);
            string rootKey = registration.UniquenessKey;
            string diagnostics = Path.Combine(root, "diagnostics");
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                new FixedCredentialStore("ftp-password", "correct-secret"), diagnosticsDirectory: diagnostics);
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
                    Assert.Fail($"The signed FTP Worker failed: {state.LastErrorCode}. " +
                        await WorkerFailureTestDiagnostics.ReadAsync(diagnostics));
                }

                await Task.Delay(50, timeout.Token);
            }

            string revision = (await supervisor.StatAsync(new MirrorPulseWorkerStatRequest(
                instance.InstanceId, "report.bin", rootKey), timeout.Token))!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(revision));
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor,
                new MirrorPulseAdapterDirectoryPageSource(supervisor));
            string filePath = Path.Combine(paths.SyncRootPath, topology.Roots.Single().DirectoryName,
                "report.bin");
            byte[] identity = MirrorPulsePlaceholderIdentity.CreateForRoot(registration,
                "report.bin", revision).Encode();
            await using Stream read = await provider.OpenReadAsync(filePath, identity,
                8, 2, 3, timeout.Token);
            read.Seek(2, SeekOrigin.Begin);
            byte[] range = new byte[3];
            await read.ReadExactlyAsync(range, timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 5, 7, 11 }, range);

            byte[] replacement = [1, 4, 9, 16];
            string updated;
            await using (var content = new MemoryStream(replacement, writable: false))
            {
                updated = await supervisor.UploadAsync(new MirrorPulseWorkerUploadRequest(
                    instance.InstanceId, "report.bin", revision, content, replacement.Length, RootKey: rootKey), timeout.Token);
                Assert.IsFalse(string.IsNullOrWhiteSpace(updated));
            }

            CollectionAssert.AreEqual(replacement, fixture.ReadStoredFile("/report.bin"));
            await using (var stale = new MemoryStream([8, 8, 8], writable: false))
            {
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () => await supervisor.UploadAsync(
                    new MirrorPulseWorkerUploadRequest(instance.InstanceId, "report.bin", revision,
                        stale, 3, RootKey: rootKey), timeout.Token));
            }

            CollectionAssert.AreEqual(replacement, fixture.ReadStoredFile("/report.bin"));

            string movedRevision = await supervisor.MoveAsync(new MirrorPulseWorkerMoveRequest(
                instance.InstanceId, "report.bin", "renamed.bin", updated, false, RootKey: rootKey), timeout.Token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(movedRevision));
            CollectionAssert.AreEqual(replacement, fixture.ReadStoredFile("/renamed.bin"));
            await supervisor.DeleteAsync(new MirrorPulseWorkerDeleteRequest(
                instance.InstanceId, "renamed.bin", movedRevision, false, RootKey: rootKey), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new MirrorPulseWorkerStatRequest(
                instance.InstanceId, "renamed.bin", rootKey), timeout.Token));
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
    public async Task IndependentWorkerAuthenticatesPlainExplicitAndImplicitFtpOverCurrentUserPipe()
    {
        foreach (FtpSecurityMode mode in Enum.GetValues<FtpSecurityMode>())
        {
            await VerifyModeAsync(mode);
        }
    }

    [TestMethod]
    public async Task IndependentWorkerRejectsInvalidHostConfigurationBeforeRequestingCredential()
    {
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-ftp-{Guid.NewGuid():N}";
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        string executable = Path.ChangeExtension(typeof(FtpWorkerEntryMarker).Assembly.Location, ".exe");
        var request = new WorkerLaunchRequest(instance, session, executable,
            Path.GetDirectoryName(executable)!,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName]);
        using WorkerProcessHandle worker = WorkerProcessLauncher.Start(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = await ReadAsync(pipe, timeout.Token);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Ready", hello.RequestId,
                instance, session, true, JsonSerializer.SerializeToElement(new
                {
                    endpoint = "https://user:secret@example.test/",
                    username = "user",
                    credentialReference = "ftp-password",
                    securityMode = "ExplicitTls",
                })), timeout.Token);

            ControlFrameEnvelope error = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Error", error.MessageType);
            Assert.AreEqual("InvalidConfiguration", error.Payload.GetProperty("code").GetString());
            Assert.IsFalse(error.Payload.ToString().Contains("secret", StringComparison.Ordinal));
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(1, worker.Process.ExitCode);
        }
        finally
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
        }
    }

    [TestMethod]
    public async Task IndependentWorkerReadsRestartedRangeAndStagesConditionalUploadWithRetry()
    {
        await using var fixture = new LoopbackFtpFixture(FtpSecurityMode.Plain);
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-ftp-{Guid.NewGuid():N}";
        string cache = Path.Combine(Path.GetTempPath(), "MirrorPulse-ftp-tests", Guid.NewGuid().ToString("N"));
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        string executable = Path.ChangeExtension(typeof(FtpWorkerEntryMarker).Assembly.Location, ".exe");
        var request = new WorkerLaunchRequest(instance, session, executable,
            Path.GetDirectoryName(executable)!,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName],
            new Dictionary<string, string> { ["MP_TRANSFER_CACHE_DIR"] = cache });
        using WorkerProcessHandle worker = WorkerProcessLauncher.Start(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = await ReadAsync(pipe, timeout.Token);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Ready", hello.RequestId,
                instance, session, true, JsonSerializer.SerializeToElement(new
                {
                    endpoint = $"ftp://127.0.0.1:{fixture.Port}/",
                    username = "user",
                    credentialReference = "ftp-password",
                    securityMode = "Plain",
                })), timeout.Token);
            ControlFrameEnvelope credential = await ReadAsync(pipe, timeout.Token);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "CredentialResponse",
                credential.RequestId, instance, session, true,
                JsonSerializer.SerializeToElement(new { referenceId = "ftp-password", secret = "correct-secret" })),
                timeout.Token);
            Assert.AreEqual("Connected", (await ReadAsync(pipe, timeout.Token)).MessageType);

            Guid readId = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "ReadRange", readId,
                instance, session, false,
                JsonSerializer.SerializeToElement(new { path = "report.bin", offset = 2, length = 3 })),
                timeout.Token);
            ControlFrameEnvelope range = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("ReadRangeReady", range.MessageType,
                range.MessageType == "OperationError" ? range.Payload.ToString() : string.Empty);
            BinaryChunkFrame chunk = BinaryChunkCodec.Decode(
                await LengthPrefixedFrameReader.ReadAsync(pipe, timeout.Token));
            CollectionAssert.AreEqual(new byte[] { 5, 7, 11 }, chunk.Data.ToArray());
            Assert.AreEqual(2, chunk.Offset);
            Assert.IsTrue(chunk.EndOfStream);

            Guid statId = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Stat", statId, instance, session,
                false, JsonSerializer.SerializeToElement(new { path = "report.bin" })), timeout.Token);
            ControlFrameEnvelope stat = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("StatResult", stat.MessageType);
            string originalRevision = stat.Payload.GetProperty("revision").GetString()!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(originalRevision));

            byte[] replacement = [1, 4, 9, 16];
            ControlFrameEnvelope complete = await UploadAsync(
                pipe, instance, session, Guid.NewGuid(), "report.bin", originalRevision,
                replacement, timeout.Token);
            Assert.AreEqual("UploadComplete", complete.MessageType,
                complete.MessageType == "OperationError" ? complete.Payload.ToString() : string.Empty);
            CollectionAssert.AreEqual(replacement, fixture.ReadStoredFile("/report.bin"));

            ControlFrameEnvelope conflict = await UploadAsync(
                pipe, instance, session, Guid.NewGuid(), "report.bin", originalRevision,
                [8, 8, 8], timeout.Token);
            Assert.AreEqual("OperationError", conflict.MessageType);
            Assert.AreEqual("RemoteConflict", conflict.Payload.GetProperty("code").GetString());
            CollectionAssert.AreEqual(replacement, fixture.ReadStoredFile("/report.bin"));

            fixture.FailNextStore = true;
            Guid retryId = Guid.NewGuid();
            ControlFrameEnvelope failed = await UploadAsync(pipe, instance, session, retryId,
                "new.bin", null, [6, 2, 6], timeout.Token);
            Assert.AreEqual("OperationError", failed.MessageType);
            Assert.AreEqual("RetryableTransferFailure", failed.Payload.GetProperty("code").GetString());
            ControlFrameEnvelope retried = await UploadAsync(pipe, instance, session, retryId,
                "new.bin", null, [6, 2, 6], timeout.Token);
            Assert.AreEqual("UploadComplete", retried.MessageType,
                retried.MessageType == "OperationError" ? retried.Payload.ToString() : string.Empty);
            CollectionAssert.AreEqual(new byte[] { 6, 2, 6 }, fixture.ReadStoredFile("/new.bin"));

            await WriteAsync(pipe, new ControlFrameEnvelope(1, "ReadRange", Guid.NewGuid(),
                instance, session, false,
                JsonSerializer.SerializeToElement(new { path = "../outside", offset = 0, length = 1 })),
                timeout.Token);
            ControlFrameEnvelope unsafePath = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("OperationError", unsafePath.MessageType);
            Assert.AreEqual("InvalidRequest", unsafePath.Payload.GetProperty("code").GetString());

            Guid corruptId = Guid.NewGuid();
            Guid corruptStream = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Upload", corruptId, instance,
                session, false, JsonSerializer.SerializeToElement(new
                {
                    path = "bad.bin",
                    expectedRevision = (string?)null,
                    length = 2,
                    streamId = corruptStream,
                })), timeout.Token);
            Assert.AreEqual("UploadReady", (await ReadAsync(pipe, timeout.Token)).MessageType);
            byte[] corrupt = BinaryChunkCodec.Encode(new BinaryChunkFrame(
                corruptId, instance, session, corruptStream, 0, new byte[] { 1, 2 }, true,
                Sha256Digest.Compute([1, 2])));
            corrupt[^1] ^= 0x10;
            await WritePayloadAsync(pipe, corrupt, timeout.Token);
            ControlFrameEnvelope rejectedChunk = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("OperationError", rejectedChunk.MessageType);
            Assert.AreEqual("InvalidRequest", rejectedChunk.Payload.GetProperty("code").GetString());
            Assert.IsNull(fixture.ReadStoredFile("/bad.bin"));

            Guid stopId = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Stop", stopId, instance,
                session, false, JsonSerializer.SerializeToElement(new { })), timeout.Token);
            Assert.AreEqual("Stopped", (await ReadAsync(pipe, timeout.Token)).MessageType);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, worker.Process.ExitCode);
            Assert.IsEmpty(Directory.EnumerateFiles(cache));
        }
        finally
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }

            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    private static async Task<ControlFrameEnvelope> UploadAsync(
        Stream pipe,
        InstanceId instance,
        WorkerSessionId session,
        Guid requestId,
        string path,
        string? expectedRevision,
        byte[] content,
        CancellationToken cancellationToken)
    {
        Guid streamId = Guid.NewGuid();
        await WriteAsync(pipe, new ControlFrameEnvelope(1, "Upload", requestId, instance,
            session, false, JsonSerializer.SerializeToElement(new
            {
                path,
                expectedRevision,
                length = content.Length,
                streamId,
            })), cancellationToken);
        ControlFrameEnvelope ready = await ReadAsync(pipe, cancellationToken);
        Assert.AreEqual("UploadReady", ready.MessageType,
            ready.MessageType == "OperationError" ? ready.Payload.ToString() : string.Empty);
        byte[] encoded = BinaryChunkCodec.Encode(new BinaryChunkFrame(
            requestId, instance, session, streamId, 0, content, true,
            Sha256Digest.Compute(content)));
        await WritePayloadAsync(pipe, encoded, cancellationToken);
        while (true)
        {
            ControlFrameEnvelope response = await ReadAsync(pipe, cancellationToken);
            if (response.MessageType != "TransferProgress")
            {
                return response;
            }
        }
    }

    private static async Task VerifyModeAsync(FtpSecurityMode mode)
    {
        await using var fixture = new LoopbackFtpFixture(mode);
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-ftp-{Guid.NewGuid():N}";
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        string executable = Path.ChangeExtension(typeof(FtpWorkerEntryMarker).Assembly.Location, ".exe");
        Assert.IsTrue(File.Exists(executable), "The independently built FTP Worker executable is missing.");
        var request = new WorkerLaunchRequest(instance, session, executable,
            Path.GetDirectoryName(executable)!,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName]);
        using WorkerProcessHandle worker = WorkerProcessLauncher.Start(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Hello", hello.MessageType);
            Assert.AreEqual(instance, hello.InstanceId);
            Assert.AreEqual(session, hello.WorkerSessionId);
            Assert.AreEqual("mirrorpulse.ftp", hello.Payload.GetProperty("adapterId").GetString());

            var ready = new ControlFrameEnvelope(1, "Ready", hello.RequestId, instance, session, true,
                JsonSerializer.SerializeToElement(new
                {
                    endpoint = $"ftp://127.0.0.1:{fixture.Port}/",
                    username = "user",
                    credentialReference = "ftp-password",
                    securityMode = mode.ToString(),
                    trustedCertificateSha256 = mode == FtpSecurityMode.Plain
                        ? null : fixture.CertificateSha256,
                }));
            await WriteAsync(pipe, ready, timeout.Token);

            ControlFrameEnvelope credentialRequest = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("CredentialRequest", credentialRequest.MessageType);
            Assert.AreEqual("ftp-password", credentialRequest.Payload.GetProperty("referenceId").GetString());
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "CredentialResponse",
                credentialRequest.RequestId, instance, session, true,
                JsonSerializer.SerializeToElement(new { referenceId = "ftp-password", secret = "correct-secret" })),
                timeout.Token);

            ControlFrameEnvelope connected = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Connected", connected.MessageType,
                connected.MessageType == "Error" ? connected.Payload.ToString() : string.Empty);
            Assert.AreEqual(mode != FtpSecurityMode.Plain,
                connected.Payload.GetProperty("encrypted").GetBoolean());

            Guid stopId = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Stop", stopId, instance, session, false,
                JsonSerializer.SerializeToElement(new { })), timeout.Token);
            ControlFrameEnvelope stopped = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Stopped", stopped.MessageType);
            Assert.AreEqual(stopId, stopped.RequestId);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, worker.Process.ExitCode);
            Assert.IsTrue(fixture.Authenticated);
            Assert.AreEqual(mode != FtpSecurityMode.Plain, fixture.ControlChannelEncrypted);
            Assert.IsFalse(request.Arguments.Any(argument => argument.Contains("correct-secret", StringComparison.Ordinal)));
        }
        finally
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
        }
    }

    private static async ValueTask<ControlFrameEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(stream, cancellationToken));

    private static async ValueTask WriteAsync(
        Stream stream,
        ControlFrameEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await WritePayloadAsync(stream, ControlFrameJsonCodec.Encode(envelope), cancellationToken);
    }

    private static async ValueTask WritePayloadAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal sealed class LoopbackFtpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly Task _server;
        private readonly FtpSecurityMode _mode;
        private readonly string _username;
        private readonly string _password;
        private readonly Dictionary<string, (byte[] Content, DateTime Modified)> _files =
            new(StringComparer.Ordinal);
        private TcpListener? _dataListener;
        private long _restartOffset;
        private string? _renameFrom;
        private bool _protectData;
        private int _uploadCount;
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/" };
        private string _currentDirectory = "/";

        public LoopbackFtpFixture(FtpSecurityMode mode, string username = "user",
            string password = "correct-secret", byte[]? initialContent = null)
        {
            _mode = mode;
            _username = username;
            _password = password;
            using RSA key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 generated = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _certificate = X509CertificateLoader.LoadPkcs12(
                generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            CertificateSha256 = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _files["/report.bin"] = (initialContent ?? [2, 3, 5, 7, 11, 13, 17, 19],
                new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));
            _server = ServeAsync();
        }

        public int Port { get; }

        public string CertificateSha256 { get; }

        public bool Authenticated { get; private set; }

        public bool ControlChannelEncrypted { get; private set; }

        public bool FailNextStore { get; set; }

        public byte[]? ReadStoredFile(string path) =>
            _files.TryGetValue(path, out var file) ? file.Content.ToArray() : null;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _dataListener?.Stop();
            try
            {
                await _server.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or TimeoutException)
            {
            }

            _certificate.Dispose();
        }

        private async Task ServeAsync()
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            Stream stream = client.GetStream();
            if (_mode == FtpSecurityMode.ImplicitTls)
            {
                stream = await SecureAsync(stream);
            }

            await SendAsync(stream, "220 MirrorPulse test FTP ready\r\n");
            while (true)
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? line = await reader.ReadLineAsync();
                if (line is null)
                {
                    return;
                }

                string command = line.Split(' ', 2)[0].ToUpperInvariant();
                string argument = line.Length > command.Length ? line[(command.Length + 1)..] : string.Empty;
                switch (command)
                {
                    case "AUTH" when _mode == FtpSecurityMode.ExplicitTls && argument == "TLS":
                        await SendAsync(stream, "234 Proceed with TLS\r\n");
                        stream = await SecureAsync(stream);
                        break;
                    case "USER":
                        await SendAsync(stream, argument == _username ? "331 Password required\r\n" : "530 Invalid user\r\n");
                        break;
                    case "PASS":
                        Authenticated = argument == _password;
                        await SendAsync(stream, Authenticated ? "230 Logged in\r\n" : "530 Login incorrect\r\n");
                        break;
                    case "FEAT":
                        await SendAsync(stream, "211-Features\r\n UTF8\r\n SIZE\r\n MDTM\r\n MLST type*;size*;modify*;\r\n REST STREAM\r\n211 End\r\n");
                        break;
                    case "MLSD":
                        if (!_directories.Contains(argument)) { await SendAsync(stream, "550 Not found\r\n"); break; }
                        await SendAsync(stream, "150 Opening directory connection\r\n");
                        using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                        {
                            Stream dataStream = _protectData ? await SecureAsync(data.GetStream()) : data.GetStream();
                            string prefix = argument.TrimEnd('/') + "/";
                            foreach (var file in _files.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal) && !item.Key[prefix.Length..].Contains('/'))
                                .OrderBy(item => item.Key, StringComparer.Ordinal))
                                await SendAsync(dataStream, FormattableString.Invariant(
                                    $"type=file;size={file.Value.Content.Length};modify={file.Value.Modified:yyyyMMddHHmmss}; {file.Key[prefix.Length..]}\r\n"));
                            foreach (string directory in _directories.Where(path => path != "/" && path.StartsWith(prefix, StringComparison.Ordinal) && !path[prefix.Length..].Contains('/')))
                                await SendAsync(dataStream, $"type=dir;size=0;modify=20260929120000; {directory[prefix.Length..]}\r\n");
                        }
                        _dataListener?.Stop();
                        await SendAsync(stream, "226 Directory complete\r\n");
                        break;
                    case "PROT":
                        _protectData = argument == "P";
                        await SendAsync(stream, "200 Data protection set\r\n");
                        break;
                    case "SIZE":
                        await SendAsync(stream, _files.TryGetValue(argument, out var sized)
                            ? $"213 {sized.Content.Length}\r\n" : "550 Not found\r\n");
                        break;
                    case "MDTM":
                        await SendAsync(stream, _files.TryGetValue(argument, out var dated)
                            ? $"213 {dated.Modified:yyyyMMddHHmmss}\r\n" : "550 Not found\r\n");
                        break;
                    case "EPSV":
                    case "PASV":
                        _dataListener?.Stop();
                        _dataListener = new TcpListener(IPAddress.Loopback, 0);
                        _dataListener.Start();
                        int port = ((IPEndPoint)_dataListener.LocalEndpoint).Port;
                        await SendAsync(stream, command == "EPSV"
                            ? $"229 Entering Extended Passive Mode (|||{port}|)\r\n"
                            : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})\r\n");
                        break;
                    case "REST":
                        _restartOffset = long.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                        await SendAsync(stream, "350 Restart position accepted\r\n");
                        break;
                    case "RETR":
                        if (!_files.TryGetValue(argument, out var retrieved))
                        {
                            await SendAsync(stream, "550 Not found\r\n");
                            break;
                        }

                        await SendAsync(stream, "150 Opening data connection\r\n");
                        using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                        {
                            Stream dataStream = _protectData ? await SecureAsync(data.GetStream()) : data.GetStream();
                            await dataStream.WriteAsync(retrieved.Content.AsMemory(checked((int)_restartOffset)));
                            await dataStream.FlushAsync();
                        }

                        _restartOffset = 0;
                        _dataListener?.Stop();
                        await SendAsync(stream, "226 Transfer complete\r\n");
                        break;
                    case "STOR":
                        string parent = argument[..argument.LastIndexOf('/')];
                        if (!_directories.Contains(parent.Length == 0 ? "/" : parent)) { await SendAsync(stream, "550 Parent missing\r\n"); break; }
                        await SendAsync(stream, "150 Opening data connection\r\n");
                        using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                        {
                            Stream dataStream = _protectData ? await SecureAsync(data.GetStream()) : data.GetStream();
                            using var output = new MemoryStream();
                            await dataStream.CopyToAsync(output);
                            if (!FailNextStore)
                            {
                                _uploadCount++;
                                _files[argument] = (output.ToArray(),
                                    new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc).AddSeconds(_uploadCount));
                            }
                        }

                        _dataListener?.Stop();
                        if (FailNextStore)
                        {
                            FailNextStore = false;
                            await SendAsync(stream, "451 Temporary transfer failure\r\n");
                        }
                        else
                        {
                            await SendAsync(stream, "226 Transfer complete\r\n");
                        }

                        break;
                    case "RNFR":
                        _renameFrom = _files.ContainsKey(argument) ? argument : null;
                        await SendAsync(stream, _renameFrom is null ? "550 Not found\r\n" : "350 Ready for destination\r\n");
                        break;
                    case "RNTO":
                        if (_renameFrom is null)
                        {
                            await SendAsync(stream, "503 Rename source missing\r\n");
                            break;
                        }

                        _files[argument] = _files[_renameFrom];
                        _files.Remove(_renameFrom);
                        _renameFrom = null;
                        await SendAsync(stream, "250 Rename complete\r\n");
                        break;
                    case "DELE":
                        await SendAsync(stream, _files.Remove(argument) ? "250 Deleted\r\n" : "550 Not found\r\n");
                        break;
                    case "SYST":
                        await SendAsync(stream, "215 UNIX Type: L8\r\n");
                        break;
                    case "PWD":
                        await SendAsync(stream, $"257 \"{_currentDirectory}\" is current directory\r\n");
                        break;
                    case "CWD":
                        if (_directories.Contains(argument)) { _currentDirectory = argument; await SendAsync(stream, "250 Directory changed\r\n"); }
                        else await SendAsync(stream, "550 Not found\r\n");
                        break;
                    case "MKD":
                        string directoryParent = argument[..argument.LastIndexOf('/')];
                        bool created = !_files.ContainsKey(argument) && _directories.Contains(directoryParent.Length == 0 ? "/" : directoryParent) && _directories.Add(argument);
                        await SendAsync(stream, created ? "257 Directory created\r\n" : "550 Cannot create directory\r\n");
                        break;
                    case "RMD":
                        bool removed = argument != "/" && !_files.Keys.Any(path => path.StartsWith(argument + "/", StringComparison.Ordinal)) &&
                            !_directories.Any(path => path.StartsWith(argument + "/", StringComparison.Ordinal)) && _directories.Remove(argument);
                        await SendAsync(stream, removed ? "250 Directory removed\r\n" : "550 Directory not empty or absent\r\n");
                        break;
                    case "QUIT":
                        await SendAsync(stream, "221 Goodbye\r\n");
                        return;
                    default:
                        await SendAsync(stream, "200 Command okay\r\n");
                        break;
                }
            }
        }

        private async Task<Stream> SecureAsync(Stream input)
        {
            var tls = new SslStream(input, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: false);
            ControlChannelEncrypted = true;
            return tls;
        }

        private static Task SendAsync(Stream stream, string response) =>
            stream.WriteAsync(Encoding.ASCII.GetBytes(response)).AsTask();
    }
}
