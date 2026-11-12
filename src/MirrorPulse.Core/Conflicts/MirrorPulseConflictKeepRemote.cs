using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Conflicts;

/// <summary>
/// Applies the explicit KeepRemote decision after the caller's safety checks.
/// </summary>
public static class MirrorPulseConflictKeepRemote
{
    /// <summary>Checks the original effect shape shared by protected admission and the durable file start.</summary>
    /// <remarks>The caller still needs the exact selected conflict, private copies, journal and native protection.</remarks>
    public static bool SupportsProtectedFileEffect(MirrorPulseJournalCoalescingPlan plan, MirrorPulseMutationIntent? rejected) =>
        rejected is { IsDirectory: false } && (rejected.Kind switch
        {
            MirrorPulseWorkerChangeKind.ContentUpdate => true,
            MirrorPulseWorkerChangeKind.Create => plan.Effect == MirrorPulseCoalescedEffect.CreateFile && rejected.ExpectedRevision is null,
            MirrorPulseWorkerChangeKind.Move => plan.Effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile &&
                !string.IsNullOrEmpty(rejected.ExpectedRevision) && rejected.PreviousRelativePath == plan.OriginalPath &&
                rejected.RelativePath == plan.FinalPath && plan.OriginalPath != plan.FinalPath,
            _ => false,
        });

    public static MirrorPulseConflictResolutionResult Apply(
        MirrorPulseConflictRecord conflict,
        DateTimeOffset appliedAt,
        Guid? resolutionId = null) =>
        MirrorPulseConflictResolutionPlanner.Apply(
            conflict,
            MirrorPulseConflictAction.KeepRemote,
            appliedAt,
            resolutionId: resolutionId);
}
