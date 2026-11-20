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
    IReadOnlyList<MirrorPulseBlockedLocalOperation>? BlockedOperations = null)
{
    /// <summary>All routable observations, including held namespace and metadata dependencies.</summary>
    public IReadOnlyList<MirrorPulseWorkerChangeCommand>? CompleteWindow { get; init; }

    /// <summary>Routable blocked originals still fence their affected names, even when another observation is unregistered.</summary>
    public IReadOnlyList<MirrorPulseWorkerChangeCommand> RoutedBlockedCommands { get; init; } = [];
}

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

        var builder = new MirrorPulseLocalBatchPlanBuilder(router);
        foreach (var change in observations) builder.Add(change);
        while (!builder.Finish(512)) { }
        return builder.Result;
    }
}

/// <summary>Pure incremental routing and final dependency projection for one complete window.</summary>
internal sealed class MirrorPulseLocalBatchPlanBuilder(MirrorPulseRootRouter router)
{
    private readonly List<MirrorPulseWorkerChangeCommand> _commands = [];
    private readonly List<MirrorPulseBlockedLocalOperation> _blocked = [];
    private readonly List<MirrorPulseWorkerChangeCommand> _complete = [];
    private readonly List<MirrorPulseWorkerChangeCommand> _routedBlocked = [];
    private readonly HashSet<Guid> _blockedIds = [];
    private bool _completeRouting = true;
    private int _finalized;
    private bool _finishing;
    private MirrorPulseLocalBatchPlan? _result;

    internal MirrorPulseLocalBatchPlan Result => _result ?? throw new InvalidOperationException("A partial window has no dispatch plan.");

    internal void Add(MirrorPulseLocalChangeObservation change)
    {
        if (_finishing) throw new InvalidOperationException("A finishing window cannot accept later observations.");
        ArgumentNullException.ThrowIfNull(change);
        MirrorPulseRoutedItem current;
        MirrorPulseRoutedItem? previous;
        try
        {
            current = router.ResolvePath(change.RelativePath);
            previous = change.PreviousRelativePath is null ? null : router.ResolvePath(change.PreviousRelativePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            _completeRouting = false;
            Block(change, null, MirrorPulseLocalOperationBlockReason.UnregisteredPath);
            return;
        }
        MirrorPulseWorkerChangeCommand? command = Enum.IsDefined(change.Kind) ? new(
            change.OperationId, change.Sequence, current.InstanceId, current.RootKey, ToWorkerKind(change.Kind),
            current.RelativePath, previous?.RootKey, previous?.RelativePath, change.IsDirectory, change.ItemId, change.ObservedAt)
        { PreviousInstanceId = previous is not null && previous.InstanceId != current.InstanceId ? previous.InstanceId : null } : null;
        if (command is not null) _complete.Add(command); else _completeRouting = false;
        // CfSharp filters its proved remote-creation/application echoes. An
        // unfiltered metadata observation cannot prove that a source accepted
        // timestamps or attributes. The product promises content synchronization,
        // and the current Worker contract has no metadata mutation capability.
        if (change.Kind == CloudLocalChangeKind.MetadataUpdate)
        {
            Block(change, current.InstanceId, MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange);
            return;
        }

        if (current.RelativePath.Length == 0)
        {
            Block(change, current.InstanceId, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired);
            return;
        }

        if (previous is not null && (previous.InstanceId != current.InstanceId || previous.RootKey != current.RootKey))
        {
            Block(change, current.InstanceId, MirrorPulseLocalOperationBlockReason.CrossRootMove);
            return;
        }

        MirrorPulseLocalOperationBlockReason? reason = change.Kind switch
        {
            CloudLocalChangeKind.Move when previous is null || previous.RelativePath.Length == 0 => MirrorPulseLocalOperationBlockReason.InvalidMove,
            _ when !Enum.IsDefined(change.Kind) => MirrorPulseLocalOperationBlockReason.UnsupportedChangeKind,
            _ => null,
        };
        if (reason is { } value) { Block(change, current.InstanceId, value); return; }

        _commands.Add(command!);
    }

    internal bool Finish(int budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        _finishing = true;
        int count = 0;
        while (_finalized < _complete.Count && count++ < budget)
        {
            var command = _complete[_finalized++];
            if (_blockedIds.Contains(command.OperationId)) _routedBlocked.Add(command);
        }
        if (_finalized != _complete.Count) return false;
        _result ??= new(_commands.AsReadOnly(), Array.Empty<Guid>(), false, _blocked.AsReadOnly())
        {
            CompleteWindow = _completeRouting ? _complete.AsReadOnly() : null,
            RoutedBlockedCommands = _routedBlocked.AsReadOnly()
        };
        return true;
    }

    private void Block(MirrorPulseLocalChangeObservation change, InstanceId? instance, MirrorPulseLocalOperationBlockReason reason)
    {
        _blocked.Add(new(change.OperationId, instance, change.RelativePath, reason, change.ObservedAt));
        _blockedIds.Add(change.OperationId);
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
