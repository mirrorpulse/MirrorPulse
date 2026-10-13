using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeReplacementValidationRetainsBackupBindingAndIdentityAcrossPlaceholderAndOrdinaryStarts()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.replacement-validation"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var inputs = new MirrorPulseLocalImportStagingStore(paths);
        var backups = new MirrorPulseLocalReplacementBackupStore(paths);
        byte[] oldBytes = [1, 2, 3, 4, 5]; byte[] replacement = new byte[2_097_409]; new Random(53).NextBytes(replacement);
        Guid operation = Guid.NewGuid();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [5, 3, 1]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            string inputPath = Path.Combine(directory, "replacement.bin");
            await File.WriteAllBytesAsync(inputPath, replacement, deadline.Token);
            var content = await role.RunNormalUserOperationAsync(() => inputs.StageAsync(operation, inputPath, deadline.Token));
            File.Delete(inputPath);
            await using var input = await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(operation, content, deadline.Token));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
            await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
            var file = fileSystem.GetFile("Docs/original.bin");
            await File.WriteAllBytesAsync(file.FullPath, oldBytes, deadline.Token);
            await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
            var original = originals.Single(value => !value.Baseline.IsDirectory);
            var initial = await file.InspectAsync(deadline.Token);
            Assert.IsTrue(initial.IsPlaceholder); Assert.AreEqual(CloudContentAvailability.FullyAvailable, initial.ContentAvailability);
            Assert.IsTrue(CloudPlaceholderIdentity.TryDecode(initial.PlaceholderIdentity.Span, out var identity)); Assert.IsNotNull(identity);
            string roleOnly = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role);
            string restored = original.Intent.TargetDacl;
            await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] expectedOld = attempt == 0 ? oldBytes : [9, 8, 7, 6, 5, 4];
                if (attempt == 1)
                {
                    // A create-mode overwrite can make the file ordinary; its latest unsent bytes still need protection.
                    await File.WriteAllBytesAsync(file.FullPath, expectedOld, deadline.Token);
                    Assert.IsFalse((await file.InspectAsync(deadline.Token)).IsPlaceholder);
                }
                Guid backupOperation = Guid.NewGuid();
                var expectedBackup = new MirrorPulseNamespaceImportContent(expectedOld.LongLength, Convert.ToHexString(SHA256.HashData(expectedOld)));
                await role.RunNamespaceOperationAsync(async () =>
                {
                    var tightened = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, original.Baseline.LocalObject,
                        identity, async (lease, stop) =>
                        {
                            RequireObject(await lease.InspectAsync(stop), restored);
                            await lease.ApplyDaclAsync(roleOnly, stop);
                            RequireObject(await lease.InspectAsync(stop), roleOnly);
                        }, deadline.Token);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, tightened.Outcome);
                    Assert.IsTrue(tightened.CallbackCompleted && tightened.Drained && tightened.NativeIdentityPrepared && tightened.DurableProjectionCommitted);
                });
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                await role.RunNamespaceOperationAsync(async () =>
                {
                    await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, deadline.Token);
                    RequireObject(await metadata.InspectAsync(deadline.Token), roleOnly);
                    await using (var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        RequireObject(await metadata.InspectAsync(deadline.Token), roleOnly);
                        Assert.AreEqual(expectedBackup, await backups.StageRetainedAsync(backupOperation, target, deadline.Token));
                        await using var backup = await backups.OpenLeaseAsync(backupOperation, expectedBackup, deadline.Token);
                        Assert.AreEqual(expectedOld.LongLength, backup.Data.Length);
                        Assert.AreEqual(expectedBackup.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(backup.Data, deadline.Token)));
                        // The backup is durable before changing the same original object's bytes.
                        target.SetLength(0); input.Data.Position = 0;
                        await input.Data.CopyToAsync(target, CancellationToken.None); await target.FlushAsync(CancellationToken.None); target.Flush(true);
                        target.Position = 0;
                        Assert.AreEqual(content.Length, target.Length);
                        Assert.AreEqual(content.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(target, CancellationToken.None)));
                        RequireObject(await metadata.InspectAsync(CancellationToken.None), roleOnly);
                    }
                    var written = await file.InspectAsync(CancellationToken.None);
                    Assert.IsTrue(written.IsPlaceholder, "Opening the retained object without create-mode replacement must preserve its placeholder state.");
                    Assert.AreEqual(initial.LocalBinding, written.LocalBinding);
                    CollectionAssert.AreEqual(initial.PlaceholderIdentity.ToArray(), written.PlaceholderIdentity.ToArray());
                    await role.RunNormalUserOperationAsync(async () =>
                    {
                        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                        // The role-only descriptor rejects this ordinary caller even while the object is a placeholder.
                        AssertBirthAliasDenied(Path.Combine(directory, $"written-replacement-{attempt}.link"), file.FullPath, expectedError: 5);
                    });
                    // All data handles are closed before the mature public same-object conversion scope.
                    var converted = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, original.Baseline.LocalObject,
                        identity, async (lease, stop) =>
                        {
                            RequireObject(await lease.InspectAsync(stop), roleOnly);
                            await lease.ApplyDaclAsync(restored, stop);
                            RequireObject(await lease.InspectAsync(stop), restored);
                        }, CancellationToken.None);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, converted.Outcome);
                    Assert.IsTrue(converted.CallbackCompleted && converted.Drained && converted.NativeIdentityPrepared && converted.DurableProjectionCommitted);
                    RequireObject(await metadata.InspectAsync(CancellationToken.None), restored);
                });
                var final = await file.InspectAsync(deadline.Token);
                Assert.IsTrue(final.IsPlaceholder); Assert.AreEqual(CloudSynchronizationState.NotInSync, final.SynchronizationState);
                Assert.AreEqual(initial.LocalBinding, final.LocalBinding);
                CollectionAssert.AreEqual(initial.PlaceholderIdentity.ToArray(), final.PlaceholderIdentity.ToArray());
                CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                await role.RunNormalUserOperationAsync(async () =>
                {
                    await using var backup = await new MirrorPulseLocalReplacementBackupStore(paths).OpenLeaseAsync(backupOperation, expectedBackup, deadline.Token);
                    using var received = new MemoryStream(); await backup.Data.CopyToAsync(received, deadline.Token);
                    CollectionAssert.AreEqual(expectedOld, received.ToArray());
                });
                AssertBirthAliasDenied(Path.Combine(directory, $"converted-replacement-{attempt}.link"), file.FullPath);
            }
            for (int start = 0; start < 2; start++)
            {
                if (start == 1) await File.WriteAllBytesAsync(file.FullPath, replacement, deadline.Token);
                var beforeHandoff = await file.InspectAsync(deadline.Token);
                Assert.AreEqual(start == 0, beforeHandoff.IsPlaceholder);
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(
                    file, router, original.Baseline.LocalObject, caller.User!.Value, [restored], role, false, identity, deadline.Token));
                await role.RunNamespaceOperationAsync(async () =>
                {
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(
                        file, router, original.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() }, caller.User!.Value, [restored], role, false, identity, deadline.Token));
                    var tightened = await MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(file, router,
                        original.Baseline.LocalObject, caller.User!.Value, [restored], role, false, identity, deadline.Token);
                    RequireObject(tightened, roleOnly);
                });
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                AssertBirthAliasDenied(Path.Combine(directory, $"before-handoff-{start}.link"), file.FullPath, expectedError: 5);
                await using var nextRole = new MirrorPulseNamespaceExecutionSession();
                string nextRoleOnly = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(nextRole);
                await nextRole.RunNamespaceOperationAsync(async () =>
                {
                    var handed = await MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(file, router,
                        original.Baseline.LocalObject, caller.User!.Value, [roleOnly], nextRole, false, identity, deadline.Token);
                    RequireObject(handed, nextRoleOnly);
                    Assert.AreEqual(beforeHandoff.IsPlaceholder, (await file.InspectAsync(deadline.Token)).IsPlaceholder);
                    var replay = await MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(file, router,
                        original.Baseline.LocalObject, caller.User!.Value, [roleOnly], nextRole, false, identity, deadline.Token);
                    RequireObject(replay, nextRoleOnly);
                    await nextRole.RunNormalUserOperationAsync(async () =>
                    {
                        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                        AssertBirthAliasDenied(Path.Combine(directory, $"after-handoff-{start}.link"), file.FullPath, expectedError: 5);
                    });
                    var converted = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, original.Baseline.LocalObject, identity,
                        async (lease, stop) => { RequireObject(await lease.InspectAsync(stop), nextRoleOnly); await lease.ApplyDaclAsync(restored, stop); }, deadline.Token);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, converted.Outcome);
                    Assert.IsTrue(converted.Drained && converted.NativeIdentityPrepared && converted.DurableProjectionCommitted);
                });
                var afterHandoff = await file.InspectAsync(deadline.Token);
                Assert.AreEqual(initial.LocalBinding, afterHandoff.LocalBinding); Assert.IsTrue(afterHandoff.IsPlaceholder);
                CollectionAssert.AreEqual(initial.PlaceholderIdentity.ToArray(), afterHandoff.PlaceholderIdentity.ToArray());
                CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ReplacementValidation: placeholderAndOrdinaryStart=True; latestUnsentBackup=True; publicExclusiveTightening=True; durableOriginalBackup=True; sharedPrivateBackupStore=True; sameOriginalObject=True; openWritePreservesPlaceholder=True; ordinaryStartRecovered=True; roleOnlyContentDenied=True; aliasDenied=True; publicSameObjectReconversion=True; originalIdentityRetained=True; binaryExact=True; dirty=True; sharedOwnerHandoff=True; handoffOrdinaryAndPlaceholder=True; newRoleOnlyClosed=True; handoffReplay=True; wrongBindingRejected=True; normalCallerRejected=True; noSourceAccess=True; productOperation=False; hostIntegrated=False.");

            void RequireObject(MirrorPulseNamespacePermissionObject facts, string dacl)
            {
                Assert.AreEqual(original.Baseline.LocalObject, facts.LocalObject); Assert.AreEqual(caller.User!.Value, facts.OwnerSid);
                Assert.AreEqual("Docs/original.bin", facts.RelativePath); Assert.IsFalse(facts.IsDirectory); Assert.AreEqual(1u, facts.LinkCount);
                Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(dacl, facts.Dacl));
            }
        }
        catch (Exception error) { TestContext.WriteLine("ReplacementValidationFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
