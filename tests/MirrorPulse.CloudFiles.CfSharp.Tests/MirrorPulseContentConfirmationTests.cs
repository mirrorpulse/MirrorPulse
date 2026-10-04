using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseContentConfirmationTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void ProductProofMapsFullNativeBindingAndExactAcceptedIdentityToPublicRequest()
    {
        var binding = new MirrorPulseLocalFileBinding(ulong.MaxValue, Guid.NewGuid(), Guid.NewGuid());
        var proof = new MirrorPulseContentAcceptanceProof(Guid.NewGuid(),
            new(binding, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, Convert.ToBase64String([9, 8, 7])),
            Guid.NewGuid(), "remote-object", "accepted", 0, Convert.ToHexString(SHA256.HashData([])));
        CloudContentConfirmationRequest request = MirrorPulseContentConfirmation.CreateRequest(proof);
        Assert.AreEqual(binding.VolumeSerialNumber, request.ExpectedBinding.VolumeSerialNumber);
        Assert.AreEqual(binding.SyncRootFileId, request.ExpectedBinding.SyncRootFileId);
        Assert.AreEqual(binding.LocalFileId, request.ExpectedBinding.LocalFileId);
        Assert.AreEqual(proof.AcceptedItemId, request.AcceptedIdentity.ItemId);
        Assert.AreEqual(proof.AcceptedRevision, request.AcceptedIdentity.RemoteRevision);
        Assert.AreEqual(proof.Sha256, Convert.ToHexString(request.ExpectedSha256.Span));
        Assert.AreEqual(CloudContentPreparation.ReplacePlaceholderIdentity, request.Preparation);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, request.ExpectedPlaceholderIdentity.ToArray());
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeManagedConfirmationHonorsActualPolicyAndReplaysExactProof(bool tracked)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            CloudSyncRoot.Register(paths.SyncRootPath, SyncRootRegistrationOptions.CreateBuilder("MirrorPulse-test", "1.0")
                .WithInSyncPolicy(tracked ? CloudInSyncPolicy.TrackAll : CloudInSyncPolicy.None).Build());
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using CloudFileSystem fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await fileSystem.StartAsync();
            byte[] bytes = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(Path.Combine(paths.SyncRootPath, "content.bin"), bytes);
            CloudFile file = fileSystem.GetFile("content.bin");
            CloudItemSnapshot snapshot = await file.InspectAsync();
            MirrorPulseUploadBinding binding = MirrorPulseContentConfirmation.CaptureUploadBinding(snapshot);
            var proof = new MirrorPulseContentAcceptanceProof(Guid.NewGuid(), binding, Guid.NewGuid(), "remote", "accepted",
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, default);
            bool prepared = receipt.NativeIdentityPrepared;
            for (int attempt = 1; ; attempt++)
            {
                TestContext.WriteLine($"Confirmation: attempt={attempt}, tracked={tracked}, outcome={receipt.Outcome}, stage={receipt.Stage}, " +
                    $"nativeStage={receipt.NativeStage}, prepared={receipt.NativeIdentityPrepared}, applied={receipt.NativeApplied}, " +
                    $"verified={receipt.NativeConfirmationVerified}, projected={receipt.DurableProjectionCommitted}, bytes={receipt.BytesVerified}, " +
                    $"nativeHResult={receipt.NativeErrorHResult:X8}, projectionHResult={receipt.ProjectionErrorHResult:X8}");
                if (attempt == 3 || receipt.Outcome is not (MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost)) break;
                await Task.Delay(50);
                // Retry the identical durable request; no new binding, identity, hash or upload.
                receipt = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, default);
                prepared |= receipt.NativeIdentityPrepared;
            }
            if (tracked) Assert.AreEqual(MirrorPulseContentConfirmationOutcome.NotApplicable, receipt.Outcome);
            else Assert.IsTrue(receipt.Outcome is MirrorPulseContentConfirmationOutcome.Confirmed or MirrorPulseContentConfirmationOutcome.AlreadyConfirmed);
            Assert.AreEqual(!tracked, receipt.MayAcknowledge);
            Assert.AreEqual(!tracked, prepared);
            Assert.AreEqual(!tracked, receipt.NativeConfirmationVerified);
            Assert.AreEqual(!tracked, receipt.DurableProjectionCommitted);
            if (tracked)
            {
                Assert.AreEqual(0, receipt.BytesVerified);
                Assert.IsFalse((await file.InspectAsync()).IsPlaceholder);
            }
            else
            {
                CloudItemSnapshot confirmed = await file.InspectAsync();
                Assert.AreEqual(snapshot.LocalBinding, confirmed.LocalBinding);
                Assert.AreEqual(proof.RemoteId, confirmed.RemoteId);
                MirrorPulseContentConfirmationReceipt replay = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof, default);
                Assert.AreEqual(MirrorPulseContentConfirmationOutcome.AlreadyConfirmed, replay.Outcome);
                Assert.IsTrue(replay.MayAcknowledge);
                Assert.IsFalse(replay.NativeApplied);
                var replacementProof = proof with { UploadBinding = binding with { LocalObject = binding.LocalObject with { LocalFileId = Guid.NewGuid() } } };
                Assert.AreEqual(MirrorPulseContentConfirmationOutcome.LocalObjectMismatch,
                    (await MirrorPulseContentConfirmation.ConfirmAsync(file, replacementProof, default)).Outcome);
            }
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file.FullPath));
        }
        finally
        {
            if (Directory.Exists(paths.SyncRootPath)) CloudSyncRoot.Open(paths.SyncRootPath).Unregister();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
