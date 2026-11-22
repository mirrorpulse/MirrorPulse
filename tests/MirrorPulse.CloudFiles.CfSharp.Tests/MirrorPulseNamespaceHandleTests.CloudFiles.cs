using System.Runtime.InteropServices;
using System.Security.AccessControl;
using CfSharp;
using CfSharp.Native;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceHandleTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePlaceholderHardLinkPolicyPreservesUnacceptedBytesAndRejectsExistingAliasesAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests")) + Path.DirectorySeparatorChar;
        string fixture = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.placeholder-alias"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        string target = Path.Combine(paths.SyncRootPath, "Docs", "unsent.txt");
        string inside = Path.Combine(paths.SyncRootPath, "Docs", "inside-link.txt");
        string outside = Path.Combine(fixture, "outside-link.txt");
        string aliased = Path.Combine(paths.SyncRootPath, "Docs", "aliased.txt");
        string retainedAlias = Path.Combine(fixture, "retained-alias.txt");
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "unaccepted-local");
        CloudLocalFileBinding? originalBinding = null;
        string? originalDacl = null;
        string? verifiedDacl = null;
        string latest = "latest unaccepted synthetic bytes";
        var provider = new NoSourceReadProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Directory.CreateDirectory(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-namespace-handle-fixture"), "synthetic", timeout.Token);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            Assert.AreEqual(CloudHardLinkPolicy.Disallowed, CloudSyncRoot.Open(paths.SyncRootPath).GetInfo().HardLinkPolicy);
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(provider).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                    await File.WriteAllTextAsync(target, latest, timeout.Token);
                    Assert.IsTrue(CreateHardLink(outside, target, nint.Zero));
                    File.Delete(outside);
                    await File.WriteAllTextAsync(aliased, "aliased unaccepted bytes", timeout.Token);
                    Assert.IsTrue(CreateHardLink(retainedAlias, aliased, nint.Zero));
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("cold.bin", router.CreateFileIdentity(registration.InstanceId, "docs", "cold", "v1"), 16)
                            .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                }

                await VerifyColdAndDirectoryMetadataAsync(fileSystem);
                CloudFile file = fileSystem.GetFile("Docs/unsent.txt");
                CloudItemSnapshot before = await file.InspectAsync(timeout.Token);
                if (owner == 0)
                {
                    Assert.IsFalse(before.IsPlaceholder);
                    originalBinding = before.LocalBinding;
                    Assert.IsNotNull(originalBinding);
                    originalDacl = ReadDacl(target);
                }
                Assert.AreEqual(originalBinding, before.LocalBinding);
                var retainedBinding = new MirrorPulseLocalFileBinding(originalBinding!.VolumeSerialNumber,
                    originalBinding.SyncRootFileId, originalBinding.LocalFileId);
                IMirrorPulseNamespacePermissionLease? escaped = null;
                int aclWrites = 0;
                var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, retainedBinding, localIdentity: null,
                    async (lease, stop) =>
                {
                    escaped = lease;
                    var observed = await lease.InspectAsync(stop);
                    Assert.AreEqual(retainedBinding, observed.LocalObject);
                    Assert.AreEqual(registration.RootId, observed.RootId);
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(originalDacl!, observed.Dacl));
                    if (verifiedDacl is not null) Assert.AreEqual(verifiedDacl, observed.Dacl);
                    // Exact-item mature operations reuse the public scope. Select an existing
                    // official identity under that admission instead of rekeying pending work.
                    CloudItemSnapshot current = await file.InspectAsync(stop);
                    if (owner == 0 && current.ItemId is { } knownId)
                        identity = new(knownId, current.RemoteId ?? "unaccepted-local");
                    CloudPlaceholderMutationResult conversion = await file.ConvertToPlaceholderAsync(identity, cancellationToken: stop);
                    Assert.IsTrue(conversion.Snapshot.IsPlaceholder);
                    await AssertPlaceholderRetainedAsync(file, inspectBytes: false);
                    AssertProtectedHardLinkDenied(inside);
                    AssertProtectedHardLinkDenied(outside);
                    await AssertProtectedWriteDeniedAsync();
                    await Task.Yield();
                    await lease.ApplyDaclAsync(originalDacl!, stop);
                    aclWrites++;
                    string readback = (await lease.InspectAsync(stop)).Dacl;
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(originalDacl!, readback));
                    verifiedDacl ??= readback;
                    Assert.AreEqual(verifiedDacl, readback);
                    await AssertProtectedWriteDeniedAsync();
                    AssertProtectedHardLinkDenied(inside);
                    AssertProtectedHardLinkDenied(outside);
                }, timeout.Token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome, receipt.Error?.ToString());
                Assert.IsTrue(receipt.CallbackStarted && receipt.CallbackCompleted && receipt.Drained);
                Assert.IsTrue(receipt.NativeIdentityPrepared && receipt.DurableProjectionCommitted);
                Assert.IsTrue(receipt.AccessDescriptorApplied && receipt.AccessDescriptorReadBack);
                Assert.AreEqual(originalBinding, receipt.Snapshot!.LocalBinding);
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => escaped!.InspectAsync(default).AsTask());
                await AssertPlaceholderRetainedAsync(file);
                AssertHardLinkDenied(inside);
                AssertHardLinkDenied(outside);

                // The protection is finite. Preserve the original Windows regression outside
                // the scope, then require the same historical object on the next initialization.
                latest += " retained edit";
                await File.WriteAllTextAsync(target, latest, timeout.Token);
                CloudItemSnapshot afterEdit = await file.InspectAsync(timeout.Token);
                int nativeInfo = QueryNativePlaceholderInfo(target, out uint infoBytes);
                TestContext.WriteLine($"PlaceholderAfterOverwrite: architecture={RuntimeInformation.ProcessArchitecture}; owner={owner}; scopeReleased=True; placeholder={afterEdit.IsPlaceholder}; placeholderState={afterEdit.PlaceholderState}; reparsePoint={afterEdit.Attributes?.HasFlag(FileAttributes.ReparsePoint)}; synchronizationState={afterEdit.SynchronizationState}; nativeInfoHResult={nativeInfo:X8}; nativeInfoBytes={infoBytes}; originalBindingRetained={afterEdit.LocalBinding == originalBinding}; originalPermissionsRetained={ReadDacl(target) == verifiedDacl}; latestBytesRetained={await File.ReadAllTextAsync(target, timeout.Token) == latest}; sourceReads={provider.Reads}; aclWrites={aclWrites}.");
                Assert.IsFalse(afterEdit.IsPlaceholder);
                Assert.AreEqual(unchecked((int)0x80070178), nativeInfo);
                Assert.AreEqual(originalBinding, afterEdit.LocalBinding);
                Assert.AreEqual(verifiedDacl, ReadDacl(target));
                Assert.IsTrue(CreateHardLink(inside, target, nint.Zero));
                Assert.IsTrue(CreateHardLink(outside, target, nint.Zero));
                Assert.AreEqual(latest, await File.ReadAllTextAsync(outside, timeout.Token));
                File.Delete(inside);
                File.Delete(outside);

                // A cooperative cancellation retains protection until the actual callback exits.
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<CloudProtectedLocalOperationResult> draining = MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router,
                    retainedBinding, identity, async (_, stop) =>
                    {
                        using var signal = stop.Register(() => cancellationObserved.TrySetResult());
                        entered.TrySetResult();
                        await released.Task;
                    }, cancel.Token);
                try
                {
                    await entered.Task.WaitAsync(timeout.Token);
                    cancel.Cancel();
                    await cancellationObserved.Task.WaitAsync(timeout.Token);
                    Assert.IsFalse(draining.IsCompleted);
                    await AssertProtectedWriteDeniedAsync();
                    AssertProtectedHardLinkDenied(inside);
                }
                finally { released.TrySetResult(); }
                var canceled = await draining.WaitAsync(timeout.Token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Canceled, canceled.Outcome, canceled.Error?.ToString());
                Assert.IsTrue(canceled.CancellationRequested && canceled.Drained && canceled.NativeIdentityPrepared);
                await AssertPlaceholderRetainedAsync(file);

                var callbackFailure = new IOException("Synthetic callback failure after actual Access descriptor application.");
                var failed = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, retainedBinding, identity,
                    async (lease, stop) =>
                    {
                        await lease.ApplyDaclAsync(originalDacl!, stop);
                        throw callbackFailure;
                    }, timeout.Token);
                aclWrites++;
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.CallbackFailed, failed.Outcome);
                Assert.AreSame(callbackFailure, failed.Error);
                Assert.IsTrue(failed.AccessDescriptorApplied && failed.AccessDescriptorReadBack && failed.Drained);
                Assert.IsTrue(failed.NativeIdentityPrepared && failed.DurableProjectionCommitted);
                await AssertPlaceholderRetainedAsync(file);

                string originalAliasedDacl = ReadDacl(aliased);
                CloudFile rejected = fileSystem.GetFile("Docs/aliased.txt");
                CloudItemSnapshot rejectedBefore = await rejected.InspectAsync(timeout.Token);
                Assert.IsFalse(rejectedBefore.IsPlaceholder);
                var rejectedIdentity = new CloudPlaceholderIdentity(rejectedBefore.ItemId ?? Guid.NewGuid(),
                    rejectedBefore.RemoteId ?? "unaccepted-aliased");
                CloudFilesException failure = await Assert.ThrowsAsync<CloudFilesException>(() =>
                    rejected.ConvertToPlaceholderAsync(rejectedIdentity, cancellationToken: timeout.Token).AsTask());
                TestContext.WriteLine($"PlaceholderAliasConversionRefused: owner={owner}; nativeHResult={failure.HResult:X8}; nativeError={failure.Win32ErrorCode}; originalPermissionsRetained={ReadDacl(aliased) == originalAliasedDacl}.");
                Assert.AreEqual(396, failure.Win32ErrorCode); // ERROR_CLOUD_FILE_INCOMPATIBLE_HARDLINKS.
                CloudItemSnapshot refused = await rejected.InspectAsync(timeout.Token);
                Assert.IsFalse(refused.IsPlaceholder);
                Assert.AreEqual(rejectedBefore.LocalBinding, refused.LocalBinding);
                Assert.AreEqual(originalAliasedDacl, ReadDacl(aliased));
                Assert.AreEqual(originalAliasedDacl, ReadDacl(retainedAlias));
                Assert.AreEqual("aliased unaccepted bytes", await File.ReadAllTextAsync(retainedAlias, timeout.Token));
                var refusedScope = await rejected.RunProtectedLocalOperationAsync(CloudProtectedLocalOperationRequest.ForLocalConversion(
                    rejectedBefore.LocalBinding!, rejectedIdentity),
                    (_, _) => throw new AssertFailedException("An existing alias must prevent all permission callbacks."), timeout.Token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.NotApplicable, refusedScope.Outcome);
                Assert.IsFalse(refusedScope.CallbackStarted || refusedScope.NativeConverted || refusedScope.AccessDescriptorApplied);
                Assert.AreEqual(originalAliasedDacl, ReadDacl(retainedAlias));
                Assert.AreEqual(0, provider.Reads);
                TestContext.WriteLine($"PlaceholderAliasBoundary: architecture={RuntimeInformation.ProcessArchitecture}; owner={owner}; actualPolicy=Disallowed; protectedPublicConversion=True; protectedAccessReadWrite=True; cancellationDrained=True; originalBindingRetained=True; originalPermissionsRetained=True; latestBytesRetained=True; nativeInSync=False; sourceReads=0; aclWrites={aclWrites}; installedIdentity=False; productIntegrated=False.");
            }
            Assert.AreEqual(latest, await File.ReadAllTextAsync(target, timeout.Token));

            async Task VerifyColdAndDirectoryMetadataAsync(CloudFileSystem fileSystem)
            {
                CloudFile cold = fileSystem.GetFile("Docs/cold.bin");
                CloudItemSnapshot coldBefore = await cold.InspectAsync(timeout.Token);
                Assert.IsTrue(coldBefore.IsPlaceholder);
                Assert.AreEqual(0L, coldBefore.OnDiskDataSize);
                CloudLocalFileBinding coldBinding = coldBefore.LocalBinding!;
                var coldReceipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(cold, router,
                    new(coldBinding.VolumeSerialNumber, coldBinding.SyncRootFileId, coldBinding.LocalFileId), localIdentity: null,
                    async (lease, stop) =>
                    {
                        var metadata = await lease.InspectAsync(stop);
                        await lease.ApplyDaclAsync(metadata.Dacl, stop);
                        var observed = await lease.InspectAsync(stop);
                        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(metadata.Dacl, observed.Dacl));
                        Assert.AreEqual(metadata, observed with { Dacl = metadata.Dacl, ObservedAt = metadata.ObservedAt });
                        Assert.AreEqual(0L, (await cold.InspectAsync(stop)).OnDiskDataSize);
                    }, timeout.Token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, coldReceipt.Outcome, coldReceipt.Error?.ToString());
                Assert.IsTrue(coldReceipt.AccessDescriptorApplied && coldReceipt.AccessDescriptorReadBack && coldReceipt.Drained);
                Assert.IsFalse(coldReceipt.NativeConverted || coldReceipt.NativeIdentityPrepared);
                CloudItemSnapshot coldAfter = await cold.InspectAsync(timeout.Token);
                Assert.AreEqual(coldBinding, coldAfter.LocalBinding);
                Assert.AreEqual(coldBefore.ItemId, coldAfter.ItemId);
                Assert.AreEqual(coldBefore.RemoteRevision, coldAfter.RemoteRevision);
                Assert.AreEqual(0L, coldAfter.OnDiskDataSize);
                Assert.AreEqual(0, provider.Reads);

                CloudDirectory directory = fileSystem.GetDirectory("Docs");
                CloudLocalFileBinding directoryBinding = (await directory.InspectAsync(timeout.Token)).LocalBinding!;
                var directoryReceipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(directory, router,
                    new(directoryBinding.VolumeSerialNumber, directoryBinding.SyncRootFileId, directoryBinding.LocalFileId), localIdentity: null,
                    async (lease, stop) =>
                    {
                        var metadata = await lease.InspectAsync(stop);
                        Assert.IsTrue(metadata.IsDirectory);
                        await lease.ApplyDaclAsync(metadata.Dacl, stop);
                        var observed = await lease.InspectAsync(stop);
                        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(metadata.Dacl, observed.Dacl));
                        Assert.AreEqual(metadata, observed with { Dacl = metadata.Dacl, ObservedAt = metadata.ObservedAt });
                    }, timeout.Token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, directoryReceipt.Outcome, directoryReceipt.Error?.ToString());
                Assert.IsTrue(directoryReceipt.AccessDescriptorApplied && directoryReceipt.AccessDescriptorReadBack && directoryReceipt.Drained);
                Assert.IsFalse(directoryReceipt.NativeConverted || directoryReceipt.NativeIdentityPrepared);
                Assert.AreEqual(directoryBinding, (await directory.InspectAsync(timeout.Token)).LocalBinding);
            }

            async Task AssertPlaceholderRetainedAsync(CloudFile file, bool inspectBytes = true)
            {
                CloudItemSnapshot snapshot = await file.InspectAsync(timeout.Token);
                Assert.IsTrue(snapshot.IsPlaceholder);
                Assert.AreEqual(originalBinding, snapshot.LocalBinding);
                Assert.AreEqual(CloudContentAvailability.FullyAvailable, snapshot.ContentAvailability);
                Assert.AreEqual(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                Assert.IsNull(snapshot.RemoteRevision);
                if (inspectBytes)
                {
                    Assert.AreEqual(verifiedDacl, ReadDacl(target));
                    Assert.AreEqual(latest, await File.ReadAllTextAsync(target, timeout.Token));
                }
            }

            async Task AssertProtectedWriteDeniedAsync()
            {
                IOException refused = await Assert.ThrowsAsync<IOException>(() => File.WriteAllTextAsync(target, "forbidden competing write", timeout.Token));
                Assert.AreEqual(32, refused.HResult & 0xffff);
            }

            void AssertProtectedHardLinkDenied(string link)
            {
                bool created = CreateHardLink(link, target, nint.Zero);
                int error = created ? 0 : Marshal.GetLastPInvokeError();
                TestContext.WriteLine($"ProtectedScopeHardLinkAttempt: inside={link == inside}; created={created}; nativeError={error}.");
                Assert.IsFalse(created);
                Assert.IsTrue(error is 32 or 396, "Exclusive sharing or actual disallowed placeholder policy must reject the alias.");
                Assert.IsFalse(File.Exists(link));
            }

            void AssertHardLinkDenied(string link)
            {
                bool created = CreateHardLink(link, target, nint.Zero);
                int error = created ? 0 : Marshal.GetLastPInvokeError();
                TestContext.WriteLine($"PlaceholderHardLinkAttempt: inside={link == inside}; created={created}; nativeError={error}.");
                Assert.IsFalse(created);
                Assert.AreEqual(396, error); // ERROR_CLOUD_FILE_INCOMPATIBLE_HARDLINKS.
                Assert.IsFalse(File.Exists(link));
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            DeleteMarkedFixture(prefix, fixture);
        }
    }

    private static string ReadDacl(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.Access)
        .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static unsafe int QueryNativePlaceholderInfo(string path, out uint infoBytes)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> buffer = stackalloc byte[8192];
        fixed (byte* pointer = buffer)
        {
            uint returned = 0;
            int result = CfApi.CfGetPlaceholderInfo(handle.DangerousGetHandle(), CfPlaceholderInfoClass.Standard,
                pointer, (uint)buffer.Length, &returned);
            infoBytes = returned;
            return result;
        }
    }

    private sealed class NoSourceReadProvider : ICloudFileContentProvider
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            throw new NotSupportedException("The alias fixture contains only complete unaccepted local content.");
        }
    }
}
