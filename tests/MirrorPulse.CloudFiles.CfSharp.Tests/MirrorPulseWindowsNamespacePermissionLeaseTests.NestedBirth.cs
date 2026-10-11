using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private sealed record NativeNestedBirth(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan,
        MirrorPulseNamespaceBirthStart Start, MirrorPulseNamespaceBirthConversionPreparation Conversion,
        MirrorPulseNamespaceBirthObservation Observation, MirrorPulseNamespaceBirthProtection Protection);

    private static async Task<IReadOnlyList<NativeNestedBirth>> CreateNativeNestedBirthsAsync(CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession role, InstanceId instanceId, MirrorPulseNamespaceBirthIntent firstParent,
        MirrorPulseNamespaceBirthObservation firstParentObservation, MirrorPulseNamespaceBirthProtection firstParentProtection,
        byte[] payload, CancellationToken token)
    {
        var result = new List<NativeNestedBirth>();
        await role.RunNamespaceOperationAsync(async () =>
        {
            var parentBirth = firstParent;
            var parentObservation = firstParentObservation;
            var parentProtection = firstParentProtection;
            foreach ((string name, bool isDirectory) in new[] { ("nested", true), ("deeper", true), ("payload.bin", false) })
            {
                CloudDirectory parent = fileSystem.GetDirectory(parentBirth.RelativePath);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token);
                var parentFacts = await parentGuard.InspectAsync(token);
                Assert.AreEqual(parentObservation.LocalObject, parentFacts.LocalObject);
                Assert.AreEqual(parentProtection.Verification.Dacl, parentFacts.Dacl);
                var birth = new MirrorPulseNamespaceBirthIntent(2, Guid.NewGuid(), firstParent.RootId, Guid.Empty, Guid.Empty,
                    parentObservation.LocalObject, parentBirth.RelativePath + "/" + name, isDirectory,
                    MirrorPulseNamespaceBirthOrigin.ControlledCreation, role.RoleSid.Value, DateTimeOffset.UtcNow)
                { BornParent = new(parentBirth.OperationId, parentProtection.ProtectionId) };
                CloudPlaceholderIdentity identity = router.CreateFileIdentity(instanceId, "docs", "local:" + birth.OperationId.ToString("N"));
                var plan = new MirrorPulseNamespaceBirthPlan(1, birth.OperationId, identity.ItemId, identity.RemoteId, null, DateTimeOffset.UtcNow);
                CloudItem child = isDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
                await catalog.PrepareNamespaceBirthAsync(birth, token);
                await catalog.PrepareNamespaceBirthPlanAsync(plan, token);
                Assert.IsTrue(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, birth.OperationId, child, token));
                var start = new MirrorPulseNamespaceBirthStart(1, birth.OperationId, DateTimeOffset.UtcNow);
                Assert.IsTrue((await catalog.RecordNamespaceBirthStartAsync(start, token)).NewlyRecorded);
                if (isDirectory) Directory.CreateDirectory(child.FullPath);
                else
                {
                    var security = new FileSecurity();
                    security.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role), AccessControlSections.Access);
                    await using FileStream writer = new FileInfo(child.FullPath).Create(FileMode.CreateNew,
                        FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
                    await writer.WriteAsync(payload, token);
                    writer.Flush(flushToDisk: true);
                }
                string expectedDacl = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parentFacts.Dacl, isDirectory);
                var conversion = await MirrorPulseNamespaceBirthConversionPreparer.RecordAsync(catalog,
                    birth.OperationId, child, router, expectedDacl, token);
                if (isDirectory)
                    await child.ConvertToPlaceholderAsync(identity, CloudPlaceholderConversionOptions.CreateBuilder()
                        .WithPopulationState(CloudDirectoryPopulationState.Complete).Build(), token);
                else
                {
                    var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, conversion.LocalObject, identity,
                        async (lease, stop) =>
                        {
                            await child.ConvertToPlaceholderAsync(identity, cancellationToken: stop);
                            await lease.ApplyDaclAsync(expectedDacl, stop);
                            Assert.AreEqual(conversion.LocalObject, (await lease.InspectAsync(stop)).LocalObject);
                        }, token);
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome, receipt.Error?.ToString());
                    Assert.IsTrue(receipt.CallbackStarted && receipt.CallbackCompleted && receipt.Drained);
                }
                var observation = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, child, router, token);
                Assert.AreEqual(conversion.LocalObject, observation.LocalObject);
                Assert.AreEqual(plan.ItemId, observation.ItemId);
                Assert.IsFalse(observation.IsInSync);
                await Assert.ThrowsAsync<InvalidDataException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(
                    catalog, Guid.NewGuid(), birth.OperationId, child, fileSystem.GetDirectory("Docs"), router, token));
                var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, Guid.NewGuid(), birth.OperationId, child, parent, router, token);
                Assert.AreEqual(2, protection.Version);
                Assert.AreEqual(birth.BornParent, protection.BornParent);
                Assert.AreEqual(Guid.Empty, protection.ParentPermissionOperationId);
                Assert.AreEqual(observation.LocalObject, protection.Verification.LocalObject);
                Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expectedDacl, protection.Verification.Dacl));
                result.Add(new(birth, plan, start, conversion, observation, protection));
                parentBirth = birth; parentObservation = observation; parentProtection = protection;
            }
        });
        Assert.HasCount(3, result);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(fileSystem.GetFile(result[^1].Birth.RelativePath).FullPath, token));
        return result;
    }

    private static async Task VerifyNativeNestedBirthsAsync(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        IReadOnlyList<NativeNestedBirth> births, byte[] payload, CancellationToken token)
    {
        Assert.HasCount(3, births);
        foreach (var original in births)
        {
            CloudItem child = original.Birth.IsDirectory ? fileSystem.GetDirectory(original.Birth.RelativePath) : fileSystem.GetFile(original.Birth.RelativePath);
            CloudDirectory parent = fileSystem.GetDirectory(original.Birth.RelativePath[..original.Birth.RelativePath.LastIndexOf('/')]);
            Assert.AreEqual(original.Birth, await catalog.ReadNamespaceBirthAsync(original.Birth.OperationId, token));
            Assert.AreEqual(original.Conversion, await catalog.ReadNamespaceBirthConversionAsync(original.Birth.OperationId, token));
            Assert.AreEqual(original.Observation, await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, original.Birth.OperationId, child, router, token));
            Assert.IsFalse((await catalog.RecordNamespaceBirthStartAsync(original.Start, token)).NewlyRecorded);
            Assert.IsFalse(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, original.Birth.OperationId, child, token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(
                catalog, original.Protection.ProtectionId, original.Birth.OperationId, child, parent, router, token));
            await role.RunNamespaceOperationAsync(async () => Assert.AreEqual(original.Protection,
                await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, original.Protection.ProtectionId, original.Birth.OperationId, child, parent, router, token)));
            Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(original.Birth.OperationId, token));
        }
        string leaf = fileSystem.GetFile(births[^1].Birth.RelativePath).FullPath;
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(leaf, token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(leaf), token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(leaf, leaf + ".renamed"), token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(leaf)!, "ordinary.txt"), "not admitted", token));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(leaf, token));
        var scan = await catalog.BeginNamespaceBirthRecoveryScanAsync(token);
        var recovered = (await catalog.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 64, token)).Entries;
        foreach (var original in births)
        {
            var entry = recovered.Single(entry => entry.Intent.OperationId == original.Birth.OperationId);
            Assert.IsTrue(entry.OwnsNameReservation);
            Assert.AreEqual(original.Protection, entry.LatestProtection);
        }
    }

    private async Task<Guid[]> ReadNativeNestedBirthJournalAsync(CloudLocalChangeFeed feed, IReadOnlyList<NativeNestedBirth> births, CancellationToken token)
    {
        Guid leafId = births[^1].Plan.ItemId;
        var operations = new HashSet<Guid>();
        foreach (var directory in births.Where(birth => birth.Birth.IsDirectory))
        {
            var page = await WaitForBirthJournalAsync(feed, leafId, directory.Plan.ItemId, token);
            foreach (var change in page) operations.Add(change.OperationId);
        }
        return operations.ToArray();
    }
}
