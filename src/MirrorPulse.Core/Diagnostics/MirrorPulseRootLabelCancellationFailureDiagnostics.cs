using System.Globalization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseRootLabelCancellationStage
{
    Unknown, RootWorkDrain, InstanceLibrary, OriginalOwnership, PendingPermissionRecovery, OriginalCancellation,
    MetadataInventory, AnchorMetadata, AnchorInspection, MemberInspection, PublicReconciliation, MemberReinspection,
    FinalNativeObservations, CatalogCancellation, CurrentTreeRecovery, CurrentTreeAudit,
    MemberProvenance, MemberMetadata, MemberNativeObject, MemberPublicIdentity, MemberSealedObject, MemberInventory,
    OriginalRootIdentity, ObjectSnapshot, ObjectNativeBinding, ObjectRetainedBinding, ObjectOwner, ObjectRoot,
    ObjectPath, ObjectKind, ObjectLinkCount, ObjectRetainedDescriptor, ObjectDescriptorPolicy
}

/// <summary>Observes fixed cancellation boundaries while retaining the original operation and exception.</summary>
public static class MirrorPulseRootLabelCancellationFailureDiagnostics
{
    private static readonly object StageKey = new();

    public static void MarkStage(Exception failure, MirrorPulseRootLabelCancellationStage stage)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (Stage(failure) == MirrorPulseRootLabelCancellationStage.Unknown) failure.Data[StageKey] = stage;
    }

    public static MirrorPulseRootLabelCancellationStage Stage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Data[StageKey] is MirrorPulseRootLabelCancellationStage stage && Enum.IsDefined(stage)
            ? stage : MirrorPulseRootLabelCancellationStage.Unknown;
    }

    public static async ValueTask<T> AtStageAsync<T>(MirrorPulseRootLabelCancellationStage stage, Func<ValueTask<T>> action)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(action);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            MarkStage(original, stage); throw;
        }
    }

    public static async ValueTask<T> RunAsync<T>(Guid operationId, Func<ValueTask<T>> action, Func<LogEntry, ValueTask> observe)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(observe);
        try { return await action().ConfigureAwait(false); }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            try
            {
                await observe(new(LogLevel.Warning, "host", "RootLabelCancellationFailed", DateTimeOffset.UtcNow,
                    [new("operationId", operationId.ToString("D")),
                     new("rootLabelFailureBoundary", MirrorPulseRootLabelFailureDiagnostics.Classify(original).ToString()),
                     new("rootLabelCancellationStage", Stage(original).ToString()),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(original)),
                     new("hresult", original.HResult.ToString("X8", CultureInfo.InvariantCulture))])).ConfigureAwait(false);
            }
            catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException) { }
            throw;
        }
    }
}
