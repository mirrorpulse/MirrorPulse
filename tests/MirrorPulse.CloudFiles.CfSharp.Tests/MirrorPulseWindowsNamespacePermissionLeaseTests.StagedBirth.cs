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
        byte[] payload, CancellationToken token)
    {
        NativeStagedBirth? staged = null;
        await role.RunNamespaceOperationAsync(async () =>
        {
            CloudDirectory directory = fileSystem.GetDirectory("Docs");
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(directory, router, token);
            Assert.AreEqual(parent.Baseline.LocalObject, (await parentGuard.InspectAsync(token)).LocalObject);
            var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), parent.Baseline.RootId!.Value,
                parent.Baseline.EvidenceId, parent.Intent.OperationId, parent.Baseline.LocalObject,
                "Docs/staged.bin", false, MirrorPulseNamespaceBirthOrigin.ControlledCreation, role.RoleSid.Value, DateTimeOffset.UtcNow);
            CloudPlaceholderIdentity identity = router.CreateFileIdentity(instanceId, "docs", "local:" + birth.OperationId.ToString("N"));
            var plan = new MirrorPulseNamespaceBirthPlan(1, birth.OperationId, identity.ItemId, identity.RemoteId, null, DateTimeOffset.UtcNow);
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
        var identity = new CloudPlaceholderIdentity(original.Plan.ItemId, original.Plan.RemoteId);
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
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token);
            var parentFacts = await parentGuard.InspectAsync(token);
            var current = await catalog.ReadLatestNamespacePermissionChangeAsync(original.Birth.ParentEvidenceId, token);
            Assert.IsNotNull(current?.Verification);
            Assert.AreEqual(role.RoleSid.Value, current.Intent.RoleSid);
            Assert.AreEqual(original.Birth.ParentLocalObject, parentFacts.LocalObject);
            Assert.AreEqual(current.Verification.Dacl, parentFacts.Dacl);
            string convertedDacl = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parentFacts.Dacl, false);
            Assert.AreNotEqual(original.Conversion.ExpectedConvertedDacl, convertedDacl);
            // Only this disposable probe performs the metadata handoff directly. Production
            // needs an immutable recovery action before the write, plus shared actor admission.
            MirrorPulseNamespaceHandleTests.ApplyOrdinaryBirthRoleHandoffForProbe(child.FullPath,
                original.Conversion, MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(() => File.ReadAllBytesAsync(child.FullPath, token)));
            await role.RunNormalUserOperationAsync(() =>
            {
                AssertBirthAliasDenied(Path.Combine(Path.GetDirectoryName(fileSystem.Root.FullPath)!, "handoff-before-conversion.link"), child.FullPath, 5);
                return Task.CompletedTask;
            });
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(child.FullPath, token));
            var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, original.Conversion.LocalObject, identity,
                async (lease, stop) =>
                {
                    var snapshot = await child.InspectAsync(stop);
                    Assert.IsTrue(snapshot.IsPlaceholder);
                    Assert.AreEqual(original.Plan.ItemId, snapshot.ItemId);
                    Assert.AreEqual(original.Plan.RemoteId, snapshot.RemoteId);
                    AssertBirthAliasDenied(Path.Combine(Path.GetDirectoryName(fileSystem.Root.FullPath)!, "recovered-scope.link"), child.FullPath);
                    await lease.ApplyDaclAsync(convertedDacl, stop);
                    Assert.AreEqual(original.Conversion.LocalObject, (await lease.InspectAsync(stop)).LocalObject);
                }, token);
            Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome, receipt.Error?.ToString());
            Assert.IsTrue(receipt.CallbackStarted && receipt.CallbackCompleted && receipt.Drained &&
                receipt.NativeIdentityPrepared && receipt.DurableProjectionCommitted && receipt.AccessDescriptorApplied && receipt.AccessDescriptorReadBack);
        });
        // Leave the first placeholder observation absent across the next official-store reopen.
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(original.Birth.OperationId, token));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(child.FullPath, token));
        Assert.AreEqual(original.Conversion, await catalog.ReadNamespaceBirthConversionAsync(original.Birth.OperationId, token));
        await File.AppendAllTextAsync(child.FullPath, " latest", token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(child.FullPath), token));
        TestContext.WriteLine("AbandonedBirthConversion: oldRoleDisposed=True; originalOrdinaryBindingAndDaclRetained=True; ownerMetadataHandoff=True; normalDataDeniedUntilPlaceholder=True; publicSameObjectConversion=True; inheritedCurrentRole=True; originalPreparationUnchanged=True; firstObservationDeferred=True; productRecoveryIntegrated=False.");
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
        });
        Assert.AreEqual(System.Text.Encoding.UTF8.GetString(payload) + " latest", await File.ReadAllTextAsync(child.FullPath, token));
    }
}
