using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private sealed record NativeStagedBirth(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan,
        MirrorPulseNamespaceBirthStart Start, MirrorPulseNamespaceBirthConversionPreparation Conversion);

    private static async Task<NativeStagedBirth> StageNativeAbandonedBirthAsync(CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession role, InstanceId instanceId, MirrorPulseNamespacePermissionPreparation parent,
        byte[] payload, CancellationToken token, MirrorPulseNamespaceImportContent? importContent = null, Guid? operationId = null)
    {
        NativeStagedBirth? staged = null;
        await role.RunNamespaceOperationAsync(async () =>
        {
            CloudDirectory directory = fileSystem.GetDirectory("Docs");
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(directory, router, token);
            Assert.AreEqual(parent.Baseline.LocalObject, (await parentGuard.InspectAsync(token)).LocalObject);
            var birth = new MirrorPulseNamespaceBirthIntent(1, operationId ?? Guid.NewGuid(), parent.Baseline.RootId!.Value,
                parent.Baseline.EvidenceId, parent.Intent.OperationId, parent.Baseline.LocalObject,
                "Docs/staged.bin", false, importContent is null ? MirrorPulseNamespaceBirthOrigin.ControlledCreation : MirrorPulseNamespaceBirthOrigin.Import,
                role.RoleSid.Value, DateTimeOffset.UtcNow);
            CloudPlaceholderIdentity identity = router.CreateFileIdentity(instanceId, "docs", "local:" + birth.OperationId.ToString("N"));
            var plan = new MirrorPulseNamespaceBirthPlan(importContent is null ? 1 : 2, birth.OperationId, identity.ItemId, identity.RemoteId,
                null, DateTimeOffset.UtcNow)
            { Content = importContent };
            CloudFile child = fileSystem.GetFile(birth.RelativePath);
            await catalog.PrepareNamespaceBirthAsync(birth, token);
            await catalog.PrepareNamespaceBirthPlanAsync(plan, token);
            Assert.IsTrue(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, birth.OperationId, child, token));
            var start = new MirrorPulseNamespaceBirthStart(1, birth.OperationId, DateTimeOffset.UtcNow);
            Assert.IsTrue((await catalog.RecordNamespaceBirthStartAsync(start, token)).NewlyRecorded);
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role), AccessControlSections.Access);
            await using (FileStream writer = new FileInfo(child.FullPath).Create(FileMode.CreateNew,
                FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
            {
                await writer.WriteAsync(payload, token);
                writer.Flush(flushToDisk: true);
            }
            var preparation = await MirrorPulseNamespaceBirthConversionPreparer.RecordAsync(catalog, birth.OperationId, child, router,
                MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent.Intent.TargetDacl, false), token);
            Assert.IsFalse((await child.InspectAsync(token)).IsPlaceholder);
            Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token));
            staged = new(birth, plan, start, preparation);
        });
        Assert.IsNotNull(staged);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(fileSystem.GetFile(staged.Birth.RelativePath).FullPath, token));
        return staged;
    }

    private async Task RecoverNativeAbandonedBirthAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, NativeStagedBirth original, byte[] payload, CancellationToken token)
    {
        CloudFile child = fileSystem.GetFile(original.Birth.RelativePath);
        CloudDirectory parent = fileSystem.GetDirectory("Docs");
        Assert.AreNotEqual(original.Birth.RoleSid, role.RoleSid.Value);
        Assert.AreEqual(original.Conversion, await catalog.ReadNamespaceBirthConversionAsync(original.Birth.OperationId, token));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(original.Birth.OperationId, token));
        Assert.IsFalse((await child.InspectAsync(token)).IsPlaceholder);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(child.FullPath, token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNamespaceOperationAsync(async () =>
            _ = await File.ReadAllBytesAsync(child.FullPath, token)));
        await using (var ordinary = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(child, router, token))
        {
            var before = await ordinary.InspectAsync(token);
            Assert.AreEqual(original.Conversion.LocalObject, before.LocalObject);
            Assert.AreEqual(original.Conversion.BirthDacl, before.Dacl);
        }
        AssertBirthAliasDenied(Path.Combine(Path.GetDirectoryName(fileSystem.Root.FullPath)!, "abandoned-birth.link"), child.FullPath, 5);
        await role.RunNamespaceOperationAsync(async () =>
        {
            var coordinator = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role);
            var first = await coordinator.PrepareStagedFileAsync(Guid.NewGuid(), original.Birth.OperationId, child, parent, token);
            Assert.AreEqual(original.Conversion.LocalObject, first.LocalObject);
            string expectedBirthDacl = original.Conversion.BirthDacl;
            Assert.AreEqual(expectedBirthDacl, first.ExpectedDacl);
            Assert.AreNotEqual(original.Conversion.ExpectedConvertedDacl, first.ConvertedDacl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(
                () => coordinator.ExecuteStagedFileAsync(first.RecoveryId, child, parent, token)));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.ExecuteStagedFileAsync(first.RecoveryId, child, parent, canceled.Token));
            }
            await using (var unchanged = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(child, router, token))
                Assert.AreEqual(original.Conversion.BirthDacl, (await unchanged.InspectAsync(token)).Dacl);
            var handoffCut = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role, async (checkpoint, stop) =>
            {
                if (checkpoint != MirrorPulseNamespaceBirthRecoveryCheckpoint.AfterRoleHandoff) return;
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(() => File.ReadAllBytesAsync(child.FullPath, stop)));
                await role.RunNormalUserOperationAsync(() =>
                {
                    AssertBirthAliasDenied(Path.Combine(Path.GetDirectoryName(fileSystem.Root.FullPath)!, "handoff-before-conversion.link"), child.FullPath, 5);
                    return Task.CompletedTask;
                });
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(child.FullPath, stop));
                throw new IOException("Simulated interruption after the role-only handoff.");
            });
            await Assert.ThrowsExactlyAsync<IOException>(() => handoffCut.ExecuteStagedFileAsync(first.RecoveryId, child, parent, token));
            Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(first.RecoveryId, token));
            Assert.IsFalse((await child.InspectAsync(token)).IsPlaceholder);
            var second = await coordinator.PrepareStagedFileAsync(Guid.NewGuid(), original.Birth.OperationId, child, parent, token);
            Assert.AreEqual(first.RecoveryId, second.PreviousRecoveryId);
            Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(first.RoleOnlyDacl, second.ExpectedDacl));
            var callbackCut = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role, (checkpoint, _) =>
            {
                if (checkpoint == MirrorPulseNamespaceBirthRecoveryCheckpoint.BeforeAccessDescriptor)
                {
                    AssertBirthAliasDenied(Path.Combine(Path.GetDirectoryName(fileSystem.Root.FullPath)!, "recovered-scope.link"), child.FullPath);
                    throw new IOException("Simulated interruption after conversion before inherited ACL application.");
                }
                return ValueTask.CompletedTask;
            });
            var pending = await callbackCut.ExecuteStagedFileAsync(second.RecoveryId, child, parent, token);
            Assert.AreEqual(CloudProtectedLocalOperationOutcome.CallbackFailed, pending.Receipt.Outcome);
            Assert.IsTrue(pending.Receipt.NativeConverted && pending.Receipt.NativeIdentityPrepared && pending.Receipt.DurableProjectionCommitted && pending.Receipt.Drained);
            Assert.IsNull(pending.Verification);
            Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(second.RecoveryId, token));
            Assert.IsTrue((await child.InspectAsync(token)).IsPlaceholder);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(() => File.ReadAllBytesAsync(child.FullPath, token)));
            var recordCut = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, role, (checkpoint, _) =>
            {
                if (checkpoint == MirrorPulseNamespaceBirthRecoveryCheckpoint.AfterNativeOperation)
                    throw new IOException("Simulated interruption after native success before verification persistence.");
                return ValueTask.CompletedTask;
            });
            await Assert.ThrowsExactlyAsync<IOException>(() => recordCut.ExecuteStagedFileAsync(second.RecoveryId, child, parent, token));
            Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(second.RecoveryId, token));
            var third = await coordinator.PrepareStagedFileAsync(Guid.NewGuid(), original.Birth.OperationId, child, parent, token);
            Assert.AreEqual(second.RecoveryId, third.PreviousRecoveryId);
            Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(second.ConvertedDacl, third.ExpectedDacl));
            var completed = await coordinator.ExecuteStagedFileAsync(third.RecoveryId, child, parent, token);
            Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, completed.Receipt.Outcome, completed.Receipt.Error?.ToString());
            Assert.IsFalse(completed.Receipt.NativeConverted || completed.Receipt.AccessDescriptorApplied);
            Assert.IsNotNull(completed.Verification);
            Assert.AreEqual(original.Conversion.LocalObject, completed.Verification.Verification.LocalObject);
            Assert.AreEqual(original.Plan.ItemId, completed.Verification.ItemId);
            Assert.AreEqual(third, await coordinator.PrepareStagedFileAsync(third.RecoveryId, original.Birth.OperationId, child, parent, token));
            var replay = await coordinator.ExecuteStagedFileAsync(third.RecoveryId, child, parent, token);
            Assert.AreEqual(completed.Verification, replay.Verification);
            Assert.IsFalse(replay.Receipt.NativeConverted || replay.Receipt.AccessDescriptorApplied);
        });
        // Leave the first placeholder observation absent across the next official-store reopen.
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(original.Birth.OperationId, token));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(child.FullPath, token));
        Assert.AreEqual(original.Conversion, await catalog.ReadNamespaceBirthConversionAsync(original.Birth.OperationId, token));
        await File.AppendAllTextAsync(child.FullPath, " latest", token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(child.FullPath), token));
        TestContext.WriteLine("AbandonedBirthConversion: oldRoleDisposed=True; originalOrdinaryBindingAndDaclRetained=True; ownerMetadataHandoff=True; normalDataDeniedUntilPlaceholder=True; publicSameObjectConversion=True; inheritedCurrentRole=True; originalPreparationUnchanged=True; firstObservationDeferred=True; canceledBeforeWrite=True; handoffInterruptionRecovered=True; nativeCallbackFailureRecovered=True; nativeSuccessUnrecordedRecovered=True; verificationDurable=True; repeatedRecoveryWrites=0; productionCoordinator=True; hostIntegrated=False.");
    }

    private static async Task VerifyRecoveredNativeBirthAsync(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        NativeStagedBirth original, byte[] payload, CancellationToken token)
    {
        CloudFile child = fileSystem.GetFile(original.Birth.RelativePath);
        Assert.AreEqual(original.Birth, await catalog.ReadNamespaceBirthAsync(original.Birth.OperationId, token));
        Assert.AreEqual(original.Conversion, await catalog.ReadNamespaceBirthConversionAsync(original.Birth.OperationId, token));
        Assert.IsFalse((await catalog.RecordNamespaceBirthStartAsync(original.Start, token)).NewlyRecorded);
        Assert.IsFalse(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, original.Birth.OperationId, child, token));
        await role.RunNamespaceOperationAsync(async () =>
        {
            var action = await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(original.Birth.OperationId, token);
            Assert.IsNotNull(action);
            var verification = await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId, token);
            Assert.IsNotNull(verification);
            Assert.AreEqual(original.Conversion.LocalObject, verification.Verification.LocalObject);
            Assert.AreEqual(original.Plan.ItemId, verification.ItemId);
            var observed = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, original.Birth.OperationId, child, router, token);
            Assert.AreEqual(original.Conversion.LocalObject, observed.LocalObject);
            Assert.AreEqual(original.Plan.ItemId, observed.ItemId);
            Assert.IsFalse(observed.IsInSync);
            Assert.AreNotEqual(original.Conversion.ExpectedConvertedDacl, observed.BirthDacl);
            var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, Guid.NewGuid(), original.Birth.OperationId,
                child, fileSystem.GetDirectory("Docs"), router, token);
            Assert.AreEqual(role.RoleSid.Value, protection.RoleSid);
            Assert.AreEqual(observed.LocalObject, protection.Verification.LocalObject);
            Assert.AreEqual(observed.BirthDacl, protection.Verification.Dacl);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId, token));
            Assert.AreEqual(verification, await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId, token));
        });
        Assert.AreEqual(System.Text.Encoding.UTF8.GetString(payload) + " latest", await File.ReadAllTextAsync(child.FullPath, token));
    }
}
