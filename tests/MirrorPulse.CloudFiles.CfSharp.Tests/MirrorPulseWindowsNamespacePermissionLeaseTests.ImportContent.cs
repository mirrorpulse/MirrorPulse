using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeImportContentRecoversOriginalBindingAndPreservesLaterEditsAcrossOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.import-content"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var firstRole = new MirrorPulseNamespaceExecutionSession();
        await using var nextRole = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var originalMembers = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var inputs = new MirrorPulseLocalImportStagingStore(paths);
        NativeStagedBirth staged = null!;
        MirrorPulseNamespaceImportContent content = null!;
        MirrorPulseNamespaceBirthContentCompletion completion = null!;
        MirrorPulseNamespaceMemberRegistration member = null!;
        Guid operationId = Guid.NewGuid();
        byte[] bytes = new byte[2_097_409]; new Random(29).NextBytes(bytes);
        byte[] newer = [9, 8, 7, 6];
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [3, 8, 6]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            string inputPath = Path.Combine(directory, "input.bin");
            await File.WriteAllBytesAsync(inputPath, bytes, deadline.Token);
            content = await firstRole.RunNormalUserOperationAsync(() => inputs.StageAsync(operationId, inputPath, deadline.Token));
            File.Delete(inputPath);
            for (int owner = 0; owner < 3; owner++)
            {
                var role = owner == 0 ? firstRole : nextRole;
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1], deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                }
                await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, originalMembers, actor, deadline.Token);
                    staged = await StageNativeAbandonedBirthAsync(fileSystem, state, catalog, router, role, root.InstanceId,
                        originals.Single(original => original.Baseline.RelativePath == "Docs"), [], deadline.Token, content, operationId);
                }
                if (owner == 1)
                {
                    var rotations = originals.Select(original => new MirrorPulseNamespacePermissionPreparation(original.Baseline, original.Intent with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                        ExpectedDacl = original.Intent.TargetDacl,
                        RoleSid = role.RoleSid.Value,
                        TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, original.Baseline.IsDirectory),
                        PreparedAt = DateTimeOffset.UtcNow
                    })).ToArray();
                    await catalog.PrepareNamespacePermissionChangesAsync(rotations, deadline.Token);
                    await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        foreach (var rotation in rotations.Reverse())
                            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                                (await permissions.ApplyProtectedAsync(rotation.Intent.OperationId,
                                    NativeOriginalMemberItem(fileSystem, rotation.Baseline), router, cancellationToken: deadline.Token)).Permission!.Outcome);
                    });
                }
                CloudFile file = fileSystem.GetFile(staged.Birth.RelativePath);
                CloudDirectory parent = fileSystem.GetDirectory("Docs");
                await using var input = owner < 2 ? await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(operationId, content, deadline.Token)) : null;
                await RunNativeMemberAdmissionAsync(actor, role, async () =>
                {
                    var writer = new MirrorPulseNamespaceImportContentWriter(catalog, router, role, actor);
                    if (owner == 0)
                    {
                        var recovery = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role);
                        var action = await recovery.PrepareStagedFileAsync(Guid.NewGuid(), operationId, file, parent, deadline.Token);
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => recovery.ExecuteStagedFileAsync(action.RecoveryId, file, parent, deadline.Token));
                        var interrupted = new MirrorPulseNamespaceImportContentWriter(catalog, router, role, actor)
                        {
                            Checkpoint = (checkpoint, durable) =>
                            {
                                Assert.IsFalse(durable.CanBeCanceled);
                                if (checkpoint == MirrorPulseNamespaceImportContentCheckpoint.AfterContentFlush)
                                    throw new IOException("Injected import interruption after byte flush.");
                                return ValueTask.CompletedTask;
                            }
                        };
                        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.CopyAsync(operationId, file, parent, input, deadline.Token));
                        StringAssert.Contains(failure.Message, "Injected import interruption");
                        Assert.IsNull(await catalog.ReadNamespaceBirthContentCompletionAsync(operationId, deadline.Token));
                        Assert.IsFalse((await file.InspectAsync(deadline.Token)).IsPlaceholder);
                        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(operationId, deadline.Token));
                    }
                    else if (owner == 1)
                    {
                        completion = await writer.CopyAsync(operationId, file, parent, input, deadline.Token);
                        Assert.AreEqual(staged.Conversion.LocalObject, completion.Verification.LocalObject);
                        Assert.AreEqual(content, completion.Content);
                        Assert.IsFalse((await file.InspectAsync(deadline.Token)).IsPlaceholder);
                        var recovery = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role);
                        var action = (await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(operationId, deadline.Token))!;
                        Assert.IsNotNull((await recovery.ExecuteStagedFileAsync(action.RecoveryId, file, parent, deadline.Token)).Verification);
                        var observation = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, operationId, file, router, deadline.Token);
                        var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, Guid.NewGuid(), operationId,
                            file, parent, router, deadline.Token);
                        var parentMember = originalMembers.Values.Single(item => item.IsDirectory && item.RootId is not null);
                        member = new(1, operationId, parentMember.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth,
                            operationId, protection.ProtectionId, root.RootId, staged.Birth.RelativePath, observation.LocalObject,
                            false, observation.OwnerSid, DateTimeOffset.UtcNow);
                        await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, file, parent, router, actor, deadline.Token);
                    }
                    else
                    {
                        Assert.AreEqual(completion, await writer.CopyAsync(operationId, file, parent, null, deadline.Token));
                        Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(operationId, deadline.Token));
                    }
                }, deadline.Token);
                if (owner == 0)
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                else if (owner == 1)
                {
                    CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                    Assert.AreEqual(content.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.FullPath, deadline.Token))));
                    await input!.DisposeAsync(); inputs.RemoveCompleted(operationId);
                    await File.WriteAllBytesAsync(file.FullPath, newer, deadline.Token);
                }
                else CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                Assert.AreEqual(staged.Birth, await catalog.ReadNamespaceBirthAsync(operationId, deadline.Token));
                Assert.AreEqual(staged.Conversion, await catalog.ReadNamespaceBirthConversionAsync(operationId, deadline.Token));
                Assert.IsNull(await catalog.ReadMutationAsync(operationId, deadline.Token));
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ImportContent: sourceRemoved=True; originalBindingBeforeCopy=True; flushInterruption=True; roleRotation=True; threeOwners=True; privateInputRevalidated=True; contentBeforeConversion=True; ordinaryContentDenied=True; exactBinary=True; membership=True; completedReplayPreservesNewerBytes=True; noRemoteAcceptance=True; noSourceAccess=True; hostIntegrated=False.");
        }
        catch (Exception error) { TestContext.WriteLine("ImportContentFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreImportContentFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreImportContentFixtureForCleanup(string syncRoot, SecurityIdentifier user)
    {
        // The preceding root helper validates the exact GUID fixture, marker and root.
        var file = new FileInfo(Path.Combine(syncRoot, "Docs", "staged.bin"));
        if (!file.Exists) return;
        if (file.LinkTarget is not null) throw new InvalidOperationException("Import fixture cleanup cannot follow a file link.");
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
    }
}
