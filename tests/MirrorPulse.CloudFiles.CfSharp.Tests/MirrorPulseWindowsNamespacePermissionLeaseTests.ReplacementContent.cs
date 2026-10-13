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
    [TestCategory("NativeCloudFiles")]
    public async Task NativeReplacementContentWriterRetainsFirstBackupAndDirtyCopyAcrossBothObjectStarts()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            var registry = new CfSharpMirrorPulseCloudRootRegistry();
            var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.replacement-copy"), InstanceId.New(),
                new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
            using WindowsIdentity caller = WindowsIdentity.GetCurrent();
            await using var role = new MirrorPulseNamespaceExecutionSession();
            await using var recoveryRole = new MirrorPulseNamespaceExecutionSession();
            var activeRole = role;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var source = new ProtectedPermissionNoSourceProvider();
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            byte[] oldBytes = attempt == 3 ? [] : [0, 255, 8]; byte[] latestOldBytes = attempt == 1 ? [255, 0, 7, 6, 5, 4] : oldBytes;
            byte[] newBytes = attempt >= 2 ? [] : new byte[2_097_409]; new Random(411).NextBytes(newBytes);
            try
            {
                registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 4, 4]));
                await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                var file = fileSystem.GetFile("Docs/original.bin"); var parent = fileSystem.GetDirectory("Docs");
                await File.WriteAllBytesAsync(file.FullPath, oldBytes, deadline.Token);
                await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                var member = members.Values.Single(value => !value.IsDirectory);
                var originalNative = await file.InspectAsync(deadline.Token);
                Assert.IsTrue(CloudPlaceholderIdentity.TryDecode(originalNative.PlaceholderIdentity.Span, out var identity)); Assert.IsNotNull(identity);
                if (attempt == 1)
                {
                    await File.WriteAllBytesAsync(file.FullPath, latestOldBytes, deadline.Token);
                    Assert.IsFalse((await file.InspectAsync(deadline.Token)).IsPlaceholder);
                }
                string inputPath = Path.Combine(directory, "new-input.bin"); await File.WriteAllBytesAsync(inputPath, newBytes, deadline.Token);
                Guid operationId = Guid.NewGuid(); var inputs = new MirrorPulseLocalImportStagingStore(paths);
                var content = await inputs.StageAsync(operationId, inputPath, deadline.Token); File.Delete(inputPath);
                await using var input = await inputs.OpenLeaseAsync(operationId, content, deadline.Token);
                var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, deadline.Token))!;
                var intent = new MirrorPulseNamespaceReplacementIntent(1, operationId, original,
                    Convert.ToBase64String(identity.Encode()), content, DateTimeOffset.UtcNow);
                await catalog.PrepareNamespaceReplacementAsync(intent, deadline.Token);
                await catalog.RecordNamespaceReplacementStartAsync(new(1, operationId, DateTimeOffset.UtcNow), deadline.Token);
                var action = new MirrorPulseNamespaceReplacementAction(1, operationId, Guid.NewGuid(), member.LocalObject, member.OwnerSid,
                    role.RoleSid.Value, MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role), original.ExpectedDacl, DateTimeOffset.UtcNow);
                await catalog.PrepareNamespaceReplacementActionAsync(action, deadline.Token);
                var backups = new MirrorPulseLocalReplacementBackupStore(paths);
                var writer = new MirrorPulseNamespaceReplacementContentWriter(catalog, backups, router, role, actor)
                {
                    Checkpoint = (point, _) => attempt == 1 && point == MirrorPulseNamespaceReplacementContentCheckpoint.AfterBackupRecorded
                        ? throw new IOException("Old role stopped after retaining the first old-content backup.") : ValueTask.CompletedTask
                };
                await actor.RunAsync(async token =>
                {
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        var tightened = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, member.LocalObject, identity,
                            async (lease, stop) =>
                            {
                                RequireObject(await lease.InspectAsync(stop), action.RestoreDacl);
                                await lease.ApplyDaclAsync(action.RoleOnlyDacl, stop);
                                RequireObject(await lease.InspectAsync(stop), action.RoleOnlyDacl);
                            }, token);
                        Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, tightened.Outcome);
                        Assert.IsTrue(tightened.CallbackCompleted && tightened.Drained && tightened.NativeIdentityPrepared && tightened.DurableProjectionCommitted);
                        if (attempt == 1)
                        {
                            await Assert.ThrowsExactlyAsync<IOException>(() => writer.CopyAsync(operationId, file, parent, input, CancellationToken.None));
                            return;
                        }
                        var receipt = await writer.CopyAsync(operationId, file, parent, input, CancellationToken.None);
                        Assert.AreEqual(content, receipt.Content); Assert.AreEqual(member.LocalObject, receipt.Verification.LocalObject);
                        if (attempt != 3) Assert.IsFalse(receipt.IsInSync);
                        Assert.IsTrue(receipt.IsPlaceholder); Assert.AreEqual(intent.OriginalPlaceholderIdentity, receipt.PlaceholderIdentity);
                    });
                    return true;
                }, deadline.Token);
                MirrorPulseNamespaceReplacementBackup? firstBackup = null;
                if (attempt == 1)
                {
                    var interrupted = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, deadline.Token))!;
                    firstBackup = interrupted.Backup; Assert.IsNotNull(firstBackup); Assert.IsNull(interrupted.WriteStart); Assert.IsNull(interrupted.ContentCompletion);
                    // Stop the real old token before handing the original file to another role.
                    await role.DisposeAsync(); activeRole = recoveryRole;
                    int interruptHandoff = 1;
                    var recovery = new MirrorPulseNamespaceReplacementRoleRecovery(catalog, router, recoveryRole, actor)
                    {
                        Checkpoint = (point, _) => point == MirrorPulseNamespaceReplacementRoleCheckpoint.AfterMetadataApplied &&
                            Interlocked.Exchange(ref interruptHandoff, 0) == 1 ? throw new IOException("Metadata handoff completed before its receipt.") : ValueTask.CompletedTask
                    };
                    writer = new(catalog, backups, router, recoveryRole, actor);
                    await actor.RunAsync(async token =>
                    {
                        await recoveryRole.RunNamespaceOperationAsync(async () =>
                        {
                            await Assert.ThrowsExactlyAsync<IOException>(() => recovery.RecoverAsync(operationId, file, parent, token));
                            Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token))!.RoleHandoff);
                            var handoff = await recovery.RecoverAsync(operationId, file, parent, token);
                            var receipt = await writer.CopyAsync(operationId, file, parent, input, token);
                            Assert.AreEqual(handoff.RecoveryId, receipt.AuthorityRecoveryId); Assert.AreEqual(action.ActionId, receipt.ActionId);
                            Assert.AreEqual(content, receipt.Content); Assert.IsFalse(receipt.IsInSync);
                        });
                        return true;
                    }, deadline.Token);
                }
                var copied = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, deadline.Token))!;
                Assert.IsNotNull(copied.Backup); Assert.IsNotNull(copied.WriteStart); Assert.IsNotNull(copied.ContentCompletion);
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(latestOldBytes)), copied.Backup.Content.Sha256);
                if (firstBackup is not null) Assert.AreEqual(firstBackup, copied.Backup);
                var currentAction = MirrorPulseNamespaceReplacementContentWriter.RequireCurrentAction(copied);
                await using (var backup = await backups.OpenLeaseAsync(operationId, copied.Backup.Content, deadline.Token))
                    Assert.AreEqual(copied.Backup.Content.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(backup.Data, deadline.Token)));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                int interruptAfterScope = attempt == 0 ? 1 : 0;
                var restorer = new MirrorPulseNamespaceReplacementAccessRestorer(catalog, router, activeRole, actor)
                {
                    Checkpoint = (point, _) =>
                    {
                        if (point == MirrorPulseNamespaceReplacementRestorationCheckpoint.AfterScope && Interlocked.Exchange(ref interruptAfterScope, 0) == 1)
                            throw new IOException("Interrupted after the drained public scope and before its product result.");
                        return ValueTask.CompletedTask;
                    }
                };
                await actor.RunAsync(async token =>
                {
                    await activeRole.RunNamespaceOperationAsync(async () =>
                    {
                        if (attempt == 0)
                        {
                            await Assert.ThrowsExactlyAsync<IOException>(() => restorer.RestoreAsync(operationId, file, parent, token));
                            Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token))!.RestorationResult);
                        }
                        var restored = await restorer.RestoreAsync(operationId, file, parent, token);
                        Assert.AreEqual("Completed", restored.NativeOutcome);
                        Assert.IsTrue(restored.CallbackCompleted && restored.Drained && restored.NativeIdentityPrepared && restored.DurableProjectionCommitted);
                        Assert.IsTrue(restored.AccessDescriptorApplied && restored.AccessDescriptorReadBack && restored.ProductVerified);
                        Assert.AreEqual(member.LocalObject, restored.Verification!.LocalObject);
                        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(currentAction.RestoreDacl, restored.Verification.Dacl));
                    });
                    return true;
                }, deadline.Token);
                var restoredExecution = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, deadline.Token))!;
                Assert.AreEqual(copied.Action, restoredExecution.Action); Assert.AreEqual(copied.Backup, restoredExecution.Backup);
                Assert.AreEqual(copied.ContentCompletion, restoredExecution.ContentCompletion);
                CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                var after = await file.InspectAsync(deadline.Token);
                Assert.AreEqual(originalNative.LocalBinding, after.LocalBinding);
                if (attempt != 3) Assert.AreEqual(CloudSynchronizationState.NotInSync, after.SynchronizationState);
                CollectionAssert.AreEqual(originalNative.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
                byte[] laterEdit = [7, 7, 0]; await File.WriteAllBytesAsync(file.FullPath, laterEdit, deadline.Token);
                Assert.AreEqual(copied.ContentCompletion, await actor.RunAsync(async token => await writer.CopyAsync(operationId, file, parent, null, token), deadline.Token));
                Assert.AreEqual(restoredExecution.RestorationResult, await actor.RunAsync(async token => await restorer.RestoreAsync(operationId, file, parent, token), deadline.Token));
                CollectionAssert.AreEqual(laterEdit, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                if (attempt == 0)
                {
                    await role.DisposeAsync();
                    var recovery = new MirrorPulseNamespaceReplacementRoleRecovery(catalog, router, recoveryRole, actor);
                    var nextWriter = new MirrorPulseNamespaceReplacementContentWriter(catalog, backups, router, recoveryRole, actor);
                    var nextRestorer = new MirrorPulseNamespaceReplacementAccessRestorer(catalog, router, recoveryRole, actor);
                    await actor.RunAsync(async token =>
                    {
                        await recoveryRole.RunNamespaceOperationAsync(async () =>
                        {
                            await recovery.RecoverAsync(operationId, file, parent, token);
                            Assert.AreEqual(copied.ContentCompletion, await nextWriter.CopyAsync(operationId, file, parent, null, token));
                            var current = await nextRestorer.RestoreAsync(operationId, file, parent, token);
                            Assert.IsTrue(current.ProductVerified && current.Drained);
                            Assert.AreNotEqual(restoredExecution.Restoration!.ActionId,
                                (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token))!.Restoration!.ActionId);
                        });
                        return true;
                    }, deadline.Token);
                    CollectionAssert.AreEqual(laterEdit, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                    CollectionAssert.AreEqual(originalNative.PlaceholderIdentity.ToArray(), (await file.InspectAsync(deadline.Token)).PlaceholderIdentity.ToArray());
                    var recovered = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, deadline.Token))!;
                    Assert.AreEqual(copied.Backup, recovered.Backup); Assert.AreEqual(copied.ContentCompletion, recovered.ContentCompletion);
                    Assert.AreEqual(restoredExecution.RestorationResult, await catalog.RecordNamespaceReplacementRestorationResultAsync(restoredExecution.RestorationResult!, deadline.Token));
                }
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, deadline.Token));
                CollectionAssert.AreEqual(new[] { root.RootId }, (await catalog.ReadNamespaceRecoveryRootsAsync(deadline.Token)).ToArray());
                Assert.IsNull(await catalog.ReadMutationAsync(operationId, deadline.Token)); Assert.AreEqual(0, source.Reads);
                TestContext.WriteLine($"ReplacementContent: ordinaryStart={attempt == 1}; emptyInput={attempt >= 2}; unchangedEmpty={attempt == 3}; admittedOriginal=True; firstBackup=True; durableWriteStart=True; actualBinary=True; sameBinding=True; publicIdentity=True; changedContentDirty=True; replayPreservesLaterEdit=True; rootStillRequiresCompletion=True; noSourceAccess=True; productionCopy=True; productionAccessRestoration=True; interruptedAfterScope={attempt == 0}; sameRoleRecovery={attempt != 1}; newRoleCopyRecovery={attempt == 1}; originalBackupRetained=True; metadataHandoffInterruption={attempt == 1}; verifiedOldScopeNewRolePreservesLaterEdit={attempt == 0}; productionExecutor=False; hostIntegrated=False.");

                void RequireObject(MirrorPulseNamespacePermissionObject facts, string dacl)
                {
                    Assert.AreEqual(member.LocalObject, facts.LocalObject); Assert.AreEqual(member.OwnerSid, facts.OwnerSid); Assert.AreEqual(1u, facts.LinkCount);
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(dacl, facts.Dacl));
                }
            }
            catch (Exception error) { TestContext.WriteLine("ReplacementContentFailure: " + error); throw; }
            finally
            {
                registry.Unregister(paths.SyncRootPath);
                RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
                RestoreImportContentFixtureForCleanup(paths.SyncRootPath, caller.User!);
                DeleteFixture(directory);
            }
        }
    }
}
