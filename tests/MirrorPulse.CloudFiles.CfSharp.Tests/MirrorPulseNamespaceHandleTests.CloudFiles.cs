using System.Runtime.InteropServices;
using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;

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
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "unaccepted-local", string.Empty);
        CloudLocalFileBinding? originalBinding = null;
        string? originalDacl = null;
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
                }

                CloudFile file = fileSystem.GetFile("Docs/unsent.txt");
                CloudItemSnapshot before = await file.InspectAsync(timeout.Token);
                if (owner == 0)
                {
                    Assert.IsFalse(before.IsPlaceholder);
                    originalBinding = before.LocalBinding;
                    Assert.IsNotNull(originalBinding);
                    originalDacl = ReadDacl(target);
                }
                else
                {
                    Assert.IsTrue(before.IsPlaceholder);
                    Assert.AreEqual(identity.ItemId, before.ItemId);
                    CollectionAssert.AreEqual(identity.Encode(), before.PlaceholderIdentity.ToArray());
                }

                await using (var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(file, router, timeout.Token))
                {
                    MirrorPulseNamespacePermissionObject observed = await lease.InspectAsync(timeout.Token);
                    Assert.AreEqual(originalBinding!.LocalFileId, observed.LocalObject.LocalFileId);
                    if (owner == 0)
                    {
                        CloudPlaceholderMutationResult converted = await file.ConvertToPlaceholderAsync(identity, cancellationToken: timeout.Token);
                        Assert.IsTrue(converted.DurableStateUpdated);
                        Assert.IsTrue(converted.Snapshot.IsPlaceholder);
                    }
                    await AssertPlaceholderRetainedAsync(file);
                    AssertHardLinkDenied(inside);
                    AssertHardLinkDenied(outside);
                    latest += " retained edit";
                    await File.WriteAllTextAsync(target, latest, timeout.Token);
                    await AssertPlaceholderRetainedAsync(file);
                }
                AssertHardLinkDenied(inside);
                AssertHardLinkDenied(outside);

                string originalAliasedDacl = ReadDacl(aliased);
                CloudFile rejected = fileSystem.GetFile("Docs/aliased.txt");
                CloudItemSnapshot rejectedBefore = await rejected.InspectAsync(timeout.Token);
                Assert.IsFalse(rejectedBefore.IsPlaceholder);
                CloudFilesException failure = await Assert.ThrowsAsync<CloudFilesException>(() =>
                    rejected.ConvertToPlaceholderAsync(new(Guid.NewGuid(), "unaccepted-aliased", string.Empty), cancellationToken: timeout.Token).AsTask());
                TestContext.WriteLine($"PlaceholderAliasConversionRefused: owner={owner}; nativeHResult={failure.HResult:X8}; nativeError={failure.Win32ErrorCode}; originalPermissionsRetained={ReadDacl(aliased) == originalAliasedDacl}.");
                Assert.AreEqual(396, failure.Win32ErrorCode); // ERROR_CLOUD_FILE_INCOMPATIBLE_HARDLINKS.
                CloudItemSnapshot refused = await rejected.InspectAsync(timeout.Token);
                Assert.IsFalse(refused.IsPlaceholder);
                Assert.AreEqual(rejectedBefore.LocalBinding, refused.LocalBinding);
                Assert.AreEqual(originalAliasedDacl, ReadDacl(aliased));
                Assert.AreEqual(originalAliasedDacl, ReadDacl(retainedAlias));
                Assert.AreEqual("aliased unaccepted bytes", await File.ReadAllTextAsync(retainedAlias, timeout.Token));
                Assert.AreEqual(0, provider.Reads);
                TestContext.WriteLine($"PlaceholderAliasBoundary: architecture={RuntimeInformation.ProcessArchitecture}; owner={owner}; actualPolicy=Disallowed; publicConversion=True; originalBindingRetained=True; originalPermissionsRetained=True; latestBytesRetained=True; nativeInSync=False; sourceReads=0; aclWrites=0; installedIdentity=False; productIntegrated=False.");
            }
            Assert.AreEqual(latest, await File.ReadAllTextAsync(target, timeout.Token));

            async Task AssertPlaceholderRetainedAsync(CloudFile file)
            {
                CloudItemSnapshot snapshot = await file.InspectAsync(timeout.Token);
                Assert.IsTrue(snapshot.IsPlaceholder);
                Assert.AreEqual(originalBinding, snapshot.LocalBinding);
                Assert.AreEqual(CloudContentAvailability.FullyAvailable, snapshot.ContentAvailability);
                Assert.AreEqual(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                Assert.IsTrue(string.IsNullOrEmpty(snapshot.RemoteRevision));
                Assert.AreEqual(originalDacl, ReadDacl(target));
                Assert.AreEqual(latest, await File.ReadAllTextAsync(target, timeout.Token));
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
