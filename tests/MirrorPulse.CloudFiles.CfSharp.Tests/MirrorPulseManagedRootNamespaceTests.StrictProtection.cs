using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
public sealed partial class MirrorPulseManagedRootNamespaceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task NativeStrictTreeAclPreservesLatestBytesHydrationAndConfirmationAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        TestContext.WriteLine($"StrictTreeArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.strict-protection"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = current.User!;
        byte[] payload = Enumerable.Range(0, 131_089).Select(index => (byte)(index % 251)).ToArray();
        byte[] acceptedBytes = Encoding.UTF8.GetBytes("accepted latest in-place edit");
        CloudPlaceholderIdentity identity = router.CreateFileIdentity(instance, "docs", "online", "v1");
        MirrorPulseContentAcceptanceProof? originalProof = null;
        CloudLocalFileBinding? rootBinding = null;
        Guid retainedOperation = Guid.Empty;
        string managed = Path.Combine(paths.SyncRootPath, "Docs");
        string nested = Path.Combine(managed, "Nested");
        string unsent = Path.Combine(nested, "unsent.txt");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                var source = new StrictProtectionRangeSource(payload, instance);
                var provider = new MirrorPulseDemandProvider(router, source);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(provider).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    // This fixture validates the selected policy's native compatibility.
                    // It does not apply ACLs to any existing product or user directory.
                    DirectorySecurity acl = new DirectoryInfo(managed).GetAccessControl();
                    acl.AddAccessRule(new FileSystemAccessRule(user,
                        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Deny));
                    new DirectoryInfo(managed).SetAccessControl(acl);
                    Directory.CreateDirectory(nested);
                    await File.WriteAllTextAsync(unsent, "original unsent", timeout.Token);
                    await File.WriteAllTextAsync(unsent, "latest unsent", timeout.Token);
                    await File.WriteAllTextAsync(Path.Combine(nested, "replacement.tmp"), "replacement", timeout.Token);
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("online.bin", identity, payload.Length).WithInSyncState(true)
                            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                    await File.WriteAllBytesAsync(Path.Combine(managed, "accepted.txt"), acceptedBytes, timeout.Token);
                    CloudFile acceptedFile = fileSystem.GetFile(Path.Combine("Docs", "accepted.txt"));
                    originalProof = new(Guid.NewGuid(),
                        MirrorPulseContentConfirmation.CaptureUploadBinding(await acceptedFile.InspectAsync(timeout.Token)),
                        MirrorPulsePlaceholderIdentity.Create(instance, "accepted", "v2").ToCfSharp().ItemId,
                        "accepted", "v2", acceptedBytes.Length, Convert.ToHexString(SHA256.HashData(acceptedBytes)));
                }
                CloudItemSnapshot rootSnapshot = await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token);
                Assert.IsTrue(rootSnapshot.IsPlaceholder);
                Assert.IsNotNull(rootSnapshot.LocalBinding);
                rootBinding ??= rootSnapshot.LocalBinding;
                Assert.AreEqual(rootBinding, rootSnapshot.LocalBinding);
                CloudItemSnapshot ordinary = await fileSystem.GetFile(Path.Combine("Docs", "Nested", "unsent.txt")).InspectAsync(timeout.Token);
                Assert.IsFalse(ordinary.IsPlaceholder, "The latest unsent bytes must remain an ordinary-file protection case.");
                AssertFixtureDeleteDeny(managed, user);
                AssertFixtureDeleteDeny(nested, user);
                AssertFixtureDeleteDeny(unsent, user);
                foreach (string mode in new[] { "delete-child", "rename-child", "replace-child", "rename", "delete-tree" })
                {
                    NamespaceMutationProbeResult result = await RunNamespaceProcessAsync(root, mode, timeout.Token);
                    Assert.IsFalse(result.Completed, $"Strict protection allowed {mode} after restart={run}.");
                    Assert.AreEqual("latest unsent", await File.ReadAllTextAsync(unsent, timeout.Token));
                    CollectionAssert.AreEqual(acceptedBytes, await File.ReadAllBytesAsync(Path.Combine(managed, "accepted.txt"), timeout.Token));
                }
                // Actual CFAPI hydration and the product's public confirmation ACL
                // run while the deny ACEs stay installed throughout the tree.
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(managed, "online.bin"), timeout.Token));
                if (run == 0) Assert.IsGreaterThan(0, source.Hydrations);
                CloudFile accepted = fileSystem.GetFile(Path.Combine("Docs", "accepted.txt"));
                MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(accepted, originalProof!, timeout.Token);
                for (int attempt = 1; attempt < 3 && receipt.Outcome is
                    MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost; attempt++)
                {
                    await Task.Delay(50, timeout.Token);
                    receipt = await MirrorPulseContentConfirmation.ConfirmAsync(accepted, originalProof!, timeout.Token);
                }
                TestContext.WriteLine($"StrictTreeConfirmation: restart={run}; outcome={receipt.Outcome}; stage={receipt.Stage}; nativeHResult={receipt.NativeErrorHResult:X8}; projectionHResult={receipt.ProjectionErrorHResult:X8}.");
                Assert.IsTrue(receipt.NativeConfirmationVerified);
                Assert.IsTrue(receipt.DurableProjectionCommitted);
                Assert.IsTrue(receipt.MayAcknowledge);
                if (run == 1) Assert.AreEqual(MirrorPulseContentConfirmationOutcome.AlreadyConfirmed, receipt.Outcome);
                AssertFixtureDeleteDeny(accepted.FullPath, user);
                CloudOperationJournalEntry? pending = null;
                while (pending is null)
                {
                    await using (ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token))
                    {
                        CloudItemState? item = await transaction.Items.GetByRelativePathAsync(Path.Combine("Docs", "Nested", "unsent.txt"), timeout.Token);
                        if (item is not null)
                            pending = (await transaction.Operations.ListAsync(128, timeout.Token))
                                .FirstOrDefault(entry => entry.ItemId == item.ItemId && (run == 0 || entry.OperationId == retainedOperation));
                        await transaction.RollbackAsync(timeout.Token);
                    }
                    if (pending is null) await Task.Delay(20, timeout.Token);
                }
                if (run == 0) retainedOperation = pending.OperationId;
                else Assert.AreEqual(retainedOperation, pending.OperationId);
                TestContext.WriteLine($"StrictTreeProtection: restart={run}; latestOrdinaryBytesRetained=True; rootBindingStable=True; nativeHydration=True; originalPendingIdRetained=True; controlledOperationsIntegrated=False; OS={Environment.OSVersion.Version}.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(managed)) ResetMarkedFixtureAcl(root, managed, user);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AssertFixtureDeleteDeny(string path, SecurityIdentifier user)
    {
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.IsTrue(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(rule => rule.IdentityReference.Equals(user) && rule.AccessControlType == AccessControlType.Deny &&
                (rule.FileSystemRights & FileSystemRights.Delete) != 0), "The fixture must retain the current-user delete deny ACE.");
    }

    private static void ResetMarkedFixtureAcl(string root, string managed, SecurityIdentifier user)
    {
        string prefix = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _) ||
            !File.Exists(Path.Combine(root, ".mp-namespace-fixture")) || managed != Path.Combine(root, "sync", "Docs"))
            throw new InvalidOperationException("Only the marked disposable tree may receive cleanup permissions.");
        foreach (string path in new[] { managed }.Concat(Directory.EnumerateDirectories(managed, "*", SearchOption.AllDirectories)))
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        foreach (string path in Directory.EnumerateFiles(managed, "*", SearchOption.AllDirectories))
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
    }

    private sealed class StrictProtectionRangeSource(byte[] content, InstanceId instance) : IMirrorPulseWorkerRangeTransport
    {
        private int _hydrations;
        public int Hydrations => Volatile.Read(ref _hydrations);
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(instance, request.InstanceId);
            Assert.AreEqual("docs", request.RootKey);
            Assert.AreEqual("online.bin", request.NormalizedPath);
            Interlocked.Increment(ref _hydrations);
            return ValueTask.FromResult<Stream>(new MemoryStream(content.AsSpan(checked((int)request.Offset),
                checked((int)request.Length)).ToArray(), writable: false));
        }
    }
}
