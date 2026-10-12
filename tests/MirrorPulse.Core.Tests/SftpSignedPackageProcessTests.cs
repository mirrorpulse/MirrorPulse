using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SftpSignedPackageProcessTests
{
    [TestMethod]
    public async Task SignedDualArchitecturePackageInstallsAndRunsNativeSftpWorker()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mirrorpulse-sftp-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using SftpProtocolFixture fixture = await SftpProtocolFixture.StartAsync();
            string project = Path.Combine(FindRepositoryRoot(), "Adapters", "official",
                "MirrorPulse.Adapter.Sftp.Worker", "MirrorPulse.Adapter.Sftp.Worker.csproj");
            string x64 = Path.Combine(root, "publish-x64");
            string arm64 = Path.Combine(root, "publish-arm64");
            await PublishAsync(project, "win-x64", x64);
            await PublishAsync(project, "win-arm64", arm64);

            using RSA key = RSA.Create(2048);
            string packagePath = Path.Combine(root, "sftp.mpadapter");
            SignedAdapterPackageBuildResult package = await OfficialProcessAdapterPackageBuilder.BuildAsync(
                new OfficialProcessAdapterPackageInput("com.mirrorpulse.adapter.sftp", "SFTP", "1.0.0",
                    x64, arm64, "MirrorPulse.Adapter.Sftp.Worker.exe"),
                packagePath, key, "MirrorPulse");
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (string runtime in new[] { "win-x64", "win-arm64" })
                {
                    Assert.IsNotNull(archive.GetEntry($"worker/{runtime}/MirrorPulse.Adapter.Worker.exe"));
                    Assert.IsNotNull(archive.GetEntry($"worker/{runtime}/MirrorPulse.Adapter.Sftp.Worker.dll"));
                    Assert.IsNotNull(archive.GetEntry($"worker/{runtime}/Renci.SshNet.dll"));
                    Assert.IsNotNull(archive.GetEntry($"worker/{runtime}/BouncyCastle.Cryptography.dll"));
                }
            }

            string runtimeIdentifier = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64" : "win-x64";
            using RSA wrongKey = RSA.Create(2048);
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => SignedProcessAdapterInstaller.InstallAsync(
                packagePath, package.SignaturePath, Path.Combine(root, "wrong-key"),
                runtimeIdentifier, wrongKey, "MirrorPulse"));
            SignedProcessAdapterInstallation installed = await SignedProcessAdapterInstaller.InstallAsync(
                packagePath, package.SignaturePath, Path.Combine(root, "installed"),
                runtimeIdentifier, key, "MirrorPulse");
            Assert.AreEqual("com.mirrorpulse.adapter.sftp", installed.AdapterId);
            await VerifyInstalledWorkerConnectsAsync(installed, fixture);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyInstalledWorkerConnectsAsync(
        SignedProcessAdapterInstallation installed, SftpProtocolFixture fixture)
    {
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-sftp-package-{Guid.NewGuid():N}";
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        WorkerProcessHandle worker = WorkerProcessLauncher.Start(new WorkerLaunchRequest(
            instance, session, installed.ExecutablePath, installed.InstallationDirectory,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Hello", hello.MessageType);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Ready", hello.RequestId,
                instance, session, true, JsonSerializer.SerializeToElement(new
                {
                    endpoint = $"sftp://127.0.0.1:{fixture.Port}/",
                    username = "user",
                    credentialReference = "sftp-password",
                    trustedHostKeySha256 = fixture.Fingerprint,
                })), timeout.Token);
            ControlFrameEnvelope credential = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("CredentialRequest", credential.MessageType);
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "CredentialResponse", credential.RequestId,
                instance, session, true,
                JsonSerializer.SerializeToElement(new { referenceId = "sftp-password", secret = "correct-secret" })),
                timeout.Token);
            ControlFrameEnvelope connected = await ReadAsync(pipe, timeout.Token);
            Assert.AreEqual("Connected", connected.MessageType, connected.Payload.ToString());
            Assert.AreEqual(fixture.Fingerprint, connected.Payload.GetProperty("hostKeySha256").GetString());
            Guid stopId = Guid.NewGuid();
            await WriteAsync(pipe, new ControlFrameEnvelope(1, "Stop", stopId,
                instance, session, false, JsonSerializer.SerializeToElement(new { })), timeout.Token);
            Assert.AreEqual("Stopped", (await ReadAsync(pipe, timeout.Token)).MessageType);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, worker.Process.ExitCode);
        }
        finally
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            worker.Dispose();
        }
    }

    private static async Task PublishAsync(string project, string runtime, string output)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "publish", project, "--configuration", "Release", "--runtime",
            runtime, "--self-contained", "false", "--no-restore", "--output", output })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet publish did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, process.ExitCode,
            await standardOutput + Environment.NewLine + await standardError);
    }

    private static async Task<ControlFrameEnvelope> ReadAsync(Stream pipe, CancellationToken cancellationToken) =>
        ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(pipe, cancellationToken));

    private static async Task WriteAsync(Stream pipe, ControlFrameEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] payload = ControlFrameJsonCodec.Encode(envelope);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame.AsSpan(4));
        await pipe.WriteAsync(frame, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MirrorPulse.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("The MirrorPulse repository root was not found.");
    }
}
