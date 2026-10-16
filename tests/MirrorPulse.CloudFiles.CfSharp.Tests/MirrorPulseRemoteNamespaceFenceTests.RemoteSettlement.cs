using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [SupportedOSPlatform("windows10.0.26100")]
    [DataRow("missing")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("ordinal")]
    [DataRow("change")]
    [DataRow("status")]
    public async Task RemoteSettlementRejectsInexactOfficialProgressBeforeTouchingAnObject(string mismatch)
    {
        await VerifyRemoteProgressRefusalAsync(mismatch, settle: true);
    }

    [TestMethod]
    [SupportedOSPlatform("windows10.0.26100")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("ordinal")]
    [DataRow("started")]
    [DataRow("missing-start")]
    [DataRow("offline")]
    public async Task RemoteStartAndSettlementDoNotReplaceMissingHistoryOrReauthorizeAnUnknownDispatch(string mismatch)
    {
        await VerifyRemoteProgressRefusalAsync(mismatch, settle: mismatch == "missing-start" || mismatch == "offline");
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private static async Task VerifyRemoteProgressRefusalAsync(string mismatch, bool settle)
    {
        await using var fixture = await MoveFixture.OpenAsync(); var instance = fixture.Docs.InstanceId;
        var batch = new CloudRemoteChangeBatch($"{instance}/batch", ReadOnlyMemory<byte>.Empty,
            [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, fixture.Move.RemoteId, "incoming", CloudItemKind.File,
                fixture.Move.Target.RelativePath, fixture.Move.ItemId, "previous", fixture.Move.Source.RelativePath)], new byte[] { 1 });
        var intent = fixture.Move with
        {
            Version = 2,
            RemoteEntry = new(1, instance, batch.BatchId,
            Convert.ToHexString(batch.Fingerprint.Span), 1, 0, "move", "previous", "incoming", "original")
        };
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(intent);
        bool started = mismatch != "missing-start" && (settle || mismatch == "started");
        if (started) await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, DateTimeOffset.UtcNow));
        if (mismatch == "offline") fixture.Router.ReplaceRegistrations([new(fixture.Docs.AdapterId, instance, fixture.Docs.RootId,
            fixture.Docs.UniquenessKey, fixture.Docs.Label, fixture.Docs.DirectoryName, fixture.Docs.CustomEntry,
            RootRegistrationState.Disabled, fixture.Docs.RegisteredAt, fixture.Docs.IdentityScope)]);
        Directory.CreateDirectory(fixture.Paths.SyncRootPath); var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
        await using var store = await state.OpenAsync(new(fixture.Paths.SyncRootPath));
        if (mismatch != "missing")
        {
            int count = mismatch == "count" ? 2 : 1;
            int applied = settle ? 1 : 0; if (mismatch == "ordinal") applied = settle ? 0 : 1;
            var status = mismatch == "status" ? CloudRemoteBatchStatus.Applying : applied == count ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying;
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.RemoteBatches.UpsertAsync(new(batch.BatchId, applied == count ? batch.FinalCursor.Span : batch.InitialCursor.Span,
                applied, count, status, [], DateTimeOffset.UtcNow,
                mismatch == "fingerprint" ? new byte[32] : batch.Fingerprint, applied == 0 ? null : mismatch == "change" ? "different" : "move"));
            await transaction.CommitAsync();
        }
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(state)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
        var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, fixture.Catalog, fixture.Router, execution, actor);
        if (mismatch is "started" or "offline")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => settle ?
                coordinator.SettleRemoteAsync(intent.OperationId, instance, batch, 0).AsTask() :
                coordinator.BeginRemoteAsync(intent.OperationId, instance, batch, 0).AsTask());
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => settle ?
                coordinator.SettleRemoteAsync(intent.OperationId, instance, batch, 0).AsTask() :
                coordinator.BeginRemoteAsync(intent.OperationId, instance, batch, 0).AsTask());
        Assert.AreEqual(started, await fixture.Catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId) is not null);
        Assert.IsNull(await fixture.Catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId));
        Assert.IsNull(await fixture.Catalog.ReadNamespaceMemberMoveCompletionAsync(intent.OperationId));
        Assert.AreEqual(fixture.Child, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "new.txt"));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
    }
}
