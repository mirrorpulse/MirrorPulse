using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;

if (args.Length == 3 && args[0] == "--protected-writer")
{
    return await ProtectedConfirmationWriter.RunAsync(args[1], args[2]);
}

if (args.Length == 3 && args[0] == "--namespace-operation")
{
    return await NamespaceMutationProbe.RunAsync(args[1], args[2]);
}

if (args.Length == 4)
{
    return await DurabilityFaultProbe.RunAsync(args);
}

if (args.Length != 1)
{
    return 2;
}

var paths = new MirrorPulseStoragePaths(Path.Combine(args[0], "sync"), Path.Combine(args[0], "data"));
Directory.CreateDirectory(paths.SyncRootPath);
DateTimeOffset now = DateTimeOffset.UtcNow;
Guid itemId = new("e3faed63-6701-41de-99d0-ddf82062023d");
Guid operationId = new("95a37be7-6b27-42c4-9006-526ddda5e856");
Guid conflictId = new("d0de7d17-2624-41ec-a457-a86f38e71d16");
Guid suppressionId = new("c5a089f7-97a0-4251-aad3-8665b1cb0025");
ICloudStateStore store = await MirrorPulseCfSharpStateStoreFactory.Create(paths)
    .OpenAsync(new CloudStateStoreContext(paths.SyncRootPath));
await using (ICloudStateTransaction committed = await store.BeginTransactionAsync())
{
    await committed.Items.UpsertAsync(new CloudItemState(
        itemId, "remote-a", "a.txt", CloudItemKind.File, null, null, false, now));
    await committed.Operations.EnqueueAsync(new CloudOperationJournalEntry(
        operationId, CloudStateOperationKind.ContentUpdate, itemId, [1], now));
    await committed.RemoteBatches.UpsertAsync(new CloudRemoteBatchState(
        "partial", [2], 1, 2, CloudRemoteBatchStatus.Applying, [3], now));
    await committed.Conflicts.UpsertAsync(new CloudConflictState(
        conflictId, itemId, CloudStateConflictKind.Content, [4], now));
    await committed.EchoSuppressions.UpsertAsync(new CloudEchoSuppressionState(
        suppressionId, itemId, CloudStateOperationKind.ContentUpdate, "a.txt", [5], now.AddHours(1)));
    await committed.Checkpoints.UpsertAsync(new CloudStateCheckpoint("remote/instance", [2], now));
    await committed.CommitAsync();
}

ICloudStateTransaction uncommitted = await store.BeginTransactionAsync();
await uncommitted.Checkpoints.UpsertAsync(new CloudStateCheckpoint("must-rollback", [9], now));
// Leave the SQLite transaction open and exit without disposing the store or transaction.
// The parent process must observe only the committed state after reopening the same WAL.
Environment.Exit(37);
return 0;

namespace MirrorPulse.CfSharp.CrashProbe
{
    public sealed class ProbeMarker;
}
