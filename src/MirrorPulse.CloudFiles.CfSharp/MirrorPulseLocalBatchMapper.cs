using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseLocalBatchPlan(
    IReadOnlyList<MirrorPulseWorkerChangeCommand> Commands,
    IReadOnlyList<Guid> DirectoryMetadataOperationIds,
    bool RequiresFullRescan,
    IReadOnlyList<MirrorPulseBlockedLocalOperation>? BlockedOperations = null);

/// <summary>Copies only public feed observations for product routing policy.</summary>
public sealed record MirrorPulseLocalChangeObservation(Guid OperationId, long Sequence, CloudLocalChangeKind Kind,
    Guid? ItemId, string RelativePath, string? PreviousRelativePath, bool IsDirectory, DateTimeOffset ObservedAt);

/// <summary>Projects CfSharp's durable local journal into per-Adapter commands without a second queue.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public static class MirrorPulseLocalBatchMapper
{
    public static MirrorPulseLocalBatchPlan Map(
        CloudLocalChangeBatch batch,
        MirrorPulseRootRouter router)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return MapObservations(batch.Changes.Select(change => new MirrorPulseLocalChangeObservation(change.OperationId,
            change.Sequence, change.Kind, change.ItemId, change.RelativePath, change.PreviousRelativePath,
            change.IsDirectory, change.ObservedAt)), router, batch.RequiresFullRescan);
    }

    public static MirrorPulseLocalBatchPlan MapObservations(IEnumerable<MirrorPulseLocalChangeObservation> observations,
        MirrorPulseRootRouter router, bool requiresFullRescan = false)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(router);
        if (requiresFullRescan)
        {
            return new([], [], true);
        }

        var commands = new List<MirrorPulseWorkerChangeCommand>();
        var directoryMetadata = new List<Guid>();
        var blocked = new List<MirrorPulseBlockedLocalOperation>();
        foreach (MirrorPulseLocalChangeObservation change in observations)
        {
            MirrorPulseRoutedItem current;
            MirrorPulseRoutedItem? previous;
            try
            {
                current = router.ResolvePath(change.RelativePath);
                previous = change.PreviousRelativePath is null ? null : router.ResolvePath(change.PreviousRelativePath);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                Block(change, null, MirrorPulseLocalOperationBlockReason.UnregisteredPath);
                continue;
            }
            if (change.IsDirectory && change.Kind == CloudLocalChangeKind.MetadataUpdate)
            {
                // Directory timestamps and availability flags do not have a Worker mutation.
                // Retain the real child operations in the batch and acknowledge this local
                // bookkeeping event so it cannot remain a permanent pending upload.
                directoryMetadata.Add(change.OperationId);
                continue;
            }

            if (current.RelativePath.Length == 0)
            {
                Block(change, current.InstanceId, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired);
                continue;
            }

            if (previous is not null && (previous.InstanceId != current.InstanceId || previous.RootKey != current.RootKey))
            {
                Block(change, current.InstanceId, MirrorPulseLocalOperationBlockReason.CrossRootMove);
                continue;
            }

            MirrorPulseLocalOperationBlockReason? reason = change.Kind switch
            {
                CloudLocalChangeKind.Move when previous is null || previous.RelativePath.Length == 0 => MirrorPulseLocalOperationBlockReason.InvalidMove,
                CloudLocalChangeKind.MetadataUpdate => MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange,
                _ when !Enum.IsDefined(change.Kind) => MirrorPulseLocalOperationBlockReason.UnsupportedChangeKind,
                _ => null,
            };
            if (reason is { } value) { Block(change, current.InstanceId, value); continue; }

            commands.Add(new MirrorPulseWorkerChangeCommand(
                change.OperationId,
                change.Sequence,
                current.InstanceId,
                current.RootKey,
                ToWorkerKind(change.Kind),
                current.RelativePath,
                previous?.RootKey,
                previous?.RelativePath,
                change.IsDirectory,
                change.ItemId,
                change.ObservedAt));
        }

        return new(commands.AsReadOnly(), directoryMetadata.AsReadOnly(), false, blocked.AsReadOnly());

        void Block(MirrorPulseLocalChangeObservation change, InstanceId? instance, MirrorPulseLocalOperationBlockReason reason) =>
            blocked.Add(new(change.OperationId, instance, change.RelativePath, reason, change.ObservedAt));
    }

    private static MirrorPulseWorkerChangeKind ToWorkerKind(CloudLocalChangeKind kind) => kind switch
    {
        CloudLocalChangeKind.Create => MirrorPulseWorkerChangeKind.Create,
        CloudLocalChangeKind.ContentUpdate => MirrorPulseWorkerChangeKind.ContentUpdate,
        CloudLocalChangeKind.MetadataUpdate => MirrorPulseWorkerChangeKind.MetadataUpdate,
        CloudLocalChangeKind.Move => MirrorPulseWorkerChangeKind.Move,
        CloudLocalChangeKind.Delete => MirrorPulseWorkerChangeKind.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
