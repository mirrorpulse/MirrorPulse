using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains the actual SDK apply or metadata-only reconciliation outcome for a product batch.</summary>
/// <remarks>A processed conflict advances batch progress without certifying native deletion or backend acceptance.</remarks>
public sealed class MirrorPulseRemoteBatchResult
{
    private readonly byte[] _safeCursor;

    private MirrorPulseRemoteBatchResult(CloudRemoteApplyResult result)
    {
        SdkApplyResult = result;
        Entries = result.Entries;
        Status = result.Status;
        _safeCursor = result.SafeCursor.ToArray();
        RequiresRetry = result.RequiresRetry;
        AppliedEntryCount = result.AppliedEntryCount;
        ConflictIds = result.ConflictIds;
    }

    private MirrorPulseRemoteBatchResult(CloudRemoteDeleteReconciliationResult result)
    {
        DeleteReconciliation = result;
        Entries = Array.AsReadOnly(new[] { result.Entry! });
        Status = result.BatchStatus!.Value;
        _safeCursor = result.SafeCursor.ToArray();
        AppliedEntryCount = result.AppliedEntryCount!.Value;
        ConflictIds = Array.AsReadOnly(new[] { result.ConflictId!.Value });
    }

    public CloudRemoteApplyResult? SdkApplyResult { get; }
    public CloudRemoteDeleteReconciliationResult? DeleteReconciliation { get; }
    public IReadOnlyList<CloudRemoteApplyEntryResult> Entries { get; }
    public CloudRemoteBatchStatus Status { get; }
    public ReadOnlyMemory<byte> SafeCursor => _safeCursor;
    public bool RequiresRetry { get; }
    public int AppliedEntryCount { get; }
    public IReadOnlyList<Guid> ConflictIds { get; }

    internal static MirrorPulseRemoteBatchResult FromSdk(CloudRemoteApplyResult result) => new(result);

    internal static MirrorPulseRemoteBatchResult FromReconciliation(CloudRemoteChangeBatch batch, int ordinal,
        CloudRemoteDeleteReconciliationResult result)
    {
        if (result.Status is not (CloudRemoteDeleteReconciliationStatus.RecordedConflict or CloudRemoteDeleteReconciliationStatus.AlreadyRecorded) ||
            result.NativeMutationAttempted || result.EntryIndex != ordinal || result.AppliedEntryCount != ordinal + 1 ||
            result.BatchStatus != (ordinal + 1 == batch.Changes.Count ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying) ||
            result.ConflictId is null || result.ConflictId == Guid.Empty || result.Entry?.ChangeId != batch.Changes[ordinal].ChangeId ||
            result.Entry.Status != CloudRemoteApplyEntryStatus.Conflict ||
            result.Entry.Conflict?.Reason != CloudRemoteConflictReason.LocalDeleteReconciliationRequired ||
            !MirrorPulseRemoteRetirementBatchCoordinator.Matches(batch, ordinal, result.Entry.Conflict.Change) ||
            !result.SafeCursor.Span.SequenceEqual(MirrorPulseRemoteBirthBatchCoordinator.PrefixCursor(batch, ordinal + 1).Span))
            throw new InvalidDataException("The SDK reconciliation did not return the exact original committed rejection.");
        return new(result);
    }
}
