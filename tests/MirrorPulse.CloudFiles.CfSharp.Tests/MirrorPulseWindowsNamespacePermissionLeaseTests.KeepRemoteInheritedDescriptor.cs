using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteCurrentRoleRestoresItsRegisteredInheritedDescriptor() =>
        RunKeepRemoteResidentFixtureAsync("registered-restore", 65537);

    private static async Task PrepareInheritedKeepRemoteOriginalsAsync(CloudFileSystem fileSystem,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        System.Security.Principal.SecurityIdentifier owner, List<MirrorPulseNamespacePermissionPreparation> originals,
        CancellationToken token)
    {
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        // Protect each parent before capturing its child. The retained file descriptor then
        // models the inherited registered graph produced by controlled product births.
        foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetFile("Docs/original.bin") })
        {
            SetProtectedFixtureUserOwner(item, owner);
            if (item.Kind == CloudItemKind.File)
            {
                // Establish a distinct synthetic original before retaining any baseline.
                // Its recorded protection then applies the actual inherited descriptor.
                await role.RunNamespaceOperationAsync(() =>
                {
                    var setup = new FileSecurity();
                    setup.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, false), AccessControlSections.Access);
                    new FileInfo(item.FullPath).SetAccessControl(setup);
                    return Task.CompletedTask;
                });
            }
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token);
            var observed = await metadata.InspectAsync(token);
            var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), observed.RootId, observed.LocalObject,
                observed.RelativePath, observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
            string target = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, observed.IsDirectory);
            if (!observed.IsDirectory)
            {
                await using var parent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(fileSystem.GetDirectory("Docs"), router, token);
                target = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl((await parent.InspectAsync(token)).Dacl, false);
                Assert.AreNotEqual(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, false), target);
            }
            var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject,
                baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value,
                baseline.OriginalDacl, target, DateTimeOffset.UtcNow);
            var preparation = new MirrorPulseNamespacePermissionPreparation(baseline, intent);
            await catalog.PrepareNamespacePermissionChangesAsync([preparation], token);
            await role.RunNamespaceOperationAsync(async () =>
            {
                var identity = observed.IsDirectory ? null : router.CreateFileIdentity(router.Registrations.Single().InstanceId, "docs", "local:original");
                var applied = await coordinator.ApplyProtectedAsync(intent.OperationId, item, router, identity, token);
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
            });
            originals.Add(preparation);
        }
    }
}
