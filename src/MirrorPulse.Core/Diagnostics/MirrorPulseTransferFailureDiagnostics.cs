using System.ComponentModel;
using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseTransferCommandPhase
{
    Prepare, Read, Copy, VerifyTarget, PrepareTargetBirth, PublishTarget, RetireSource, Complete, ReadStatus
}

public enum MirrorPulseTransferFailureBoundary
{
    Unknown, NamespaceOwner, CurrentProtection, RootRoute, DestinationOccupied, JournalUnavailable, SourceProjection,
    JournalReconciliation, JournalOwnership, JournalShape, SourceBinding, PrivateSource,
    RecoveryParentDescriptor, RecoveryMemberDescriptor, RecoveryMemberIdentity, RecoveryMemberBinding
}

internal enum MirrorPulseTransferMemberRecoveryStep
{
    OriginalMember,
    BornMemberSelection,
    BornMemberHistory,
    BornMemberContext,
    BornMemberGuards,
    BornMemberAdmission,
    BornMemberScope,
    BornMemberReadback,
    BornMemberCompletion
}

/// <summary>Observes one failed transfer call using fixed fields while preserving its original exception and progress.</summary>
public static class MirrorPulseTransferFailureDiagnostics
{
    private static readonly object MemberRecoveryStepKey = new();

    internal static void MarkMemberRecoveryStep(Exception error, string? progress)
    {
        MirrorPulseTransferMemberRecoveryStep? step = progress switch
        {
            "NamespaceOriginalMemberBegin" => MirrorPulseTransferMemberRecoveryStep.OriginalMember,
            "NamespaceBornMemberBegin" => MirrorPulseTransferMemberRecoveryStep.BornMemberSelection,
            "NamespaceBornRecoveryHistoryBegin" or "NamespaceBornRecoveryHistoryCompleted" => MirrorPulseTransferMemberRecoveryStep.BornMemberHistory,
            "NamespaceBornRecoveryContextBegin" or "NamespaceBornRecoveryContextHistoryCompleted" or
            "NamespaceBornRecoveryContextLineageBegin" or "NamespaceBornRecoveryContextLineageCompleted" or
            "NamespaceBornRecoveryContextParentBegin" or "NamespaceBornRecoveryContextParentCompleted" or
            "NamespaceBornRecoveryContextInheritanceBegin" or "NamespaceBornRecoveryContextInheritanceCompleted" => MirrorPulseTransferMemberRecoveryStep.BornMemberContext,
            "NamespaceBornRecoveryGuardsBegin" => MirrorPulseTransferMemberRecoveryStep.BornMemberGuards,
            "NamespaceBornRecoveryActionBegin" or "NamespaceBornRecoveryActionCompleted" => MirrorPulseTransferMemberRecoveryStep.BornMemberAdmission,
            "NamespaceBornRecoveryScopeBegin" or "NamespaceBornRecoveryAdmissionCompleted" or
            "NamespaceBornRecoveryCallbackBegin" or "NamespaceBornRecoveryFirstInspectionCompleted" => MirrorPulseTransferMemberRecoveryStep.BornMemberScope,
            "NamespaceBornRecoveryReadbackBegin" => MirrorPulseTransferMemberRecoveryStep.BornMemberReadback,
            "NamespaceBornRecoveryCommitBegin" => MirrorPulseTransferMemberRecoveryStep.BornMemberCompletion,
            _ => null
        };
        if (step is not null) error.Data[MemberRecoveryStepKey] = step.Value;
    }

