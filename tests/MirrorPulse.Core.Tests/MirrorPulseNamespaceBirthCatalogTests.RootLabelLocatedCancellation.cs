using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(false, false, true)]
    [DataRow(true, true, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public async Task PreparedBornRootCancellationRetainsLocatedEpochsAcrossAnIndependentAnchorRole(bool enabled, bool nested, bool rotateAnchor)
    {
        using var fixture = new Fixture();
        RootSubtreeFixture setup;
        var expectedEpochs = new Dictionary<Guid, MirrorPulseNamespaceBirthProtection>();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareBornRootJournalFixtureAsync(catalog, enabled, nested, async (store, members) =>
            {
                var time = members.Max(member => member.RegisteredAt).AddMilliseconds(20);
                foreach (var member in members)
                {
                    var epoch = await LocatedBornEpochAsync(store, member, time);
                    await store.RecordNamespaceBirthProtectionAsync(epoch);
                    expectedEpochs.Add(member.MemberId, epoch);
                    time = time.AddMilliseconds(20);
                }
            });
            Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
            if (rotateAnchor)
                _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Rename.Proof!.CapturedAt.AddSeconds(1));
        }

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var tree = await reopened.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
        Assert.AreEqual(setup.Root.MemberId, tree.RootMemberId);
        Assert.AreEqual(setup.Anchor.MemberId, tree.Anchor.Context.Member.MemberId);
        Assert.AreEqual(rotateAnchor ? "S-1-5-5-123-457" : Role, tree.Anchor.Context.Protection.RoleSid);
        Assert.HasCount(nested ? 3 : 1, tree.Members);
        Assert.IsNull(tree.RecoveryOwner, "A clean older-role preparation cannot manufacture an initializer owner.");
        foreach (var snapshot in tree.Members)
        {
            var expected = expectedEpochs[snapshot.Context.Member.MemberId];
            Assert.AreEqual(expected.ProtectionId, snapshot.Context.Protection.ProtectionId);
            Assert.AreEqual(expected.RoleSid, snapshot.Context.Protection.RoleSid);
            Assert.AreEqual(expected.Verification, snapshot.Context.Verification);
            Assert.AreEqual(expected, await reopened.ReadLatestNamespaceBirthProtectionAsync(snapshot.Context.Member.OriginId));
            Assert.AreEqual(setup.Root.RootId, snapshot.Context.Member.RootId);
        }
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        if (rotateAnchor)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        else
            Assert.IsTrue((await reopened.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId)).FencedRoots.Contains(setup.Move.RootId));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Console.WriteLine($"LocatedRootCancellationMetadata: enabled={enabled}; nested={nested}; laterAnchor={rotateAnchor}; originalLocatedEpochs=True; originalPendingLabel=True; currentAdmissionGranted=False; nativeExecuted=False.");
    }
}
