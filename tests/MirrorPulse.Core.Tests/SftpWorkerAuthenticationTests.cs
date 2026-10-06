using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using MirrorPulse.Adapter.Sftp.Worker;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SftpWorkerAuthenticationTests
{
    [TestMethod]
    public async Task HostKeyRequiresFirstApprovalThenPinAndRejectsChangedKey()
    {
        await using SftpProtocolFixture fixture = await SftpProtocolFixture.StartAsync();
        ControlFrameEnvelope first = await ConnectAsync(fixture, trustedFingerprint: null, approveFirstKey: true);
        Assert.AreEqual("Connected", first.MessageType, first.Payload.ToString());
        Assert.AreEqual(fixture.Fingerprint, first.Payload.GetProperty("hostKeySha256").GetString());

        ControlFrameEnvelope pinned = await ConnectAsync(fixture, fixture.Fingerprint, approveFirstKey: false);
        Assert.AreEqual("Connected", pinned.MessageType);

        string changedFingerprint = new('A', 43);
        ControlFrameEnvelope rejected = await ConnectAsync(fixture, changedFingerprint, approveFirstKey: false);
        Assert.AreEqual("Error", rejected.MessageType);
        Assert.AreEqual("HostKeyRejected", rejected.Payload.GetProperty("code").GetString());
        Assert.IsFalse(rejected.Payload.ToString().Contains("correct-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeclinedFirstHostKeyDoesNotAuthenticate()
    {
        await using SftpProtocolFixture fixture = await SftpProtocolFixture.StartAsync();
        ControlFrameEnvelope rejected = await ConnectAsync(fixture, trustedFingerprint: null, approveFirstKey: false);
        Assert.AreEqual("Error", rejected.MessageType);
        Assert.AreEqual("HostKeyRejected", rejected.Payload.GetProperty("code").GetString());
    }

    private static async Task<ControlFrameEnvelope> ConnectAsync(
        SftpProtocolFixture fixture, string? trustedFingerprint, bool approveFirstKey)
    {
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-sftp-auth-{Guid.NewGuid():N}";
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        string executable = Path.ChangeExtension(typeof(SftpWorkerEntryMarker).Assembly.Location, ".exe");
        var launch = new WorkerLaunchRequest(instance, session, executable,
            Path.GetDirectoryName(executable)!,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName]);
        Assert.IsFalse(string.Join(' ', launch.Arguments).Contains("correct-secret", StringComparison.Ordinal));
        using WorkerProcessHandle worker = WorkerProcessLauncher.Start(launch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = await ReadAsync(pipe, timeout.Token);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Ready", hello.RequestId,
                instance, session, true, JsonSerializer.SerializeToElement(new
                {
                    endpoint = $"sftp://127.0.0.1:{fixture.Port}/",
                    username = "user",
                    credentialReference = "sftp-password",
                    trustedHostKeySha256 = trustedFingerprint,
                })), timeout.Token);
            ControlFrameEnvelope credential = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("CredentialRequest", credential.MessageType);
            Assert.AreEqual("sftp-password", credential.Payload.GetProperty("referenceId").GetString());
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "CredentialResponse",
                credential.RequestId, instance, session, true,
                JsonSerializer.SerializeToElement(new { referenceId = "sftp-password", secret = "correct-secret" })),
                timeout.Token);

            ControlFrameEnvelope result = await ReadAsync(pipe, timeout.Token);
            if (result.MessageType == "HostKeyChallenge")
            {
                Assert.IsNull(trustedFingerprint);
                Assert.AreEqual(fixture.Fingerprint, result.Payload.GetProperty("sha256").GetString());
                await WriteAsync(pipe, new ControlFrameEnvelope(1, "HostKeyDecision", result.RequestId,
                    instance, session, true, JsonSerializer.SerializeToElement(new
                    {
                        sha256 = fixture.Fingerprint,
                        approved = approveFirstKey,
                    })), timeout.Token);
                result = await ReadAsync(pipe, timeout.Token);
            }

            if (result.MessageType == "Connected")
            {
                Guid stopId = Guid.NewGuid();
                await WriteAsync(pipe, new ControlFrameEnvelope(1, "Stop", stopId,
                    instance, session, false, JsonSerializer.SerializeToElement(new { })), timeout.Token);
                Assert.AreEqual("Stopped", (await ReadAsync(pipe, timeout.Token)).MessageType);
            }

            await worker.WaitForExitAsync(timeout.Token);
            Assert.IsFalse(result.Payload.ToString().Contains("correct-secret", StringComparison.Ordinal));
            return result;
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

    private static async Task<ControlFrameEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(stream, cancellationToken));

    private static async Task WriteAsync(Stream stream, ControlFrameEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] payload = ControlFrameJsonCodec.Encode(envelope);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

internal sealed class SftpProtocolFixture : IAsyncDisposable
{
    private readonly Process _process;

    private SftpProtocolFixture(Process process, int port, string fingerprint, string storageDirectory)
    {
        _process = process;
        Port = port;
        Fingerprint = fingerprint;
        StorageDirectory = storageDirectory;
    }

    public int Port { get; }

    public string Fingerprint { get; }

    public string StorageDirectory { get; }

    public static async Task<SftpProtocolFixture> StartAsync(string? identityLabel = null)
    {
        if (identityLabel is not null and not ("left" or "right"))
            throw new ArgumentException("Unknown disposable fixture identity.", nameof(identityLabel));
        string repository = FindRepositoryRoot();
        string python = Path.Combine(repository, "artifacts", "test-tools", "sftp", "venv", "Scripts", "python.exe");
        if (!File.Exists(python))
        {
            throw new InvalidOperationException(
                "The isolated SFTP fixture environment is missing. Run pwsh -File eng/setup-test-environment.ps1 before testing.");
        }

        string script = Path.Combine(repository, "eng", "sftp-fixture", "server.py");
        string storage = Path.Combine(Path.GetTempPath(), $"mirrorpulse-sftp-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(storage);
        if (identityLabel is not null) start.ArgumentList.Add(identityLabel);
        Process process = Process.Start(start) ?? throw new InvalidOperationException("The SFTP fixture did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new InvalidOperationException("The SFTP fixture exited before reporting its port: " +
                    await process.StandardError.ReadToEndAsync(timeout.Token));
            }

            using JsonDocument ready = JsonDocument.Parse(line);
            return new SftpProtocolFixture(process,
                ready.RootElement.GetProperty("port").GetInt32(),
                ready.RootElement.GetProperty("sha256").GetString()!, storage);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            Directory.Delete(storage, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        string errors = await _process.StandardError.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            Console.WriteLine(errors);
        }

        _process.Dispose();
        Directory.Delete(StorageDirectory, recursive: true);
    }

    internal static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MirrorPulse.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("The MirrorPulse repository root was not found.");
    }
}