    public static async ValueTask<T> RunAsync<T>(Guid operationId, MirrorPulseTransferCommandPhase phase,
        Func<ValueTask<T>> action, Func<LogEntry, ValueTask> observe)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original transfer operation is required.", nameof(operationId));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(observe);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try
            {
                var fields = new List<LogField>
                {
                    new("operationId", operationId.ToString("D")),
                    new("transferCommandPhase", phase.ToString()),
                    new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(original)),
                    new("transferFailureKind", Kind(original)),
                    new("transferFailureBoundary", Boundary(original).ToString()),
                    new("hresult", original.HResult.ToString("X8", CultureInfo.InvariantCulture))
                };
                if (original is Win32Exception native)
                    fields.Add(new("nativeErrorCode", native.NativeErrorCode.ToString(CultureInfo.InvariantCulture)));
                var treeRecoveryStage = MirrorPulseRootLabelCancellationFailureDiagnostics.Stage(original);
                if (treeRecoveryStage is MirrorPulseRootLabelCancellationStage.CurrentTreeAnchorRecovery or
                    MirrorPulseRootLabelCancellationStage.CurrentTreeMemberRecovery or MirrorPulseRootLabelCancellationStage.CurrentTreeInitializationAudit)
                    fields.Add(new("transferTreeRecoveryStage", treeRecoveryStage.ToString()));
                if (original.Data[MemberRecoveryStepKey] is MirrorPulseTransferMemberRecoveryStep memberStep && Enum.IsDefined(memberStep))
                    fields.Add(new("transferMemberRecoveryStep", memberStep.ToString()));
                await observe(new(LogLevel.Error, "host", "TransferCommandFailed", DateTimeOffset.UtcNow, fields)).ConfigureAwait(false);
            }
            catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException)
            {
                // Diagnostics cannot replace the original command failure or authorize a retry.
            }
            throw;
        }

    }

    private static string Kind(Exception error) => error switch
    {
        Win32Exception => "Native",
        ObjectDisposedException => "Disposed",
        InvalidDataException => "InvalidData",
        InvalidOperationException => "InvalidOperation",
        UnauthorizedAccessException => "Authorization",
        OperationCanceledException => "Cancelled",
        IOException => "IO",
        ArgumentException => "Argument",
        _ => "Other"
    };

    private static MirrorPulseTransferFailureBoundary Boundary(Exception error) => error.Message switch
    {
        "Another original namespace owner prevents this transfer." or
        "A transfer continuation cannot bypass another target birth or its original owner." => MirrorPulseTransferFailureBoundary.NamespaceOwner,
        "Transfer execution requires unchanged current-role members and positions." or
        "Transfer execution requires the exact current members, protection, positions and public identities." or
        "A transfer member or direct parent lost its exact native binding and owned protection." or
        "Move execution requires current protection throughout its retained parent lineage." => MirrorPulseTransferFailureBoundary.CurrentProtection,
        "The transfer requires the original fully resident source object." => MirrorPulseTransferFailureBoundary.SourceBinding,
        "The transfer requires its complete retained read-only private content handle." or
        "The private content changed or does not match the original transfer." => MirrorPulseTransferFailureBoundary.PrivateSource,
        "The actual protected parent descriptor changed before born recovery." or
        "The actual current parent lost its verified descriptor." or
        "The direct parent requires its own retained protection recovery." => MirrorPulseTransferFailureBoundary.RecoveryParentDescriptor,
        "Born recovery cannot overwrite an unowned descriptor." or
        "The current born object does not inherit its actual parent's protection." or
        "The original object's current descriptor differs from its retained verified protection." => MirrorPulseTransferFailureBoundary.RecoveryMemberDescriptor,
        "Born recovery lost its retained current public identity." or
        "A known registered identity requires an actual placeholder." or
        "The native public identity disagrees with the member's retained current identity." => MirrorPulseTransferFailureBoundary.RecoveryMemberIdentity,
        "Born recovery lost its actual binding, owner, committed path, kind or single-link constraint." or
        "The actual registered object lost its binding, owner, current path or type." => MirrorPulseTransferFailureBoundary.RecoveryMemberBinding,
        "The routed transfer root differs from its current catalog registration." or
        "Both original transfer roots and instances must remain enabled and active." or
        "The transfer backend, version or root configuration changed." or
        "A transfer requires a negotiated V2 target before source materialization." or
        "The negotiated transfer protocol changed." or
        "The negotiated transfer protocol changed before target publication." => MirrorPulseTransferFailureBoundary.RootRoute,
        "An existing member owns the transfer destination." or
        "An existing native object occupies the transfer destination." or
        "The native transfer destination changed during the original operation." => MirrorPulseTransferFailureBoundary.DestinationOccupied,
        "The Host's running original journal is unavailable." or
        "A legacy transfer cannot acquire retrospective journal ownership." or
        "Transfer capture requires the Host's already-running public journal." or
        "The local-change feed has not been started." or
        "The local-change feed stopped unexpectedly." => MirrorPulseTransferFailureBoundary.JournalUnavailable,
        "The original journal requires reconciliation before transfer capture." or
        "The original journal lost its captured reconciliation boundary." => MirrorPulseTransferFailureBoundary.JournalReconciliation,
        "An original product owner already retains this transfer or effect ID." or
        "Original journal ownership cannot be adopted after the first remote copy start." or
        "The original journal reservation is immutable." => MirrorPulseTransferFailureBoundary.JournalOwnership,
        "An affected original source observation requires separate proved journal ownership." or
        "The original source content journal exceeds its bounded reservation." or
        "The local-change move paths are inconsistent." => MirrorPulseTransferFailureBoundary.JournalShape,
        "The transfer source lost its original public SDK projection." or
        "The native transfer source disagrees with its official public identity." or
        "The original source projection changed after transfer admission." or
        "The original transfer source projection changed before target publication." => MirrorPulseTransferFailureBoundary.SourceProjection,
        _ => MirrorPulseTransferFailureBoundary.Unknown
    };
}
