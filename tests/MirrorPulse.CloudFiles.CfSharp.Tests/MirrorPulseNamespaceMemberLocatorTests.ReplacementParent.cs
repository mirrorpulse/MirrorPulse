using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementResolvesCompletedCrossParentMoveWithoutRewritingFirstMemberAcrossOwners(bool born)
    {
        await using var fixture = await Fixture.OpenAsync(born);
        var originalParent = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Parent.MemberId))!;
        var original = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None);
        var binding = fixture.Parent.LocalObject with { LocalFileId = Guid.NewGuid() };
        var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), fixture.Parent.RootId, binding, "Docs/archive", true,
            originalParent.Context.Verification.OwnerSid, new RawSecurityDescriptor("D:P(A;;FR;;;SY)").GetSddlForm(AccessControlSections.Access), DateTimeOffset.UtcNow);
        var protection = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, binding, baseline.RootId, baseline.RelativePath,
            MirrorPulseNamespacePermissionChangeKind.Protect, original.RoleSid, baseline.OriginalDacl, originalParent.ExpectedDacl, DateTimeOffset.UtcNow);
        await fixture.Catalog.PrepareNamespacePermissionChangeAsync(baseline, protection);
        await fixture.Catalog.RecordNamespacePermissionApplicationAsync(protection.OperationId, binding, DateTimeOffset.UtcNow);
        await fixture.Catalog.VerifyNamespacePermissionChangeAsync(protection.OperationId, new(binding, baseline.OwnerSid, protection.TargetDacl, DateTimeOffset.UtcNow));
        var targetParent = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), fixture.Parent.MemberId,
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal, baseline.EvidenceId, protection.OperationId, baseline.RootId, baseline.RelativePath,
            binding, true, baseline.OwnerSid, DateTimeOffset.UtcNow);
        await fixture.Catalog.RegisterNamespaceMemberAsync(targetParent);
        var targetProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, targetParent, CancellationToken.None);
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), fixture.Member.MemberId, original.ProtectionId,
            fixture.Member.RootId!.Value, fixture.Member.LocalObject, fixture.ItemId, "local:original", original.OwnerSid, original.RoleSid,
            new(fixture.Parent.MemberId, fixture.Parent.LocalObject, originalParent.Context.Protection.ProtectionId, fixture.Member.RelativePath),
            new(targetParent.MemberId, targetParent.LocalObject, targetProof.ProtectionId, "Docs/archive/moved"), DateTimeOffset.UtcNow);
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(move);
        await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(new(1, move.OperationId, DateTimeOffset.UtcNow));
        await fixture.PutAsync(fixture.Row(move.Target.RelativePath));
        var observed = original.Verification with { ObservedAt = DateTimeOffset.UtcNow };
        var observedParent = targetProof.Verification with { ObservedAt = DateTimeOffset.UtcNow };
        await fixture.Catalog.RecordNamespaceMemberMoveObservationAsync(new(1, move.OperationId, move.ItemId, move.RemoteId,
            null, move.Target.RelativePath, false, true, 1, observed, observedParent));
        await fixture.Catalog.RecordNamespaceMemberMoveProjectionAsync(new(1, move.OperationId, move.ItemId, move.RemoteId,
            null, move.Target.RelativePath, DateTimeOffset.UtcNow));
        var confirmedChild = observed with { ObservedAt = DateTimeOffset.UtcNow };
        var confirmedParent = observedParent with { ObservedAt = DateTimeOffset.UtcNow };
        await fixture.Catalog.CompleteNamespaceMemberMoveAsync(new(1, move.OperationId, move.ItemId, move.RemoteId,
            move.Target.RelativePath, false, DateTimeOffset.UtcNow, confirmedChild, confirmedParent, null));
        await fixture.RestartAsync();
        var moved = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Member.MemberId))!;
        var intent = new MirrorPulseNamespaceReplacementIntent(1, Guid.NewGuid(), moved,
            Convert.ToBase64String(new CloudPlaceholderIdentity(fixture.ItemId, "local:original", "revision-1").Encode()),
            new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))), DateTimeOffset.UtcNow);
        await fixture.Catalog.PrepareNamespaceReplacementAsync(intent);
        var actualParent = await MirrorPulseNamespaceReplacementContentWriter.ReadCurrentParentAsync(fixture.Catalog, intent, CancellationToken.None);
        Assert.AreEqual(targetParent, actualParent.Context.Member); Assert.AreEqual(targetProof.Verification, actualParent.Context.Verification);
        Assert.AreEqual(fixture.Parent.MemberId, moved.Context.Member.ParentMemberId);
        Assert.AreEqual(targetParent.MemberId, moved.Context.Location.ParentMemberId);
        Assert.AreEqual(move.OperationId, moved.Context.Location.CompletedMoveOperationId);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberAsync(fixture.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
