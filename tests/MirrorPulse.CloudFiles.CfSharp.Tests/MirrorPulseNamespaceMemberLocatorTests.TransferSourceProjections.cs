using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalTransferGenerationMatchesOnlyItsExactOfficialTombstoneAcrossStoreOwners(bool nullableFileId)
    {
        await using var fixture = await Fixture.OpenAsync();
        var before = fixture.Row();
        if (nullableFileId) before = new(before.ItemId, before.RemoteId, before.RelativePath, before.Kind,
            before.RemoteRevision, null, false, before.UpdatedAt);
        await fixture.PutAsync(before);
        var original = OriginalTransferGeneration(fixture, before);
        MirrorPulseNamespaceTransferCoordinator.RequireOriginalSourceProjection(original, before, tombstone: false);
        var tombstone = new CloudItemState(before.ItemId, before.RemoteId, before.RelativePath, before.Kind,
            before.RemoteRevision, before.LocalFileId, true, before.UpdatedAt.AddSeconds(1));
        await fixture.PutAsync(tombstone); await fixture.RestartAsync();
        await using var read = await fixture.Store.BeginTransactionAsync();
        var current = await read.Items.GetByItemIdAsync(before.ItemId);
        MirrorPulseNamespaceTransferCoordinator.RequireOriginalSourceProjection(original, current, tombstone: true);
        await read.RollbackAsync();
        await fixture.AssertRowAndNoJournalAsync(tombstone);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("path")]
    [DataRow("raw-path")]
    [DataRow("case")]
    [DataRow("revision")]
    [DataRow("file-id")]
    [DataRow("null-file-id")]
    [DataRow("kind")]
    [DataRow("live")]
    public async Task OriginalTransferGenerationCannotBorrowAChangedOfficialProjection(string failure)
    {
        await using var fixture = await Fixture.OpenAsync(); var before = fixture.Row();
        var original = OriginalTransferGeneration(fixture, before);
        var current = new CloudItemState(failure == "item" ? Guid.NewGuid() : before.ItemId,
            failure == "remote" ? "a-different-remote" : before.RemoteId,
            failure switch { "path" => @"Docs\other", "raw-path" => before.RelativePath.Replace('\\', '/'), "case" => before.RelativePath.ToUpperInvariant(), _ => before.RelativePath },
            failure == "kind" ? CloudItemKind.Directory : before.Kind,
            failure == "revision" ? "another-revision" : before.RemoteRevision,
            failure == "null-file-id" ? null : failure == "file-id" ? before.LocalFileId + 1 : before.LocalFileId,
            failure != "live", before.UpdatedAt.AddSeconds(1));
        if (failure != "missing") await fixture.PutAsync(current);
        await fixture.RestartAsync();
        await using (var read = await fixture.Store.BeginTransactionAsync())
        {
            var row = await read.Items.GetByItemIdAsync(current.ItemId);
            Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseNamespaceTransferCoordinator.RequireOriginalSourceProjection(original, row, tombstone: true));
            await read.RollbackAsync();
        }
        if (failure != "missing") await fixture.AssertRowAndNoJournalAsync(current);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static MirrorPulseNamespaceTransferSourceProjection OriginalTransferGeneration(Fixture fixture, CloudItemState row) =>
        new(1, Guid.NewGuid(), row.ItemId, row.RemoteId, row.RelativePath, row.RemoteRevision,
            row.LocalFileId, fixture.Member.LocalObject, DateTimeOffset.UtcNow);
}
