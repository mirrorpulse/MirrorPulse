namespace MirrorPulse.Core.State;

/// <summary>The public SDK generation and full native binding retained before the first source deletion.</summary>
/// <remarks>The SDK's nullable Int64 file identifier is retained verbatim. It is not a replacement for
/// the native volume, sync-root and file binding. Raw SDK path spelling is also retained.</remarks>
public sealed record MirrorPulseNamespaceTransferSourceProjection(int Version, Guid OperationId, Guid ItemId,
    string RemoteId, string RelativePath, string? RemoteRevision, long? OfficialLocalFileId,
    MirrorPulseLocalFileBinding LocalObject, DateTimeOffset CapturedAt);

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateTransferSourceProjection(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceRetirement retirement, MirrorPulseNamespaceTransferSourceProjection projection, DateTimeOffset startedAt)
    {
        var source = copy.Plan.Source;
        if (projection.Version != 1 || projection.OperationId != copy.Plan.OperationId || projection.ItemId != source.Identity.ItemId ||
            projection.RemoteId != source.Identity.RemoteId || projection.RelativePath is null ||
            projection.RelativePath.Length > 32_767 || projection.RelativePath.Replace('\\', '/') != source.Context.Location.RelativePath ||
            projection.RemoteRevision != copy.Plan.SourceRemoteRevision || projection.LocalObject != source.Context.Member.LocalObject ||
            projection.CapturedAt < retirement.Intent.PreparedAt || projection.CapturedAt > startedAt)
            throw new InvalidDataException("Source retirement requires the original pre-deletion public SDK generation and full native binding.");
    }
}
