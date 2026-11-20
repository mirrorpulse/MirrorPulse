using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteAcknowledgementMigratesSchema101AndRefusesMissingSchema102Ledger()
    {
        using var fixture = new Fixture();
        await using (var fresh = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_acknowledgements; PRAGMA user_version=101;");
        await using (var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.IsNull(await upgraded.ReadConflictKeepRemoteAcknowledgementAsync(Guid.NewGuid()));
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_acknowledgements;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var refused = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        });
    }

    [TestMethod]
    public async Task KeepRemoteAcknowledgementSettlesOnlyItsVerifiedOriginalClaimAndFence()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var restore = await PrepareKeepRemoteRestoreAsync(catalog);
        Guid command = restore.Start.CommandId;
        var first = (await catalog.ReadConflictKeepRemoteFileStartAsync(command))!;
        var root = first.Original.Context.Member.RootId!.Value;
        var preparation = (await catalog.ReadConflictKeepRemoteAsync(command))!;
        var ids = preparation.Intent.OriginalOperationIds.ToArray();
        var startAt = restore.Result.RecordedAt.AddSeconds(1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartConflictKeepRemoteAcknowledgementAsync(
            command, first.StoreVolumeSerialNumber, first.StoreFileId, startAt));
        await catalog.RecordConflictKeepRemoteRestoreStartAsync(restore.Start);
        await catalog.RecordConflictKeepRemoteRestoreResultAsync(restore.Result);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartConflictKeepRemoteAcknowledgementAsync(
            command, first.StoreVolumeSerialNumber, Guid.NewGuid(), startAt));
        var start = await catalog.StartConflictKeepRemoteAcknowledgementAsync(command, first.StoreVolumeSerialNumber, first.StoreFileId, startAt);
        Assert.AreEqual(restore.Result.AttemptId, start.RestoreAttemptId);
        Assert.AreEqual(restore.Result.NativeAttemptId, start.NativeAttemptId);
        Assert.IsNull((await catalog.ReadConflictKeepRemoteAcknowledgementAsync(command))!.Result);
        Assert.AreEqual(new MirrorPulseNamespacePendingOperation(command, root, MirrorPulseNamespacePendingOperationKind.ConflictKeepRemote),
            (await catalog.ReadNamespacePendingOperationsAsync(root)).Single());
        var result = new MirrorPulseConflictKeepRemoteAcknowledgementResult(command, start.InvocationId, ids,
            startAt.AddSeconds(1), startAt.AddSeconds(2));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteConflictKeepRemoteAcknowledgementAsync(
            result with { VerifiedMissingOriginals = ids.Reverse().ToArray() }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteConflictKeepRemoteAcknowledgementAsync(
            result with { SdkReturnedAt = startAt.AddSeconds(-1) }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteConflictKeepRemoteAcknowledgementAsync(
            result with { InvocationId = Guid.NewGuid() }));
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
        Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        await catalog.DisposeAsync();

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await reopened.StartConflictKeepRemoteAcknowledgementAsync(
            command, first.StoreVolumeSerialNumber, first.StoreFileId, startAt.AddSeconds(3)));
        var settled = await reopened.CompleteConflictKeepRemoteAcknowledgementAsync(result);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
        foreach (Guid conflict in preparation.Intent.ConflictIds)
            Assert.IsFalse((await reopened.ReadUploadConflictAsync(conflict))!.IsPending);
        Assert.HasCount(0, await reopened.ReadNamespacePendingOperationsAsync(root));
        Assert.HasCount(0, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(first.CommandId, (await reopened.ReadConflictKeepRemoteFileStartAsync(command))!.CommandId);
        Assert.AreEqual(start, (await reopened.ReadConflictKeepRemoteAcknowledgementAsync(command))!.Start);
        Assert.AreEqual(settled.ReadBackAt, (await reopened.CompleteConflictKeepRemoteAcknowledgementAsync(result)).ReadBackAt);
    }
}
