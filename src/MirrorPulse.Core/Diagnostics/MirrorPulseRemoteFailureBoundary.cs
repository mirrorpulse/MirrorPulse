namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseRemotePollStage
{
    RootSelection, RetainedIntent, SnapshotRead, WorkerEnumeration, IdentitySelection,
    Projection, RetainIntent, ApplyAndCommit, CheckpointValidation, SnapshotCommit, IntentCompletion
}

public enum MirrorPulseRemoteFailureBoundary
{
    Unknown, NamespaceRecovery, BirthIdentityOwnership, BirthPlanIdentityOwnership,
    BirthParentAdmission, BirthStarted, UpdateStarted, PollIntent, NativeItemMembership,
    NativeItemMissing, NativeAccessDenied, NativeSharingViolation, NativeOther,
    GenerationHistory, GenerationAddress, PollProjection, PollCursor,
    RootIdentityMapping, RetirementAdmission, BirthItemProjection
}

/// <summary>Classifies fixed product failures without exporting exception text or stack contents.</summary>
public static class MirrorPulseRemoteFailureClassifier
{
    public static MirrorPulseRemoteFailureBoundary ReadBoundary(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is System.ComponentModel.Win32Exception native)
            return native.NativeErrorCode switch
            {
                2 or 3 => MirrorPulseRemoteFailureBoundary.NativeItemMissing,
                5 => MirrorPulseRemoteFailureBoundary.NativeAccessDenied,
                32 => MirrorPulseRemoteFailureBoundary.NativeSharingViolation,
                _ => MirrorPulseRemoteFailureBoundary.NativeOther
            };
        return exception.Message switch
        {
            "Remote changes must wait for managed root namespace recovery." or
            "Another durable namespace owner prevents this remote birth." or
            "Another namespace owner prevents the original remote update." => MirrorPulseRemoteFailureBoundary.NamespaceRecovery,
            "The remote birth identity is already owned by another original object or birth." => MirrorPulseRemoteFailureBoundary.BirthIdentityOwnership,
            "A new birth cannot adopt an original protected file identity." => MirrorPulseRemoteFailureBoundary.BirthPlanIdentityOwnership,
            "The original remote birth requires its admitted parent placement and namespace role." or
            "A remote birth requires its exact current direct parent member and placement." => MirrorPulseRemoteFailureBoundary.BirthParentAdmission,
            "A started remote birth requires first-object outcome settlement before SDK redispatch." => MirrorPulseRemoteFailureBoundary.BirthStarted,
            "A started remote update requires its original SDK progress and fresh outcome recovery before another invocation." => MirrorPulseRemoteFailureBoundary.UpdateStarted,
            "The pending poll intent does not recreate its immutable batch." => MirrorPulseRemoteFailureBoundary.PollIntent,
            "The current generation lost its immutable history." or
            "The retained generation fingerprint is invalid." or
            "The generation history is missing." or
            "The indexed current generation disagrees with its immutable address." or
            "An indexed source generation lost its retirement." or
            "A source generation lost its transfer owner." or
            "A source generation lost its original transfer." or
            "The original item index disagrees with its source generation." => MirrorPulseRemoteFailureBoundary.GenerationHistory,
            "The selected generation does not encode this original backend address and stable root." or
            "An ambiguous retired public item requires explicit generation reconciliation." => MirrorPulseRemoteFailureBoundary.GenerationAddress,
            "The captured public address does not identify this backend object and stable root." or
            "A new poll projection requires every selected public address." or
            "The poll projection must retain exactly its original identity format." or
            "The retained candidate changed independently of its intent." => MirrorPulseRemoteFailureBoundary.PollProjection,
            "The Adapter batch does not follow its durable CfSharp cursor." or
            "A completed remote poll batch must have its final safe cursor." or
            "CfSharp reported an applied batch with an incomplete safe cursor." => MirrorPulseRemoteFailureBoundary.PollCursor,
            "The placeholder identity belongs to another Adapter root." or
            "The polling entry escapes its retained root scope." or
            "The root polling batch has no current stable root mapping." => MirrorPulseRemoteFailureBoundary.RootIdentityMapping,
            "The original remote delete child is not protected membership." or
            "A missing member cannot invent a remote deletion admission." or
            "The original SDK delete does not identify its actual public item projection." => MirrorPulseRemoteFailureBoundary.RetirementAdmission,
            "The original SDK birth item projection does not match its admitted identity and path." => MirrorPulseRemoteFailureBoundary.BirthItemProjection,
            "The selected native object has no registered namespace membership." or
            "The selected path cannot provide unambiguous native membership." => MirrorPulseRemoteFailureBoundary.NativeItemMembership,
            _ => MirrorPulseRemoteFailureBoundary.Unknown
        };
    }
}
