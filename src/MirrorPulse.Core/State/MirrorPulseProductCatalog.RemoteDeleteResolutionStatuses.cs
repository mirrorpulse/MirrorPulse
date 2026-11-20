using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A historical command snapshot; unknown SDK effects remain distinct from completed deletion.</summary>
public sealed record MirrorPulseRemoteDeleteResolutionStatusSnapshot(Guid CommandId, Guid ConflictId, RootId RootId,
    InstanceId InstanceId, string RelativePath, string Stage, int MemberCount, int SettledMembers, int NextOrdinal,
    DateTimeOffset PreparedAt, DateTimeOffset? StartedAt, string? Outcome, DateTimeOffset? OutcomeObservedAt,
    DateTimeOffset? MembersCompletedAt, bool CommandCompleted, string PackageVersion, string PackageSource);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseRemoteDeleteResolutionStatusSnapshot?> ReadRemoteDeleteResolutionStatusAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original Delete command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var current = await ReadRemoteDeleteResolutionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (current is null) { transaction.Commit(); return null; }
            _ = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
            var action = await ReadRemoteConflictActionCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The explicit Delete lost its original command.");
            bool completed = action.Receipt is not null;
            string stage = completed ? "Completed" : current.CompletedAt is not null ? "CommandReceiptPending" :
                current.Outcome is not null ? "SettlingMembers" : current.StartedAt is not null ? "InvocationMayHaveBegun" : "Prepared";
            var intent = current.Plan.Decision; var root = current.Plan.Entries[0].Original.Context.Member.RootId!.Value;
            var result = new MirrorPulseRemoteDeleteResolutionStatusSnapshot(commandId, intent.Conflict.ConflictId, root,
                intent.Conflict.InstanceId, intent.Conflict.RelativePath, stage, current.Plan.Entries.Count,
                current.Plan.Entries.Count - current.NextOrdinal - 1, current.NextOrdinal, current.Plan.PreparedAt, current.StartedAt,
                current.Outcome?.Status.ToString(), current.Outcome?.VerifiedAt, current.CompletedAt, completed, intent.PackageVersion, intent.PackageSource);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }
}
