using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentProtectionUsesOnlyCompletedMemberLocationWithoutRewritingOriginalProofAcrossOwners(bool born)
    {
        await using var fixture = await Fixture.OpenAsync(born);
        var original = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None);
        var parent = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Parent, CancellationToken.None);
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), fixture.Member.MemberId, original.ProtectionId,
            fixture.Member.RootId!.Value, fixture.Member.LocalObject, fixture.ItemId, "local:original", original.OwnerSid, original.RoleSid,
            new(fixture.Parent.MemberId, fixture.Parent.LocalObject, parent.ProtectionId, fixture.Member.RelativePath),
            new(fixture.Parent.MemberId, fixture.Parent.LocalObject, parent.ProtectionId, "Docs/moved"), DateTimeOffset.UtcNow.AddSeconds(1));
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(move);
        var start = new MirrorPulseNamespaceMemberMoveStart(1, move.OperationId, move.PreparedAt.AddSeconds(1));
        await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(start);
        // An independently moved official projection is an observation, not product completion.
        await fixture.PutAsync(fixture.Row(@"Docs\moved"));
        Assert.AreEqual(original, await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None));
        var child = original.Verification with { ObservedAt = start.StartedAt.AddSeconds(1) };
        var actualParent = parent.Verification with { ObservedAt = child.ObservedAt };
        await fixture.Catalog.RecordNamespaceMemberMoveObservationAsync(new(1, move.OperationId, move.ItemId, move.RemoteId,
            null, move.Target.RelativePath, false, true, 1, child, actualParent));
        var projection = new MirrorPulseNamespaceMemberMoveProjection(1, move.OperationId, move.ItemId, move.RemoteId,
            null, move.Target.RelativePath, child.ObservedAt.AddSeconds(1));
        await fixture.Catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        Assert.AreEqual(original, await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None));
        await fixture.Catalog.CompleteNamespaceMemberMoveAsync(new(1, move.OperationId, move.ItemId, move.RemoteId,
            move.Target.RelativePath, false, projection.ObservedAt.AddSeconds(1),
            child with { ObservedAt = projection.ObservedAt.AddMilliseconds(1) },
            actualParent with { ObservedAt = projection.ObservedAt.AddMilliseconds(2) }, null));
        await fixture.RestartAsync();
        var current = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None);
        Assert.AreEqual(original with { RelativePath = move.Target.RelativePath }, current);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberAsync(fixture.Member.MemberId));
        var historical = born ? await MirrorPulseNamespaceProtectionProofReader.ReadBirthAsync(fixture.Catalog, fixture.Member.OriginId, CancellationToken.None) :
            await MirrorPulseNamespaceProtectionProofReader.ReadOriginalAsync(fixture.Catalog, fixture.Member.OriginId, CancellationToken.None);
        Assert.AreEqual(original with { CurrentIdentity = null }, historical);
        Assert.IsNull(historical.CurrentIdentity);
        Assert.AreEqual(await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId), current.CurrentIdentity);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog,
            fixture.Member with { RelativePath = move.Target.RelativePath }, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentProtectionRetainsAcceptedIdentitySeparatelyFromFirstProvenanceAcrossOwners(bool born)
    {
        await using var fixture = await Fixture.OpenAsync(born);
        var original = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None);
        var birth = born ? await fixture.Catalog.ReadNamespaceBirthAsync(fixture.Member.OriginId) : null;
        var accepted = await RetainAcceptedIdentityAsync(fixture);
        await fixture.RestartAsync();
        var current = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(fixture.Catalog, fixture.Member, CancellationToken.None);
        var identity = await fixture.Catalog.ReadNamespaceMemberIdentityAsync(fixture.Member.MemberId);
        Assert.AreEqual(original with { CurrentIdentity = identity }, current);
        Assert.AreEqual(accepted.AcceptedItemId, current.CurrentIdentity!.ItemId);
        Assert.AreEqual(accepted.RemoteId, current.CurrentIdentity.RemoteId);
        Assert.AreEqual(original.CurrentIdentity!.OriginalItemId, current.CurrentIdentity.OriginalItemId);
        Assert.AreEqual(original.CurrentIdentity.OriginalRemoteId, current.CurrentIdentity.OriginalRemoteId);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberAsync(fixture.Member.MemberId));
        if (born) Assert.AreEqual(birth, await fixture.Catalog.ReadNamespaceBirthAsync(fixture.Member.OriginId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
