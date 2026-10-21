using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false, "Docs/existing")]
    [DataRow(false, @"Docs\existing")]
    [DataRow(true, "Docs/existing")]
    [DataRow(true, @"Docs\existing")]
    public async Task TransferSourceAcceptsTheOfficialWindowsPathWithoutRewritingEitherStore(bool born, string officialPath)
    {
        await using var fixture = await Fixture.OpenAsync(born);
        await using var actor = new MirrorPulseNamespaceActor();
        var row = fixture.Row(officialPath); await fixture.PutAsync(row);
        var candidate = await ResolveAsync(fixture, actor);
        var source = await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Member.MemberId);
        Assert.IsNotNull(source);
        MirrorPulseNamespaceTransferCoordinator.RequireSourceProjection(source, candidate);
        Assert.AreEqual(officialPath, candidate.RelativePath);
        Assert.AreEqual("Docs/existing", source.Context.Location.RelativePath);
        await fixture.AssertRowAndNoJournalAsync(row);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberAsync(fixture.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("case")]
    [DataRow("identity")]
    [DataRow("tombstone")]
    [DataRow("missing")]
    public async Task TransferSourceRejectsAChangedOrMissingProjectionBeforeAnyEffect(string change)
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        var row = fixture.Row(change == "path" ? @"Docs\other" : change == "case" ? @"Docs\EXISTING" : Fixture.FirstPath);
        row = new CloudItemState(row.ItemId, change == "identity" ? "another:object" : row.RemoteId, row.RelativePath,
            row.Kind, row.RemoteRevision, row.LocalFileId, change == "tombstone", row.UpdatedAt);
        if (change != "missing") await fixture.PutAsync(row);
        var source = await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Member.MemberId);
        Assert.IsNotNull(source);
        var candidate = await ResolveAsync(fixture, actor);
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseNamespaceTransferCoordinator.RequireSourceProjection(source, candidate));
        Assert.IsEmpty(await fixture.Catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
