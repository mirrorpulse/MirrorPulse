using System.Text.Json.Serialization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

public enum MirrorPulseWorkerChangeKind
{
    Create,
    ContentUpdate,
    MetadataUpdate,
    Move,
    Delete,
}

/// <summary>
/// One idempotent local journal operation delivered to an Adapter Worker. File bytes are read
/// on demand through the authorized source path and are never embedded in this command.
/// </summary>
public sealed record MirrorPulseWorkerChangeCommand(
    Guid OperationId,
    long Sequence,
    InstanceId InstanceId,
    string RootKey,
    MirrorPulseWorkerChangeKind Kind,
    string RelativePath,
    string? PreviousRootKey,
    string? PreviousRelativePath,
    bool IsDirectory,
    Guid? ItemId,
    DateTimeOffset ObservedAt)
{
    /// <summary>The original source instance of a cross-instance move; absent legacy values use InstanceId.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstanceId? PreviousInstanceId { get; init; }
}
