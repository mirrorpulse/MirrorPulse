using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseInstanceCursorStatus(
    InstanceId InstanceId,
    bool HasCursor,
    string? CursorFingerprint,
    DateTimeOffset? UpdatedAt);

public sealed record MirrorPulseCloudStatusSnapshot(
    int PendingUploadCount,
    int PendingRemoteConflictCount,
    IReadOnlyList<MirrorPulseInstanceCursorStatus> Cursors,
    IReadOnlySet<Guid> PendingRemoteConflictIds);

/// <summary>Reads CfSharp's authoritative journal, conflicts and checkpoints for product status UI.</summary>
public static class MirrorPulseCloudStatusReader
{
    public static Task<MirrorPulseCloudStatusSnapshot> ReadAsync(
        ICloudStateStore store,
        IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken = default) => ReadAsync(store, instanceIds, [], cancellationToken);

    /// <summary>Summarizes actual streamed and root-scoped checkpoints for display, without publishing or merging cursors.</summary>
    internal static async Task<MirrorPulseCloudStatusSnapshot> ReadAsync(
        ICloudStateStore store, IEnumerable<InstanceId> instanceIds, IEnumerable<RootRegistration> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(instanceIds);
        ArgumentNullException.ThrowIfNull(roots);
        InstanceId[] instances = instanceIds.Distinct().ToArray();
        RootRegistration[] registrations = roots.ToArray();
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<CloudOperationJournalEntry> pending = await transaction.Operations
            .ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudConflictState> conflicts = await transaction.Conflicts
            .ListAsync(cancellationToken).ConfigureAwait(false);
        var cursors = new List<MirrorPulseInstanceCursorStatus>(instances.Length);
        foreach (InstanceId instanceId in instances)
        {
            var checkpoints = new List<(string Name, CloudStateCheckpoint Value)>();
            string name = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instanceId);
            CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(
                name, cancellationToken)
                .ConfigureAwait(false);
            if (checkpoint is not null) checkpoints.Add((name, checkpoint));
            // Disabled roots keep their real checkpoints visible. Reading status never dispatches them.
            foreach (var root in registrations.Where(root => root.InstanceId == instanceId).DistinctBy(root => root.RootId))
            {
                string rootName = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instanceId, root.RootId);
                var rootCursor = await transaction.Checkpoints.GetAsync(rootName, cancellationToken).ConfigureAwait(false);
                if (rootCursor is not null) checkpoints.Add((rootName, rootCursor));
            }
            if (checkpoints.Count == 0) { cursors.Add(new(instanceId, false, null, null)); continue; }
            // Retain the existing display fingerprint for a lone streamed/legacy instance cursor.
            // Otherwise hash the complete named checkpoint set, binding each value to its stable root.
            byte[] displayState = checkpoints.Count == 1 && checkpoints[0].Name == name ? checkpoints[0].Value.Value.ToArray() :
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = "MirrorPulse.InstanceCursorStatus/v1",
                    checkpoints = checkpoints.OrderBy(item => item.Name, StringComparer.Ordinal)
                        .Select(item => new { name = item.Name, value = item.Value.Value.ToArray() })
                });
            cursors.Add(new(instanceId, true, Convert.ToHexString(SHA256.HashData(displayState))[..12],
                checkpoints.Max(item => item.Value.UpdatedAt)));
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new(pending.Count, conflicts.Count, cursors,
            conflicts.Select(conflict => conflict.ConflictId).ToHashSet());
    }
}
