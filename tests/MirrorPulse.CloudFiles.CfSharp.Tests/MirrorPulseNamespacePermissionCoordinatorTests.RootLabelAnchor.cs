using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    public async Task TheOriginalRootLabelAnchorRetainsItsRealCatalogBindingRoleAndEpochWithoutChangingMembership()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, root) = await PrepareAuditMembersAsync(catalog, fixture);
        var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchor.MemberId))!;
        var intent = RootLabelAnchorIntent(snapshot, root);
        var proof = MirrorPulseRootLabelAnchorInspection.RequireProof(snapshot, intent);
        Assert.IsNull(proof.RootId);
        Assert.AreEqual(string.Empty, proof.RelativePath);
        Assert.AreEqual(anchor.LocalObject, proof.Verification.LocalObject);
        Assert.AreEqual(intent.Source.ParentProtectionId, proof.ProtectionId);
        Assert.AreEqual(intent.RoleSid, proof.RoleSid);
        Assert.AreEqual(snapshot, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchor.MemberId));
        Assert.AreEqual(root, await catalog.ReadNamespaceMemberAsync(root.MemberId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("generic-mode")]
    [DataRow("no-public-evidence")]
    [DataRow("other-parent")]
    [DataRow("other-target-binding")]
    [DataRow("other-target-epoch")]
    [DataRow("other-target-position")]
    [DataRow("registered-root")]
    [DataRow("registered-parent")]
    [DataRow("current-parent")]
    [DataRow("current-path")]
    [DataRow("current-position")]
    [DataRow("registered-binding")]
    [DataRow("registered-owner")]
    [DataRow("role")]
    [DataRow("epoch")]
    [DataRow("permission-operation")]
    [DataRow("verification-binding")]
    [DataRow("verification-owner")]
    [DataRow("verification-dacl")]
    [DataRow("future-verification")]
    public async Task RootLabelAnchorRefusesAChangedOriginalParentRolePositionOrProtectionBeforeObjectAccess(string changed)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, root) = await PrepareAuditMembersAsync(catalog, fixture);
        var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchor.MemberId))!;
        var snapshot = original;
        var context = original.Context;
        var intent = RootLabelAnchorIntent(snapshot, root);
        switch (changed)
        {
            case "generic-mode": intent = intent with { Version = 1 }; break;
            case "no-public-evidence": intent = intent with { DirectoryMoveEvidence = null }; break;
            case "other-parent": intent = intent with { Target = intent.Target with { ParentMemberId = Guid.NewGuid() } }; break;
            case "other-target-binding": intent = intent with { Target = intent.Target with { ParentLocalObject = root.LocalObject } }; break;
            case "other-target-epoch": intent = intent with { Target = intent.Target with { ParentProtectionId = Guid.NewGuid() } }; break;
            case "other-target-position": intent = intent with { Target = intent.Target with { ParentPositionOperationId = Guid.NewGuid() } }; break;
            case "registered-root": context = context with { Member = context.Member with { RootId = root.RootId } }; break;
            case "registered-parent": context = context with { Member = context.Member with { ParentMemberId = root.MemberId } }; break;
            case "current-parent": context = context with { Location = context.Location with { ParentMemberId = root.MemberId } }; break;
            case "current-path": context = context with { Location = context.Location with { RelativePath = "Docs" } }; break;
            case "current-position": context = context with { Location = context.Location with { PositionOperationId = Guid.NewGuid() } }; break;
            case "registered-binding": context = context with { Member = context.Member with { LocalObject = root.LocalObject } }; break;
            case "registered-owner": context = context with { Member = context.Member with { OwnerSid = "S-1-5-21-9-8-7-1001" } }; break;
            case "role": context = context with { Protection = context.Protection with { RoleSid = "S-1-5-5-123-999" } }; break;
            case "epoch": context = context with { Protection = context.Protection with { ProtectionId = Guid.NewGuid() } }; break;
            case "permission-operation": snapshot = snapshot with { PermissionOperationId = Guid.NewGuid() }; break;
            case "verification-binding": context = context with { Verification = context.Verification with { LocalObject = root.LocalObject } }; break;
            case "verification-owner": context = context with { Verification = context.Verification with { OwnerSid = "S-1-5-21-9-8-7-1001" } }; break;
            case "verification-dacl": context = context with { Verification = context.Verification with { Dacl = Original } }; break;
            case "future-verification": context = context with { Verification = context.Verification with { ObservedAt = intent.PreparedAt.AddSeconds(1) } }; break;
        }
        snapshot = snapshot with { Context = context };
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseRootLabelAnchorInspection.RequireProof(snapshot, intent));
        Assert.AreEqual(original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchor.MemberId));
        Assert.AreEqual(root, await catalog.ReadNamespaceMemberAsync(root.MemberId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static MirrorPulseNamespaceMemberMoveIntent RootLabelAnchorIntent(MirrorPulseNamespaceMemberProtectionSnapshot anchor,
        MirrorPulseNamespaceMemberRegistration root)
    {
        var proof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(anchor);
        return new(3, Guid.NewGuid(), root.MemberId, root.ProtectionId, root.RootId!.Value, root.LocalObject,
            Guid.NewGuid(), "local:root-anchor-model", root.OwnerSid, proof.RoleSid,
            new(anchor.Context.Member.MemberId, anchor.Context.Member.LocalObject, proof.ProtectionId, "Docs")
            { ParentPositionOperationId = anchor.Context.Location.CurrentPositionOperationId },
            new(anchor.Context.Member.MemberId, anchor.Context.Member.LocalObject, proof.ProtectionId, "Renamed")
            { ParentPositionOperationId = anchor.Context.Location.CurrentPositionOperationId }, DateTimeOffset.UtcNow.AddSeconds(1))
        { DirectoryMoveEvidence = "AQID" }; // Synthetic anchor classification only; no public SDK/native authorization is claimed.
    }
}
