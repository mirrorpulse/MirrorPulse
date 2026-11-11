using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseJournalUploadCompletionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeFailureRetryAndSuccessUseCfSharpJournalAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
        {
            Assert.Inconclusive("Requires the NativeCloudFiles test environment; run the dedicated verification gate.");
        }

        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]);
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        var instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.local"), instance,
            new AdapterRootDefinition("docs", "Documents", "Documents", false),
            RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var policy = new BackoffPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        try
        {
            cloud.Register(definition);
            Guid operationId = Guid.Empty;
            MirrorPulseWorkerChangeCommand originalCommand = null!;
            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                var fault = new MirrorPulseJournalAcknowledgementCommitFault();
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(fault.Wrap(state))
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
                    .Build();
                await fileSystem.StartAsync();
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
                var completion = new MirrorPulseJournalUploadCompletion(feed, state, policy);
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true, completion);

                if (run == 0)
                {
                    await feed.SuppressProviderEchoAsync(
                        CloudStateOperationKind.MetadataUpdate,
                        "Documents",
                        DateTimeOffset.UtcNow.AddMinutes(1));
                    await fileSystem.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children);
                    await File.WriteAllTextAsync(
                        Path.Combine(paths.SyncRootPath, "Documents", "report.txt"), "local change");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    MirrorPulseJournalUploadBatch pending = await source.ReadPendingAsync(timeout.Token);
                    Assert.IsNotEmpty(pending.ReadyCommands);
                    operationId = pending.ReadyCommands[0].OperationId;
                    originalCommand = pending.ReadyCommands[0];
                    RetryAfterDirective retry = await completion.DeferFailedUploadAsync(
                        operationId, DateTimeOffset.UtcNow);
                    Assert.AreEqual(1, retry.Attempt);
                    Assert.AreEqual(TimeSpan.FromMinutes(1), retry.Delay);
                    MirrorPulseJournalUploadBatch delayed = await source.ReadPendingAsync();
                    Assert.IsFalse(delayed.ReadyCommands.Any(command => command.OperationId == operationId));
                    Assert.IsGreaterThan(0, delayed.DeferredCount);
                }
                else
                {
                    DateTimeOffset? retryAfter = await completion.GetRetryAfterAsync(operationId);
                    Assert.IsNotNull(retryAfter);
                    Assert.IsGreaterThan(DateTimeOffset.UtcNow, retryAfter.Value);
                    MirrorPulseJournalUploadBatch delayed = await source.ReadPendingAsync();
                    Assert.IsFalse(delayed.ReadyCommands.Any(command => command.OperationId == operationId));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var file = fileSystem.GetFile("Documents/report.txt");
                    var before = await file.InspectAsync(deadline.Token);
                    byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Documents", "report.txt"), deadline.Token);
                    var accepted = router.CreateFileIdentity(instance, "docs", "report", "remote-revision-1");
                    var proof = new MirrorPulseContentAcceptanceProof(Guid.NewGuid(), MirrorPulseContentConfirmation.CaptureUploadBinding(before),
                        accepted.ItemId, accepted.RemoteId, accepted.RemoteRevision, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
                    string originalPayload;
                    await using (var read = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        var retained = (await read.Operations.GetAsync(operationId, deadline.Token))!;
                        Assert.IsNotNull(retained);
                        originalPayload = Convert.ToHexString(SHA256.HashData(retained.Payload.Span));
                        await read.RollbackAsync(deadline.Token);
                    }
                    var confirmed = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, deadline.Token);
                    for (int attempt = 1; ; attempt++)
                    {
                        TestContext.WriteLine($"Confirmation attempt={attempt}; outcome={confirmed.Outcome}; stage={confirmed.Stage}; " +
                            $"prepared={confirmed.NativeIdentityPrepared}; applied={confirmed.NativeApplied}; verified={confirmed.NativeConfirmationVerified}; " +
                            $"projected={confirmed.DurableProjectionCommitted}; nativeHResult={confirmed.NativeErrorHResult:X8}.");
                        if (attempt == 3 || confirmed.Outcome is not (MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost)) break;
                        await using (var retained = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                        {
                            var original = await retained.Operations.GetAsync(operationId, deadline.Token);
                            Assert.IsNotNull(original);
                            Assert.AreEqual(originalPayload, Convert.ToHexString(SHA256.HashData(original.Payload.Span)));
                            await retained.RollbackAsync(deadline.Token);
                        }
                        // Conversion may break the SDK's protected reference. Reuse the exact
                        // accepted proof: no recaptured binding, changed identity/hash or upload.
                        await Task.Delay(50, deadline.Token);
                        confirmed = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, deadline.Token);
                    }
                    Assert.IsTrue(confirmed.MayAcknowledge,
                        $"Confirmation outcome={confirmed.Outcome}; stage={confirmed.Stage}; nativeStage={confirmed.NativeStage}; " +
                        $"nativeApplied={confirmed.NativeApplied}; nativeVerified={confirmed.NativeConfirmationVerified}; " +
                        $"projectionCommitted={confirmed.DurableProjectionCommitted}; preparationHResult={confirmed.PreparationHResult}; " +
                        $"nativeHResult={confirmed.NativeMarkHResult}; projectionHResult={confirmed.ProjectionErrorHResult}.");
                    var snapshot = await file.InspectAsync(deadline.Token);
                    await using (var read = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        var retained = (await read.Operations.GetAsync(operationId, deadline.Token))!;
                        Assert.IsNotNull(retained);
                        Assert.AreEqual(originalPayload, Convert.ToHexString(SHA256.HashData(retained.Payload.Span)));
                        await read.RollbackAsync(deadline.Token);
                    }
                    using var log = new LocalRollingLogWriter(Path.Combine(paths.DataRootPath, "observation-test-logs"));
                    var acknowledgements = new MirrorPulseJournalAcceptedObservationAcknowledgement(state, completion, log);
                    foreach (var foreign in new[]
                    {
                        originalCommand with { InstanceId = InstanceId.New() }, originalCommand with { RootKey = "foreign" },
                        originalCommand with { IsDirectory = true }, originalCommand with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate }
                    })
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => acknowledgements.AcknowledgeAsync(
                            foreign, registration, snapshot, accepted.RemoteRevision, deadline.Token).AsTask());
                    Assert.IsFalse(File.Exists(log.FilePath));
                    fault.Arm(operationId);
                    await Assert.ThrowsExactlyAsync<IOException>(() => acknowledgements.AcknowledgeAsync(
                        originalCommand, registration, snapshot, accepted.RemoteRevision, deadline.Token).AsTask());
                    Assert.AreEqual(1, fault.Failures);
                    var failedLines = await File.ReadAllLinesAsync(log.FilePath, deadline.Token);
                    Assert.HasCount(1, failedLines);
                    using (var failed = JsonDocument.Parse(failedLines[0]))
                        Assert.AreEqual("JournalAcceptedContentObservation", failed.RootElement.GetProperty("Code").GetString());
                    await using (var retained = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        Assert.AreEqual(originalPayload, Convert.ToHexString(SHA256.HashData((await retained.Operations.GetAsync(operationId, deadline.Token))!.Payload.Span)));
                        await retained.RollbackAsync(deadline.Token);
                    }
                    await acknowledgements.AcknowledgeAsync(originalCommand, registration, snapshot, accepted.RemoteRevision, deadline.Token);
                    string[] lines = await File.ReadAllLinesAsync(log.FilePath, deadline.Token);
                    Assert.HasCount(3, lines);
                    using var failedAttempt = JsonDocument.Parse(lines[0]);
                    using var observed = JsonDocument.Parse(lines[1]);
                    using var acknowledged = JsonDocument.Parse(lines[2]);
                    Assert.AreEqual("JournalAcceptedContentObservation", observed.RootElement.GetProperty("Code").GetString());
                    Assert.AreEqual("JournalAcceptedContentAcknowledged", acknowledged.RootElement.GetProperty("Code").GetString());
                    var firstFields = observed.RootElement.GetProperty("Fields");
                    Assert.AreEqual(firstFields.GetRawText(), acknowledged.RootElement.GetProperty("Fields").GetRawText());
                    Assert.AreNotEqual(firstFields.GetProperty("correlationId").GetString(), failedAttempt.RootElement.GetProperty("Fields").GetProperty("correlationId").GetString());
                    Assert.AreEqual(operationId.ToString("D"), firstFields.GetProperty("operationId").GetString());
                    Assert.AreEqual(originalPayload, firstFields.GetProperty("journalPayloadSha256").GetString());
                    Assert.AreEqual(Convert.ToHexString(SHA256.HashData(snapshot.PlaceholderIdentity.Span)), firstFields.GetProperty("nativeIdentitySha256").GetString());
                    TestContext.WriteLine("Original accepted-content acknowledgement events:");
                    foreach (string line in lines) TestContext.WriteLine(line);
                    await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync();
                    Assert.IsNull(await transaction.Operations.GetAsync(operationId));
                    await transaction.RollbackAsync();
                }
            }
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
