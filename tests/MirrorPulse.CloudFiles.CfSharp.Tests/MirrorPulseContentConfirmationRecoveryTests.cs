using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseContentConfirmationRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRejectedProductProofDoesNotPrepareOrModifyContent()
    {
        RequireNative();
        byte[] bytes = [1, 2, 3, 4];
        await using Fixture fixture = await Fixture.StartAsync(bytes, placeholder: false);
        MirrorPulseContentAcceptanceProof proof = await fixture.CaptureProofAsync(bytes);
        MirrorPulseContentAcceptanceProof[] rejected =
        [
            proof with { Sha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 3, 2, 1 })) },
            proof with { Length = bytes.Length + 1 },
            proof with { UploadBinding = proof.UploadBinding with { LocalObject = proof.UploadBinding.LocalObject with { VolumeSerialNumber = ulong.MaxValue } } },
            proof with { UploadBinding = proof.UploadBinding with { LocalObject = proof.UploadBinding.LocalObject with { SyncRootFileId = Guid.NewGuid() } } },
            proof with { UploadBinding = proof.UploadBinding with { LocalObject = proof.UploadBinding.LocalObject with { LocalFileId = Guid.NewGuid() } } },
        ];
        foreach (MirrorPulseContentAcceptanceProof candidate in rejected)
        {
            MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, candidate, default);
            Assert.IsTrue(receipt.Outcome is MirrorPulseContentConfirmationOutcome.ContentMismatch or MirrorPulseContentConfirmationOutcome.LocalObjectMismatch);
            Assert.IsFalse(receipt.NativeIdentityPrepared);
            Assert.IsFalse(receipt.NativeApplied);
            Assert.IsFalse(receipt.MayAcknowledge);
            Assert.IsFalse((await fixture.File.InspectAsync()).IsPlaceholder);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        }
        await fixture.File.ConvertToPlaceholderAsync(fixture.Identity);
        await fixture.File.SetInSyncAsync(false);
        proof = await fixture.CaptureProofAsync(bytes);
        MirrorPulseContentAcceptanceProof stale = proof with
        {
            AcceptedRevision = "next",
            UploadBinding = proof.UploadBinding with { PreviousPlaceholderIdentity = Convert.ToBase64String([9, 8, 7]) },
        };
        Assert.AreEqual(MirrorPulseContentConfirmationOutcome.IdentityMismatch,
            (await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, stale, default)).Outcome);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        TestContext.WriteLine("Confirmation matrix: contentLengthHashBindingAndIdentityRejected=True; contentPreserved=True.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeProductProofProjectionFailureReplaysAfterRuntimeRestart()
    {
        RequireNative();
        byte[] bytes = [1, 2, 3, 4];
        await using Fixture fixture = await Fixture.StartAsync(bytes);
        MirrorPulseContentAcceptanceProof proof = await fixture.CaptureProofAsync(bytes);
        proof = proof with { AcceptedRevision = "new-acceptance" };
        MirrorPulseMutationIntent intent = Intent(proof);
        int uploads = 0;
        MirrorPulseContentConfirmationReceipt? pending = null;
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => new MirrorPulseMutationExecutor(fixture.Catalog).ExecuteAsync(intent,
            _ => { uploads++; return ValueTask.FromResult<string?>(proof.AcceptedRevision); }, async (_, token) =>
            {
                await fixture.Catalog.SaveContentAcceptanceProofAsync(proof, token);
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    fixture.Faults.FailNextCommit = true;
                    pending = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, token);
                    await fixture.Catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, pending, CancellationToken.None);
                    if (pending.NativeConfirmationVerified) break;
                    Assert.AreEqual(MirrorPulseContentConfirmationOutcome.ProtectionLost, pending.Outcome);
                    await Task.Delay(50, token);
                }
                Assert.AreEqual(MirrorPulseContentConfirmationOutcome.NativeAppliedProjectionPending, pending!.Outcome);
                Assert.IsTrue(pending.NativeApplied);
                Assert.IsTrue(pending.NativeConfirmationVerified);
                Assert.IsFalse(pending.DurableProjectionCommitted);
                Assert.IsFalse(pending.MayAcknowledge);
                throw new MirrorPulseMutationAmbiguousException();
            }, default).AsTask());
        Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await fixture.Catalog.ReadMutationAsync(intent.OperationId))!.State);
        await fixture.RestartAsync();
        Assert.AreEqual(proof, await fixture.Catalog.ReadContentAcceptanceProofAsync(intent.OperationId));
        Assert.AreEqual(pending, await fixture.Catalog.ReadContentConfirmationReceiptAsync(intent.OperationId));
        MirrorPulseMutationRecord record = (await fixture.Catalog.ReadMutationAsync(intent.OperationId))!;
        await new MirrorPulseMutationExecutor(fixture.Catalog).ReconcileAsync(record,
            (_, _) => throw new AssertFailedException("A retained proof must not trigger another remote readback or upload."), async (_, token) =>
            {
                MirrorPulseContentConfirmationReceipt repaired = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, token);
                Assert.AreEqual(MirrorPulseContentConfirmationOutcome.AlreadyConfirmed, repaired.Outcome);
                Assert.IsFalse(repaired.NativeApplied);
                Assert.IsTrue(repaired.MayAcknowledge);
                await fixture.Catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, repaired, CancellationToken.None);
            }, default);
        Assert.AreEqual(1, uploads);
        Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await fixture.Catalog.ReadMutationAsync(intent.OperationId))!.State);
        Assert.AreEqual(proof.AcceptedRevision, (await fixture.File.InspectAsync()).RemoteRevision);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        TestContext.WriteLine("Confirmation matrix: nativeAppliedProjectionPending=True; runtimeReopened=True; sameProofReplay=True; uploads=1.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativePostMarkCancellationRetainsVerifiedReceipt() => VerifyPostMarkAsync(false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativePostMarkWriteKeepsNewContentDirty() => VerifyPostMarkAsync(true);

    private async Task VerifyPostMarkAsync(bool write)
    {
        RequireNative();
        byte[] bytes = [1, 2, 3, 4];
        await using Fixture fixture = await Fixture.StartAsync(bytes);
        MirrorPulseContentAcceptanceProof proof = await fixture.CaptureProofAsync(bytes);
        using var cancellation = new CancellationTokenSource();
        fixture.Faults.BeforeCommit = async () =>
        {
            if (write)
            {
                await using var writer = new FileStream(fixture.File.FullPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                await writer.WriteAsync(new byte[] { 4, 3, 2, 1 });
            }
            else cancellation.Cancel();
        };
        MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, cancellation.Token);
        Assert.IsTrue(receipt.NativeApplied);
        Assert.IsTrue(receipt.NativeConfirmationVerified);
        Assert.IsTrue(receipt.DurableProjectionCommitted);
        Assert.IsTrue(receipt.MayAcknowledge);
        Assert.AreEqual(!write, cancellation.IsCancellationRequested);
        CloudItemSnapshot current = await fixture.File.InspectAsync();
        Assert.AreEqual(write ? CloudSynchronizationState.NotInSync : CloudSynchronizationState.InSync, current.SynchronizationState);
        CollectionAssert.AreEqual(write ? new byte[] { 4, 3, 2, 1 } : bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        TestContext.WriteLine($"Confirmation matrix: postMarkWrite={write}; pastNativeVerified=True; projectionCommitted=True; laterDirty={write}.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeSegmentedCancellationDrainsBeforeReturning() => VerifyDrainAsync(false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeSegmentedDisposalDrainsBeforeReturning() => VerifyDrainAsync(true);

    private async Task VerifyDrainAsync(bool dispose)
    {
        RequireNative();
        byte[] block = new byte[1024 * 1024];
        await using Fixture fixture = await Fixture.StartAsync([]);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var writer = new FileStream(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            for (int index = 0; index < 64; index++)
            {
                await writer.WriteAsync(block);
                digest.AppendData(block);
            }
        }
        CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
        var proof = new MirrorPulseContentAcceptanceProof(Guid.NewGuid(), MirrorPulseContentConfirmation.CaptureUploadBinding(snapshot),
            fixture.Identity.ItemId, fixture.Identity.RemoteId, fixture.Identity.RemoteRevision, 64L * block.Length,
            Convert.ToHexString(digest.GetHashAndReset()));
        CloudContentConfirmationRequest mapped = MirrorPulseContentConfirmation.CreateRequest(proof);
        var request = new CloudContentConfirmationRequest(mapped.ExpectedBinding, mapped.AcceptedIdentity, mapped.ExpectedLength,
            mapped.ExpectedSha256.Span, mapped.Preparation, mapped.ExpectedPlaceholderIdentity.Span, segmentSize: 1024);
        using var cancel = new CancellationTokenSource();
        CloudFile file = fixture.File;
        Task<CloudContentConfirmationResult> confirming = Task.Run(async () => await file.ConfirmUploadedContentAsync(request, cancel.Token));
        string nativePath = fixture.File.FullPath;
        await Task.Delay(100);
        Task? stopping = null;
        if (dispose) stopping = fixture.System.DisposeAsync().AsTask();
        else cancel.Cancel();
        MirrorPulseContentConfirmationReceipt receipt = MirrorPulseContentConfirmation.MapReceipt(
            await confirming.WaitAsync(TimeSpan.FromSeconds(10)));
        if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(MirrorPulseContentConfirmationOutcome.Canceled, receipt.Outcome);
        Assert.IsFalse(receipt.NativeApplied);
        Assert.IsFalse(receipt.MayAcknowledge);
        Assert.IsTrue(receipt.BytesVerified > 0 && receipt.BytesVerified < proof.Length, "The native segmented read must have started but not finished.");
        // No opaque handle or pending read may survive the returned cancellation/disposal.
        using (FileStream exclusive = new(nativePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(proof.Length, exclusive.Length);
        await fixture.RestartAsync();
        Assert.AreEqual(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        TestContext.WriteLine($"Confirmation matrix: duringRead=True; dispose={dispose}; drained=True; verifiedBytes={receipt.BytesVerified}; nativeApplied=False.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeWritableHandlePreventsProductConfirmation() => VerifyWriterAsync(false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeWritableMappingPreventsProductConfirmation() => VerifyWriterAsync(true);

    private async Task VerifyWriterAsync(bool mapping)
    {
        RequireNative();
        byte[] bytes = [1, 2, 3, 4];
        await using Fixture fixture = await Fixture.StartAsync(bytes);
        MirrorPulseContentAcceptanceProof proof = await fixture.CaptureProofAsync(bytes);
        using (var writer = new FileStream(fixture.File.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            using MemoryMappedFile? mapped = mapping ? MemoryMappedFile.CreateFromFile(writer, null, 0,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true) : null;
            using MemoryMappedViewAccessor? view = mapped?.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (mapping) writer.Dispose();
            MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, default);
            Assert.IsTrue(receipt.Outcome is MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost);
            Assert.IsFalse(receipt.NativeApplied);
            Assert.IsFalse(receipt.MayAcknowledge);
        }
        Assert.IsTrue((await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, default)).MayAcknowledge);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        TestContext.WriteLine($"Confirmation matrix: existingMapping={mapping}; blockedWhileWritable=True; confirmedAfterRelease=True.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeQueuedWriterInvalidatesSegmentedProductProof()
    {
        RequireNative();
        await using Fixture fixture = await Fixture.StartAsync([]);
        byte[] block = new byte[1024 * 1024];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var writer = new FileStream(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.None))
            for (int index = 0; index < 64; index++) { await writer.WriteAsync(block); digest.AppendData(block); }
        CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
        var proof = new MirrorPulseContentAcceptanceProof(Guid.NewGuid(), MirrorPulseContentConfirmation.CaptureUploadBinding(snapshot),
            fixture.Identity.ItemId, fixture.Identity.RemoteId, fixture.Identity.RemoteRevision, 64L * block.Length,
            Convert.ToHexString(digest.GetHashAndReset()));
        CloudContentConfirmationRequest mapped = MirrorPulseContentConfirmation.CreateRequest(proof);
        var request = new CloudContentConfirmationRequest(mapped.ExpectedBinding, mapped.AcceptedIdentity, mapped.ExpectedLength,
            mapped.ExpectedSha256.Span, mapped.Preparation, mapped.ExpectedPlaceholderIdentity.Span, segmentSize: 1024);
        CloudFile file = fixture.File;
        Task<CloudContentConfirmationResult> confirming = Task.Run(async () => await file.ConfirmUploadedContentAsync(request));
        await Task.Delay(100);
        Assert.IsFalse(confirming.IsCompleted, "The writer must enter while the segmented verifier is active.");
        Task changing = Task.Run(async () =>
        {
            await using var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            await writer.WriteAsync(new byte[] { 255 });
        });
        MirrorPulseContentConfirmationReceipt receipt = MirrorPulseContentConfirmation.MapReceipt(await confirming.WaitAsync(TimeSpan.FromSeconds(10)));
        await changing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(receipt.Outcome is MirrorPulseContentConfirmationOutcome.ProtectionLost or MirrorPulseContentConfirmationOutcome.ContentMismatch);
        Assert.IsFalse(receipt.NativeApplied);
        Assert.IsFalse(receipt.MayAcknowledge);
        Assert.AreEqual(CloudSynchronizationState.NotInSync, (await file.InspectAsync()).SynchronizationState);
        using (var current = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.AreEqual(255, current.ReadByte());
        TestContext.WriteLine("Confirmation matrix: queuedWriterProgressed=True; segmentedProofInvalidated=True; nativeApplied=False.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeOnlineContentAndReparseAreNotAdoptedOrHydrated()
    {
        RequireNative();
        await using Fixture fixture = await Fixture.StartAsync([1, 2, 3, 4]);
        MirrorPulseContentAcceptanceProof proof = await fixture.CaptureProofAsync([1, 2, 3, 4]);
        Assert.IsTrue((await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, default)).MayAcknowledge);
        await fixture.File.DehydrateAsync(CloudFileRange.ToEnd(0));
        CloudItemSnapshot online = await fixture.File.InspectAsync();
        Assert.AreEqual(CloudContentAvailability.OnlineOnly, online.ContentAvailability);
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseContentConfirmation.CaptureUploadBinding(online));
        MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.File, proof, default);
        Assert.AreEqual(MirrorPulseContentConfirmationOutcome.NotFullyLocal, receipt.Outcome);
        Assert.AreEqual(0, receipt.BytesVerified);
        Assert.IsFalse(receipt.NativeApplied);
        string outside = Path.Combine(fixture.Root, "outside.bin");
        await File.WriteAllBytesAsync(outside, [9, 8, 7]);
        File.CreateSymbolicLink(Path.Combine(fixture.Paths.SyncRootPath, "link.bin"), outside);
        MirrorPulseContentConfirmationReceipt linked = await MirrorPulseContentConfirmation.ConfirmAsync(fixture.System.GetFile("link.bin"), proof, default);
        Assert.AreEqual(MirrorPulseContentConfirmationOutcome.NotApplicable, linked.Outcome);
        Assert.AreEqual(0, linked.BytesVerified);
        Assert.IsFalse(linked.MayAcknowledge);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outside));
        TestContext.WriteLine("Confirmation matrix: onlineRefused=True; reparseRefused=True; noHydration=True.");
    }

    private static void RequireNative()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
    }

    private static MirrorPulseMutationIntent Intent(MirrorPulseContentAcceptanceProof proof) => new(proof.OperationId, InstanceId.New(), "local",
        MirrorPulseWorkerChangeKind.ContentUpdate, "content.bin", null, false, "before", proof.Length, proof.Sha256,
        MirrorPulseMutationOrigin.Journal, proof.UploadBinding);

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public CloudFileSystem System { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public FaultFactory Faults { get; private set; } = null!;
        public CloudPlaceholderIdentity Identity { get; } = new(Guid.NewGuid(), "remote", "accepted");
        public CloudFile File => System.GetFile("content.bin");

        public static async Task<Fixture> StartAsync(byte[] content, bool placeholder = true)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Paths = new(Path.Combine(fixture.Root, "sync"), Path.Combine(fixture.Root, "data"));
                Directory.CreateDirectory(fixture.Paths.SyncRootPath);
                CloudSyncRoot.Register(fixture.Paths.SyncRootPath, SyncRootRegistrationOptions.CreateBuilder("MirrorPulse-test", "1.0")
                    .WithInSyncPolicy(CloudInSyncPolicy.None).Build());
                await fixture.OpenAsync();
                await global::System.IO.File.WriteAllBytesAsync(fixture.File.FullPath, content);
                if (placeholder)
                {
                    await fixture.File.ConvertToPlaceholderAsync(fixture.Identity);
                    await fixture.File.SetInSyncAsync(false);
                }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<MirrorPulseContentAcceptanceProof> CaptureProofAsync(byte[] content) => new(Guid.NewGuid(),
            MirrorPulseContentConfirmation.CaptureUploadBinding(await File.InspectAsync()), Identity.ItemId, Identity.RemoteId,
            Identity.RemoteRevision, content.Length, Convert.ToHexString(SHA256.HashData(content)));

        private async Task OpenAsync()
        {
            Faults = new(MirrorPulseCfSharpStateStoreFactory.Create(Paths));
            System = new MirrorPulseCloudFileSystemBuilder(Paths).WithStateStore(Faults)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(Paths.SyncRootPath)).Build();
            await System.StartAsync();
            Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
        }

        public async Task RestartAsync()
        {
            await System.DisposeAsync();
            await Catalog.DisposeAsync();
            await OpenAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (System is not null) await System.DisposeAsync();
            if (Catalog is not null) await Catalog.DisposeAsync();
            if (Paths is not null && Directory.Exists(Paths.SyncRootPath)) CloudSyncRoot.Open(Paths.SyncRootPath).Unregister();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    // Test-only fault seams delegate every repository and transaction to official SQLite.
    private sealed class FaultFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        public bool FailNextCommit { get; set; }
        public Func<ValueTask>? BeforeCommit { get; set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class Store(ICloudStateStore inner, FaultFactory faults) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new Transaction(await inner.BeginTransactionAsync(cancellationToken), faults);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction inner, FaultFactory faults) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                if (faults.BeforeCommit is { } action)
                {
                    faults.BeforeCommit = null;
                    await action();
                }
                if (faults.FailNextCommit)
                {
                    faults.FailNextCommit = false;
                    throw new IOException("Injected official projection failure.");
                }
                await inner.CommitAsync(cancellationToken);
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
