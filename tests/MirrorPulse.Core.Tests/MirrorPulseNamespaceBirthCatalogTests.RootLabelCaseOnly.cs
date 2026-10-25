using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelSubtreeCatalog")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CaseOnlyRootLabelSettlesEveryOriginalPositionAndRetainsBothHistoricalReceipts(bool enabled)
    {
        using var fixture = new Fixture();
        RootSubtreeFixture setup;
        MirrorPulseNamespaceDirectoryMoveCompletion first;
        MirrorPulseNamespaceDirectoryMoveCompletion second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            first = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(first);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            // Synthetic opaque catalog facts only: no SDK proof decoding or native access.
            var next = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "RENAMED", "Renamed",
                setup.Rename.Proof! with { CapturedAt = first.Root.VerifiedAt.AddSeconds(1) });
            var move = setup.Move with
            {
                OperationId = next.Intent.OperationId,
                Source = setup.Move.Source with { RelativePath = "Renamed" },
                Target = setup.Move.Target with { RelativePath = "RENAMED" },
                PreviousMoveOperationId = first.OperationId,
                SourcePositionOperationId = first.OperationId,
                PreparedAt = next.Proof!.CapturedAt.AddSeconds(1)
            };
            second = await PrepareRootSubtreeFactsAsync(catalog, setup with { Rename = next, Move = move });
            await catalog.CompleteNamespaceDirectoryMoveAsync(second);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var current = await reopened.ReadManagedRootRenameAsync(setup.Move.RootId);
        Assert.IsNotNull(current); Assert.AreEqual(second.OperationId, current.OperationId);
        Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, current.Phase);
        await reopened.CompleteNamespaceDirectoryMoveAsync(first);
        await reopened.CompleteNamespaceDirectoryMoveAsync(second);
        Assert.AreEqual(current, await reopened.ReadManagedRootRenameAsync(setup.Move.RootId));
        var registration = (await reopened.ReadAdapterTopologyAsync()).Roots.Single();
        Assert.AreEqual("RENAMED", registration.Label); Assert.AreEqual("RENAMED", registration.DirectoryName);
        Assert.AreEqual(setup.Registration.RootId, registration.RootId);
        Assert.AreEqual(setup.Registration.State, registration.State);
        foreach (var member in setup.Members)
        {
            Assert.AreEqual(member, await reopened.ReadNamespaceMemberAsync(member.MemberId));
            Assert.AreEqual("RENAMED" + member.RelativePath["Docs".Length..], (await reopened.ReadNamespaceMemberLocationAsync(member.MemberId)).RelativePath);
            Assert.IsNotNull(await reopened.ReadNamespaceMemberPositionAsync(member.MemberId, first.OperationId));
            Assert.IsNotNull(await reopened.ReadNamespaceMemberPositionAsync(member.MemberId, second.OperationId));
        }
        var retainedFirst = (await reopened.ReadNamespaceDirectoryMoveCompletionAsync(first.OperationId))!;
        var retainedSecond = (await reopened.ReadNamespaceDirectoryMoveCompletionAsync(second.OperationId))!;
        Assert.AreEqual(first with { Entries = retainedFirst.Entries }, retainedFirst);
        Assert.AreEqual(second with { Entries = retainedSecond.Entries }, retainedSecond);
        CollectionAssert.AreEqual(first.Entries.ToArray(), retainedFirst.Entries.ToArray());
        CollectionAssert.AreEqual(second.Entries.ToArray(), retainedSecond.Entries.ToArray());
        Assert.IsNull(await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
