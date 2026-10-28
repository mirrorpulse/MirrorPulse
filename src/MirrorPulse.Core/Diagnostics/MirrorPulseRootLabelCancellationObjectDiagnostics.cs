using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseCancellationObjectScope { Unknown, Anchor, Root, Descendant }
public enum MirrorPulseCancellationObjectOrigin { Unknown, CapturedOriginal, ProtectedBirth }
public enum MirrorPulseCancellationDescriptorDifference { Unknown, Same, CompletionOnly, ControlFlags, EntryCount, Principals, RightsOrAceFlags, MultipleOrOther }
public enum MirrorPulseCancellationRoleReference { Unknown, Absent, Present }

/// <summary>Closed observations only; contains no descriptor, principal, identity or path.</summary>
public sealed record MirrorPulseCancellationObjectObservation(MirrorPulseCancellationObjectScope Scope,
    MirrorPulseCancellationObjectOrigin Origin, MirrorPulseCancellationDescriptorDifference Difference,
    MirrorPulseCancellationRoleReference CurrentExecutionRoleReference);

public static class MirrorPulseRootLabelCancellationObjectDiagnostics
{
    private static readonly object ObjectKey = new();

    public static void Mark(Exception failure, MirrorPulseCancellationObjectObservation observation)
    {
        ArgumentNullException.ThrowIfNull(failure); ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.Scope) || !Enum.IsDefined(observation.Origin) || !Enum.IsDefined(observation.Difference) ||
            !Enum.IsDefined(observation.CurrentExecutionRoleReference)) throw new ArgumentOutOfRangeException(nameof(observation));
        if (failure.Data[ObjectKey] is not MirrorPulseCancellationObjectObservation) failure.Data[ObjectKey] = observation;
    }

    internal static IReadOnlyList<LogField> Fields(Exception failure)
        => failure.Data[ObjectKey] is MirrorPulseCancellationObjectObservation observation
            ? [new("rootLabelCancellationObjectScope", observation.Scope.ToString()),
               new("rootLabelCancellationObjectOrigin", observation.Origin.ToString()),
               new("rootLabelCancellationDescriptorDifference", observation.Difference.ToString()),
               new("rootLabelCancellationCurrentRoleReference", observation.CurrentExecutionRoleReference.ToString())]
            : [];
}
