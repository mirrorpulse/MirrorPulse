using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static async Task<MirrorPulseNamespacePermissionIntent> PreparePendingOriginalCancellationFixtureAsync(
        CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceActor actor,
        MirrorPulseNamespacePermissionCoordinator permissions, RootRegistration root, Guid initialization,
        MirrorPulseNamespacePermissionPhase phase, CancellationToken token)
    {
        Assert.IsTrue(phase is MirrorPulseNamespacePermissionPhase.Prepared or MirrorPulseNamespacePermissionPhase.Applied);
        // This real third role is disposed before the public Host cancellation. Its
        // original intent must settle without replacing it with the current Host role.
        await using var pendingRole = new MirrorPulseNamespaceExecutionSession();
        MirrorPulseNamespacePermissionIntent intent = null!;
        await pendingRole.RunNamespaceOperationAsync(async () =>
        {
            var roots = await MirrorPulseNamespaceCurrentRootSelector.SelectAsync(fileSystem, catalog, [root], token);
            await catalog.PrepareNamespaceInitializationAsync(new(4, initialization, roots, DateTimeOffset.UtcNow)
            { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory }, token);
            await using var anchor = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, pendingRole, actor, permissions).ProtectAsync(token);
            var nativeRoot = await fileSystem.GetDirectory(root.DirectoryName).InspectAsync(token);
            var nativeBinding = nativeRoot.LocalBinding!;
            var member = (await catalog.ReadNamespaceMemberByBindingAsync(new(nativeBinding.VolumeSerialNumber, nativeBinding.SyncRootFileId, nativeBinding.LocalFileId), token))!;
            var context = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, member.MemberId, token))[0];
            intent = context.Permission!.Intent with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                RelativePath = context.Location.RelativePath,
                RoleSid = pendingRole.RoleSid.Value,
                ExpectedDacl = context.Permission.Verification!.Dacl,
                TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(pendingRole, true),
                PreparedAt = DateTimeOffset.UtcNow,
                Relocation = context.Location.CurrentPositionOperationId is { } position ? new(member.MemberId, position) : null,
                ReplacementProjection = null
            };
            var prepared = await catalog.PrepareNamespacePermissionChangeAsync(context.Original!, intent, token);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, prepared.Phase);
            if (phase == MirrorPulseNamespacePermissionPhase.Applied)
            {
                var receipt = await actor.RunAsync(async stop => await permissions.RunAdmittedOriginalMemberRecoveryAsync(initialization,
                    member.MemberId, (current, admitted) => MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(
                        fileSystem.GetDirectory(root.DirectoryName), router, member.LocalObject, async (lease, scopeToken) =>
                        {
                            Assert.AreEqual(context.Member, current.Member); Assert.AreEqual(context.Location, current.Location);
                            var before = await lease.InspectAsync(scopeToken);
                            Assert.AreEqual(member.LocalObject, before.LocalObject); Assert.AreEqual(member.OwnerSid, before.OwnerSid);
                            Assert.AreEqual(intent.ExpectedDacl, before.Dacl);
                            await lease.ApplyDaclAsync(intent.TargetDacl, scopeToken);
                            var applied = await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, member.LocalObject,
                                DateTimeOffset.UtcNow, CancellationToken.None);
                            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, applied.Phase);
                            // Deliberately stop before the independent product verification;
                            // the public same-object SDK scope still completes and drains.
                        }, admitted), stop), token);
                Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome); Assert.IsTrue(receipt.Drained);
            }
            var retained = (await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId, token))!;
            Assert.AreEqual(phase, retained.Phase); Assert.AreEqual(intent, retained.Intent); Assert.IsNull(retained.Verification);
            Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization, token)).Completion);
        });
        return intent;
    }
}
