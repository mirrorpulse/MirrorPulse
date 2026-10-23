namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseRemoteFailureBoundary
{
    Unknown, NamespaceRecovery, BirthIdentityOwnership, BirthPlanIdentityOwnership,
    BirthParentAdmission, BirthStarted, UpdateStarted, PollIntent, NativeItemMembership,
    NativeItemMissing, NativeAccessDenied, NativeSharingViolation, NativeOther
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
            "The selected native object has no registered namespace membership." or
            "The selected path cannot provide unambiguous native membership." => MirrorPulseRemoteFailureBoundary.NativeItemMembership,
            _ => MirrorPulseRemoteFailureBoundary.Unknown
        };
    }
}
