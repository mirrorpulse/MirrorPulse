namespace MirrorPulse.Control.Contracts;

/// <summary>
/// Stable error codes returned by the versioned control plane.
/// </summary>
public static class MirrorPulseControlErrorCodes
{
    public const string InvalidRequest = "mp.control.invalidRequest";
    public const string UnknownCommand = "mp.control.unknownCommand";
    public const string ProtocolVersionUnsupported = "mp.control.protocolVersionUnsupported";
    public const string FrameTooLarge = "mp.control.frameTooLarge";
    public const string HostUnavailable = "mp.control.hostUnavailable";
    public const string HostExecutableNotFound = "mp.control.hostExecutableNotFound";
    public const string HostPathUntrusted = "mp.control.hostPathUntrusted";
    public const string HostStartFailed = "mp.control.hostStartFailed";
    public const string HostStartTimeout = "mp.control.hostStartTimeout";
    public const string RequestTimeout = "mp.control.requestTimeout";
    public const string HostBusy = "mp.control.hostBusy";
    public const string Unauthorized = "mp.control.unauthorized";
    public const string Forbidden = "mp.control.forbidden";
    public const string OperationNotFound = "mp.control.operationNotFound";
    public const string OperationCancelled = "mp.control.operationCancelled";
    public const string Unsupported = "mp.control.unsupported";
    public const string StorageFailure = "mp.control.storageFailure";
    public const string InternalFailure = "mp.control.internalFailure";
}
