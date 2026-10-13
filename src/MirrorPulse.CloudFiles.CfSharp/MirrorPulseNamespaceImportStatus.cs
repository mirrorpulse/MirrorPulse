namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A historical import snapshot, not fresh native authority or remote acceptance.</summary>
public sealed record MirrorPulseNamespaceImportStatus(Guid OperationId, string RelativePath, string Phase,
    long Length, string Sha256, bool Started, bool OriginalBindingRecorded, bool ContentRecorded,
    bool Converted, bool Protected, bool Registered, bool RequiresRecovery);
