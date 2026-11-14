namespace MirrorPulse.Core.Host;

/// <summary>Preserves only a recognized Worker failure code, never its free-form response.</summary>
public sealed class AdapterWorkerOperationException : IOException
{
    internal string? DirectoryPhase { get; }

    public AdapterWorkerOperationException(string? code) : base("The Adapter Worker operation failed.")
    {
        FailureCode = code is "Offline" or "Disconnected" or "InvalidRequest" or "AccessDenied" or
            "SourceUnavailable" or "CapabilityUnavailable" or "LocalIoFailure" or "RetryableTransferFailure" or "RemoteConflict" or "DirectoryNotEmpty" or "UncorrelatedResponse" or "InvalidConfiguration" or "WorkerFailure"
            ? code : "Unknown";
    }

    internal AdapterWorkerOperationException(string? code, bool directoryAdmission) : this(code)
    {
        DirectoryPhase = directoryAdmission ? "Admission" : "Delete";
        if (FailureCode == "Unknown" && IsDirectoryFailureCode(code))
            FailureCode = code!;
    }

    internal static bool IsDirectoryFailureCode(string? code) => code is
        "AtomicDirectoryNativeFailure" or "AtomicDirectoryJournalVolumeInvalid" or
        "AtomicDirectoryJournalBoundsInvalid" or "AtomicDirectoryJournalRangeLost" or
        "AtomicDirectoryJournalRangeExceeded" or "AtomicDirectoryJournalCursorInvalid" or
        "AtomicDirectoryJournalRecordInvalid" or "AtomicDirectorySourceOrStateGenerationChanged" or
        "OriginalAtomicDirectoryHistoryChanged" or "OriginalAtomicDirectoryGenerationChanged" or
        "OriginalAtomicDirectoryGenerationOrRevisionChanged" or "OriginalAtomicDirectoryInventoryChanged" or
        "OriginalAtomicDirectoryParentChanged" or "OriginalAtomicDirectoryOperationAlreadyOwned" or
        "OriginalDirectoryObservationChanged" or "OriginalDirectoryRequestConditionChanged" or
        "OriginalDirectoryParentChanged" or "OriginalDirectoryParentJournalRequired" or
        "CompleteOriginalDirectoryOperationsRequired" or "LocalOriginalDirectoryAdmissionChanged" or
        "OriginalBoundDirectoryConditionChanged" or "OriginalBoundDirectoryAdmissionChanged" or
        "RetainedOriginalDirectoryAdmissionRequired";

    public string FailureCode { get; }
}
