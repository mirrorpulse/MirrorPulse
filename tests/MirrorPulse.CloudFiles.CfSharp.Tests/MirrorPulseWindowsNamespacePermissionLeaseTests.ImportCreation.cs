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
    public async Task NativeControlledImportPreparesWithoutCreationAndReplaysWithoutSourceAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.controlled-import"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var inputs = new MirrorPulseLocalImportStagingStore(paths);
        byte[] bytes = new byte[2_097_409]; new Random(41).NextBytes(bytes);
        byte[] newer = [4, 3, 2, 1];
        var requests = new[] { (Operation: Guid.NewGuid(), Name: "staged.bin", Bytes: bytes),
            (Operation: Guid.NewGuid(), Name: "empty.bin", Bytes: Array.Empty<byte>()) };
        var plans = new Dictionary<Guid, MirrorPulseNamespaceBirthPlan>();
        var imported = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        Guid parentId = Guid.Empty;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 4, 1]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 4; owner++)
            {
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
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var coordinator = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    parentId = members.Values.Single(value => value.IsDirectory && value.RootId is not null).MemberId;
                }
                foreach (var request in requests)
                {
                    var file = fileSystem.GetFile("Docs/" + request.Name);
                    if (owner == 0)
                    {
                        string inputPath = Path.Combine(directory, request.Name);
                        await File.WriteAllBytesAsync(inputPath, request.Bytes, deadline.Token);
                        var content = await role.RunNormalUserOperationAsync(() => inputs.StageAsync(request.Operation, inputPath, deadline.Token));
                        File.Delete(inputPath);
                        await using var input = await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(request.Operation, content, deadline.Token));
                        plans[request.Operation] = await coordinator.PrepareImportAsync(request.Operation, parentId, request.Name, input, deadline.Token);
                        Assert.AreEqual(plans[request.Operation], await coordinator.PrepareImportAsync(request.Operation, parentId, request.Name, input, deadline.Token));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareImportAsync(request.Operation,
                            parentId, "different.bin", input, deadline.Token).AsTask());
                        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteImportAsync(request.Operation, null, deadline.Token).AsTask());
                        Assert.IsFalse((await file.InspectAsync(deadline.Token)).Exists);
                        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(request.Operation, deadline.Token));
                        Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(request.Operation, deadline.Token));
                    }
                    else if (owner == 1 && request.Name == "staged.bin")
                    {
                        await using var input = await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(request.Operation,
                            plans[request.Operation].Content!, deadline.Token));
                        foreach (var interruption in new[] { MirrorPulseNamespaceCreationCheckpoint.AfterOriginalBinding,
                            MirrorPulseNamespaceCreationCheckpoint.AfterContentRecorded })
                        {
                            var interrupted = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor)
                            {
                                Checkpoint = (checkpoint, durable) =>
                                {
                                    Assert.IsFalse(durable.CanBeCanceled);
                                    if (checkpoint == interruption) throw new IOException("Injected controlled import interruption.");
                                    return ValueTask.CompletedTask;
                                }
                            };
                            var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ExecuteImportAsync(request.Operation, input, deadline.Token).AsTask());
                            StringAssert.Contains(failure.Message, "Injected controlled import interruption");
                            Assert.IsNotNull(await catalog.ReadNamespaceBirthConversionAsync(request.Operation, deadline.Token));
                            Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(request.Operation, deadline.Token));
                            Assert.IsNull(await catalog.ReadNamespaceMemberAsync(request.Operation, deadline.Token));
                        }
                        Assert.IsNotNull(await catalog.ReadNamespaceBirthContentCompletionAsync(request.Operation, deadline.Token));
                        Assert.IsFalse((await file.InspectAsync(deadline.Token)).IsPlaceholder);
                        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                    }
                    else if (owner is 1 or 2)
                    {
                        if (owner == 1)
                        {
                            await using var input = await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(request.Operation,
                                plans[request.Operation].Content!, deadline.Token));
                            imported[request.Operation] = await coordinator.ExecuteImportAsync(request.Operation, input, deadline.Token);
                        }
                        else imported[request.Operation] = await coordinator.ExecuteImportAsync(request.Operation, null, deadline.Token);
                        var completion = await catalog.ReadNamespaceBirthContentCompletionAsync(request.Operation, deadline.Token);
                        Assert.IsNotNull(completion); Assert.AreEqual(plans[request.Operation].Content, completion.Content);
                        Assert.AreEqual(imported[request.Operation].LocalObject, completion.Verification.LocalObject);
                        var conversion = await catalog.ReadNamespaceBirthConversionAsync(request.Operation, deadline.Token);
                        Assert.IsNotNull(conversion); Assert.AreEqual(conversion.LocalObject, completion.Verification.LocalObject);
                        var snapshot = await file.InspectAsync(deadline.Token);
                        Assert.IsTrue(snapshot.IsPlaceholder); Assert.AreEqual(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                        CollectionAssert.AreEqual(request.Bytes, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                        if (owner == 2)
                        {
                            inputs.RemoveCompleted(request.Operation);
                            await File.WriteAllBytesAsync(file.FullPath, newer, deadline.Token);
                        }
                    }
                    else
                    {
                        Assert.AreEqual(imported[request.Operation], await coordinator.ExecuteImportAsync(request.Operation, null, deadline.Token));
                        CollectionAssert.AreEqual(newer, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                    }
                    Assert.AreEqual(plans[request.Operation], await catalog.ReadNamespaceBirthPlanAsync(request.Operation, deadline.Token));
                    Assert.IsNull(await catalog.ReadMutationAsync(request.Operation, deadline.Token));
                }
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ControlledImport: prepareWithoutCreation=True; sourceRemoved=True; fourOwners=True; bindingInterruption=True; contentInterruption=True; emptyAndBinary=True; firstBindingBeforeCopy=True; contentBeforeConversion=True; missingInputCannotCreate=True; exactBinary=True; completedReplayPreservesNewerBytes=True; noRemoteAcceptance=True; noSourceAccess=True; hostIntegrated=False.");
        }
        catch (Exception error) { TestContext.WriteLine("ControlledImportFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreImportContentFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
