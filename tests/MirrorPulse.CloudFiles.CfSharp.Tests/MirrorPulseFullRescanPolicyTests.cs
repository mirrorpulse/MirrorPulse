using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using CfSharp;
using CfSharp.Native;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseFullRescanPolicyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeOverflowReconcilesFilesAndKeepsDisabledRootsOffline()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId active = InstanceId.New();
        InstanceId offline = InstanceId.New();
        RootRegistration Registration(InstanceId instance, string name, RootRegistrationState state) =>
            AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rescan"), instance,
                new AdapterRootDefinition(name, name, name, false), state);
        RootRegistration first = Registration(active, "Docs", RootRegistrationState.Active);
        RootRegistration second = Registration(offline, "Offline", RootRegistrationState.Disabled);
        var transport = new DiskWorker(Path.Combine(root, "remote"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
            Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Offline"));
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "note.txt"), "active local data", timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Offline", "offline.txt"), "offline local data", timeout.Token);
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await fileSystem.StartAsync(timeout.Token);
            await ReportCoordinationUsnsAsync(fileSystem, paths.SyncRootPath, timeout.Token);
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed(new() { BufferCapacity = 1 });
            await feed.StartAsync(timeout.Token);
            // Backpressure the official store while real native notifications fill the public feed buffer.
            await using (ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token))
            {
                for (int index = 0; index < 128; index++)
                {
                    string path = Path.Combine(paths.SyncRootPath, "Docs", $"churn-{index}.txt");
                    File.WriteAllText(path, "overflow");
                    File.Delete(path);
                }
                await Task.Delay(500, timeout.Token);
                await transaction.RollbackAsync(timeout.Token);
            }
            CloudLocalChangeBatch signal;
            do
            {
                signal = await feed.ReadBatchAsync(timeout.Token);
                if (!signal.RequiresFullRescan) await feed.AcknowledgeAsync(signal.Changes.Select(change => change.OperationId), timeout.Token);
            } while (!signal.RequiresFullRescan);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            async Task RetryRetainedConfirmationAsync(Func<Task> operation)
            {
                for (int attempt = 1; ; attempt++)
                {
                    try { await operation(); return; }
                    catch (MirrorPulseMutationAmbiguousException) when (attempt < 4)
                    {
                        MirrorPulseMutationRecord[] retained = (await catalog.ReadIncompleteMutationsAsync(timeout.Token))
                            .Where(record => record.State == MirrorPulseMutationState.RemoteAccepted).ToArray();
                        if (retained.Length == 0) throw;
                        foreach (MirrorPulseMutationRecord record in retained)
                        {
                            Assert.IsNotNull(await catalog.ReadContentAcceptanceProofAsync(record.Intent.OperationId, timeout.Token));
                            MirrorPulseContentConfirmationReceipt? receipt = await catalog.ReadContentConfirmationReceiptAsync(record.Intent.OperationId, timeout.Token);
                            if (receipt is null || receipt.Outcome is not (MirrorPulseContentConfirmationOutcome.Busy or
                                MirrorPulseContentConfirmationOutcome.ProtectionLost or MirrorPulseContentConfirmationOutcome.NativeAppliedProjectionPending)) throw;
                            Assert.IsFalse(receipt.MayAcknowledge);
                            TestContext.WriteLine($"Rescan confirmation recovery: attempt={attempt}, outcome={receipt.Outcome}, nativeApplied={receipt.NativeApplied}, projected={receipt.DurableProjectionCommitted}.");
                        }
                        await Task.Delay(50, timeout.Token);
                    }
                }
            }
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [first, second]);
            var policy = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog, transport, transport,
                instance => instance == active, transport, transport, transport);
            try { await RetryRetainedConfirmationAsync(() => new MirrorPulseCfSharpFullRescanAdapter(feed, policy.ReconcileAsync).HandleAsync(signal, timeout.Token).AsTask()); }
            catch (CloudFilesException exception)
            {
                Assert.Fail($"CfSharp 0.1.0-preview.3: {exception.Operation}, HRESULT 0x{exception.HResult:X8}, Win32 {exception.Win32ErrorCode}; {exception}");
            }
            CollectionAssert.AreEqual(new[] { second.RootId }, (await catalog.ReadDeferredRescanRootsAsync(timeout.Token)).ToArray());
            Assert.AreEqual(1, transport.Uploads.GetValueOrDefault(active));
            Assert.AreEqual(0, transport.Uploads.GetValueOrDefault(offline));
            Assert.AreEqual("active local data", await File.ReadAllTextAsync(transport.PathFor(active, "note.txt"), timeout.Token));
            Assert.AreEqual(CloudSynchronizationState.InSync,
                (await fileSystem.GetFile("Docs/note.txt").InspectAsync(timeout.Token)).SynchronizationState);
            // A missing known file must not be deleted remotely if a different subtree is unreadable.
            File.Delete(Path.Combine(paths.SyncRootPath, "Docs", "note.txt"));
            string deniedPath = Path.Combine(paths.SyncRootPath, "Docs", "denied");
            DirectoryInfo denied = Directory.CreateDirectory(deniedPath);
            DirectorySecurity originalAcl = denied.GetAccessControl();
            var restricted = denied.GetAccessControl();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            restricted.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            denied.SetAccessControl(restricted);
            try
            {
                var interrupted = new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync,
                    feed.AcknowledgeFullRescanAsync, _ => ValueTask.CompletedTask);
                bool permissionFailed = false;
                try { await interrupted.RunAsync(new(true), timeout.Token); }
                catch (UnauthorizedAccessException) { permissionFailed = true; }
                catch (CloudFilesException exception) when (exception.Win32ErrorCode == 5) { permissionFailed = true; }
                Assert.IsTrue(permissionFailed, "The native fixture must exercise an actual denied directory.");
                Assert.AreEqual(0, transport.Deletes);
                Assert.IsTrue(File.Exists(transport.PathFor(active, "note.txt")));
                Assert.AreEqual(MirrorPulseFullRescanPhase.Running, (await catalog.ReadFullRescanAsync(timeout.Token))!.Phase);
            }
            finally { denied.SetAccessControl(originalAcl); Directory.Delete(deniedPath); }
            RootRegistration enabledSecond = Registration(offline, "Offline", RootRegistrationState.Active);
            policy = new(fileSystem, feed, state, new(paths.SyncRootPath, [first, enabledSecond]), catalog, transport, transport,
                _ => true, transport, transport, transport);
            var ackGap = new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync, feed.AcknowledgeFullRescanAsync,
                _ => throw new IOException("Projection failed after native rescan acknowledgement."));
            await Assert.ThrowsExactlyAsync<IOException>(() => RetryRetainedConfirmationAsync(() => ackGap.RunAsync(new(false), timeout.Token).AsTask()));
            Assert.AreEqual(MirrorPulseFullRescanPhase.Acknowledging, (await catalog.ReadFullRescanAsync(timeout.Token))!.Phase);
            Assert.AreEqual(1, transport.Uploads[active]);
            Assert.AreEqual(1, transport.Uploads[offline]);
            Assert.AreEqual("offline local data", await File.ReadAllTextAsync(transport.PathFor(offline, "offline.txt"), timeout.Token));
            Assert.AreEqual(1, transport.Deletes);
            await feed.DisposeAsync();
            await fileSystem.DisposeAsync();
            await catalog.DisposeAsync();
            var restartedState = new MirrorPulseCfSharpStateSession(paths);
            await using var restarted = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(restartedState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await restarted.StartAsync(timeout.Token);
            await using CloudLocalChangeFeed restartedFeed = restarted.CreateLocalChangeFeed();
            await restartedFeed.StartAsync(timeout.Token);
            await using var reopenedCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            policy = new(restarted, restartedFeed, restartedState, new(paths.SyncRootPath, [first, enabledSecond]), reopenedCatalog,
                transport, transport, _ => true, transport, transport, transport);
            var recovered = new MirrorPulseFullRescanRecovery(reopenedCatalog, policy.ReconcileAsync, restartedFeed.AcknowledgeFullRescanAsync,
                async token => await reopenedCatalog.CompleteRootRescanAsync(second.RootId, token));
            Assert.IsTrue((await recovered.RunAsync(new(false), timeout.Token)).WasRequired);
            Assert.IsNull(await reopenedCatalog.ReadFullRescanAsync(timeout.Token));
            Assert.IsEmpty(await reopenedCatalog.ReadDeferredRescanRootsAsync(timeout.Token));
            Assert.AreEqual(1, transport.Uploads[active]);
            Assert.AreEqual(1, transport.Uploads[offline]);
            Assert.AreEqual(1, transport.Deletes);
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "after.txt"), "after rescan", timeout.Token);
            Assert.IsFalse((await restartedFeed.ReadBatchAsync(timeout.Token)).RequiresFullRescan);
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            Directory.Delete(root, true);
        }
    }

    private async Task ReportCoordinationUsnsAsync(CloudFileSystem fileSystem, string syncRoot, CancellationToken token)
    {
        // Isolate the library calls from all product scanning and Worker code. This unique
        // disposable fixture is the only place where an unconditional in-sync call is made.
        const string name = "coordination-probe.txt";
        await File.WriteAllTextAsync(Path.Combine(syncRoot, name), "coordination probe", token);
        CloudFile file = fileSystem.GetFile(name);
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "probe", "probe-revision");
        CloudPlaceholderMutationResult converted = await file.ConvertToPlaceholderAsync(identity, cancellationToken: token);
        CloudStateChangeResult cleared = await file.SetInSyncAsync(false, cancellationToken: token);
        CloudStateChangeResult marked = await file.SetInSyncAsync(true, cancellationToken: token);
        CloudStateChangeResult changed = await file.SetInSyncAsync(false, cancellationToken: token);
        CloudPlaceholderMutationResult patched = await file.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder()
            .WithMetadata(CloudPlaceholderMetadata.CreateFileBuilder().WithLastWriteTime(DateTimeOffset.UtcNow.AddMinutes(-1)).Build())
            .WithInSyncState(false).Build(), token);
        TestContext.WriteLine($"CfSharp 0.1.0-preview.3 coordination USNs: convert={converted.OperationUsn}, clear={cleared.OperationUsn}, mark={marked.OperationUsn}, changed={changed.OperationUsn}, metadata={patched.OperationUsn}; OS={Environment.OSVersion.Version}.");
        string path = Path.Combine(syncRoot, name);
        var native = SetNativeOutOfSync(path);
        TestContext.WriteLine($"CfSharp.Native direct coordination: HRESULT=0x{native.HResult:X8}, USN={native.Usn}.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "fsutil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "usn", "readdata", path }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Match match = Regex.Match(await output, @"(?im)^\s*USN\s*:\s*(0x[0-9a-f]+)");
        await error;
        TestContext.WriteLine($"Windows file USN query: exit={process.ExitCode}, USN={(match.Success ? match.Groups[1].Value : "unavailable")}.");
        Assert.AreEqual("coordination probe", await File.ReadAllTextAsync(path, token));
        await file.DeleteAsync(token);
    }

    private static unsafe (int HResult, long Usn) SetNativeOutOfSync(string path)
    {
        // Diagnostic only: use the library's public native declaration against a standard
        // handle to this disposable fixture, without adding a product fallback.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        long usn = 0;
        int result = CfApi.CfSetInSyncState(handle.DangerousGetHandle(), CfInSyncState.NotInSync, CfSetInSyncFlags.None, &usn);
        return (result, usn);
    }

    private sealed class DiskWorker(string root) : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerMutationTransport
    {
        public Dictionary<InstanceId, int> Uploads { get; } = [];
        public int Deletes { get; private set; }
        public string PathFor(InstanceId instance, string path) => Path.Combine(root, instance.ToString(), path.Replace('/', Path.DirectorySeparatorChar));
        private string? Revision(InstanceId instance, string path) => File.Exists(PathFor(instance, path))
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathFor(instance, path)))) : null;
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Revision(request.InstanceId, request.NormalizedPath));
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            string? actual = Revision(request.InstanceId, request.NormalizedPath);
            if (actual != request.ExpectedRevision) throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, actual);
            string path = PathFor(request.InstanceId, request.NormalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var target = File.Create(path)) await request.Content.CopyToAsync(target, cancellationToken);
            Uploads[request.InstanceId] = Uploads.GetValueOrDefault(request.InstanceId) + 1;
            return Revision(request.InstanceId, request.NormalizedPath)!;
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
        {
            string path = PathFor(request.InstanceId, request.NormalizedPath);
            MirrorPulseWorkerDirectoryEntry[] entries = Directory.Exists(path) ? Directory.GetFiles(path).Select(file =>
            {
                string relative = Path.GetRelativePath(PathFor(request.InstanceId, string.Empty), file).Replace('\\', '/');
                return new MirrorPulseWorkerDirectoryEntry("remote:" + relative, Revision(request.InstanceId, relative)!, "file", relative,
                    new FileInfo(file).Length, null, null, false);
            }).ToArray() : [];
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(entries, ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(File.ReadAllBytes(PathFor(request.InstanceId, request.NormalizedPath))
                .AsSpan((int)request.Offset, (int)request.Length).ToArray()));
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken)
        {
            string? actual = Revision(request.InstanceId, request.NormalizedPath);
            if (actual is not null && actual != request.ExpectedRevision) throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, actual);
            Deletes++;
            File.Delete(PathFor(request.InstanceId, request.NormalizedPath));
            return ValueTask.FromResult<string?>(null);
        }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
