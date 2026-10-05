using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Adapters.LocalDirectory;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

/// <summary>Exercises the Shell-facing callbacks from a process outside the provider.</summary>
[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseExternalConsumerTests
{
    private static readonly string[] ExpectedRootNames = ["Backup", "Documents", "Photos"];

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeOneSyncRootRetainsMultipleInstancesAndDirectoriesAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
        {
            Assert.Inconclusive("Requires the NativeCloudFiles test environment; run the dedicated verification gate.");
        }

        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var first = InstanceId.New();
        var second = InstanceId.New();
        AdapterId adapterId = AdapterId.Parse("example.drive");
        RootRegistration[] registrations = [
            .. AdapterRootRegistrationMapper.MapAll(adapterId, first, [
                new AdapterRootDefinition("docs", "Documents", "Documents", false),
                new AdapterRootDefinition("photos", "Photos", "Photos", false)], RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot),
            .. AdapterRootRegistrationMapper.MapAll(adapterId, second, [
                new AdapterRootDefinition("backup", "Backup", "Backup", false)], RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot),
        ];
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, registrations);
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        var definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]);

        try
        {
            cloud.Register(definition);
            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(state)
                    .WithContentProvider(new MirrorPulseDemandProvider(router, new OfflineRangeTransport()))
                    .Build();
                await fileSystem.StartAsync();
                if (run == 0)
                {
                    await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                    await feed.StartAsync();
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);
                }

                string output = await RunProcessAsync(CreateDirectoryEnumerationProcess(paths.SyncRootPath));
                string[] names = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                CollectionAssert.AreEquivalent(ExpectedRootNames, names);
            }
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeExternalConsumerEnumeratesAndHydratesAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
        {
            Assert.Inconclusive("Requires the NativeCloudFiles test environment; run the dedicated verification gate.");
        }

        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        string sourceFile = Path.Combine(source, "note.txt");
        string cloudFile = Path.Combine(paths.SyncRootPath, "Documents", "note.txt");
        var instance = InstanceId.New();
        var registration = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.local"), instance,
            new AdapterRootDefinition("documents", "Documents", "Documents", false),
            RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var transport = new SourceRangeTransport(source);
        var directoryPages = new SourceDirectoryPages(source, instance);
        var provider = new RecordingDemandProvider(new MirrorPulseDemandProvider(router, transport, directoryPages));
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        var definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]);

        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(sourceFile, "MirrorPulse external hydration probe");
            cloud.Register(definition);

            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(state)
                    .WithContentProvider(provider)
                    .Build();
                await fileSystem.StartAsync();
                if (run == 0)
                {
                    await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                    await feed.StartAsync();
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);
                    string output;
                    try
                    {
                        output = await RunProcessAsync(CreateEnumerationProcess(
                            Path.Combine(paths.SyncRootPath, "Documents")));
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new InvalidDataException($"Callbacks: {string.Join(", ", provider.Requests)}. " +
                            $"Directory requests: {string.Join(", ", directoryPages.Requests)}. " +
                            exception.Message, exception);
                    }
                    StringAssert.Contains(output, cloudFile);
                    CloudItemSnapshot beforeRead = await fileSystem.GetFile("Documents/note.txt").InspectAsync();
                    Assert.IsTrue(beforeRead.IsPlaceholder);
                    Assert.AreEqual(CloudContentAvailability.OnlineOnly, beforeRead.ContentAvailability);
                }

                await RunProcessAsync(CreateComparisonProcess(sourceFile, cloudFile));
                Assert.IsTrue(File.Exists(paths.CfSharpStateDatabasePath));
            }

            Assert.IsGreaterThan(0, transport.ReadCount);
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ProcessStartInfo CreateEnumerationProcess(string directory)
    {
        string path = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
        string command = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path +
            "')); [IO.Directory]::EnumerateFiles($p) | ForEach-Object { Write-Output $_ }";
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        return start;
    }

    private static ProcessStartInfo CreateDirectoryEnumerationProcess(string directory)
    {
        string path = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
        string command = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path +
            "')); [IO.Directory]::EnumerateDirectories($p) | ForEach-Object { Write-Output ([IO.Path]::GetFileName($_)) }";
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        return start;
    }

    private static ProcessStartInfo CreateComparisonProcess(string source, string cloud)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "fc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/b");
        start.ArgumentList.Add("/offline");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(cloud);
        return start;
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo start)
    {
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The external Cloud Files consumer did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"External consumer exited {process.ExitCode}: {stderr}{stdout}");
            }

            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed class SourceDirectoryPages(string source, InstanceId expected) : IMirrorPulseDirectoryPageSource
    {
        public List<string> Requests { get; } = [];

        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
            InstanceId instanceId,
            string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            Requests.Add($"{instanceId}:{normalizedPath}:{continuationCursor.Length}:{pageSize}");
            Assert.AreEqual(expected, instanceId);
            Assert.AreEqual(string.Empty, normalizedPath);
            Assert.IsTrue(continuationCursor.IsEmpty);
            Assert.IsGreaterThan(0, pageSize);
            long length = new FileInfo(Path.Combine(source, "note.txt")).Length;
            return ValueTask.FromResult(new CloudRemoteDirectoryPage([
                new CloudRemoteDirectoryEntry("note.txt", "v1", CloudItemKind.File, "note.txt", length: length)]));
        }
    }

    private sealed class RecordingDemandProvider(MirrorPulseDemandProvider inner) : ICloudDemandProvider
    {
        public List<string> Requests { get; } = [];

        public async ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(
            CloudProviderFetchPlaceholdersRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add($"children:{request.NormalizedPath}:{request.DirectoryIdentity.Length}");
            try
            {
                return await inner.FetchChildrenAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                Requests.Add($"error:{exception.GetType().Name}:{exception.Message}");
                throw;
            }
        }

        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(request, cancellationToken);
    }

    private sealed class SourceRangeTransport(string source) : IMirrorPulseWorkerRangeTransport
    {
        private readonly MirrorPulseLocalDirectoryReader _reader = new(source);
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public async ValueTask<Stream> ReadRangeAsync(
            MirrorPulseWorkerReadRangeRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            byte[] content = await _reader.ReadRangeAsync(
                request.NormalizedPath, request.Offset, checked((int)request.Length), cancellationToken);
            return new MemoryStream(content, writable: false);
        }
    }

    private sealed class OfflineRangeTransport : IMirrorPulseWorkerRangeTransport
    {
        public ValueTask<Stream> ReadRangeAsync(
            MirrorPulseWorkerReadRangeRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new IOException("The Adapter is offline."));
    }
}
