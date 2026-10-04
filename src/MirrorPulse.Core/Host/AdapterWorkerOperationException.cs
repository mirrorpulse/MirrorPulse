namespace MirrorPulse.Core.Host;

/// <summary>Preserves only a recognized Worker failure code, never its free-form response.</summary>
public sealed class AdapterWorkerOperationException : IOException
{
    public AdapterWorkerOperationException(string? code) : base("The Adapter Worker operation failed.")
    {
        FailureCode = code is "Offline" or "Disconnected" or "InvalidRequest" or "AccessDenied" or
            "SourceUnavailable" or "CapabilityUnavailable" or "LocalIoFailure" or "RetryableTransferFailure" or "RemoteConflict" or "UncorrelatedResponse" or "InvalidConfiguration" or "WorkerFailure"
            ? code : "Unknown";
    }

    public string FailureCode { get; }
}
