using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.CloudFiles;

/// <summary>Carries one bounded Cloud Files range request to the selected Adapter Worker.</summary>
public sealed record MirrorPulseWorkerReadRangeRequest(
    InstanceId InstanceId,
    string NormalizedPath,
    ReadOnlyMemory<byte> FileIdentity,
    long Offset,
    long Length,
    string? RootKey = null);

public interface IMirrorPulseWorkerRangeTransport
{
    ValueTask<Stream> ReadRangeAsync(
        MirrorPulseWorkerReadRangeRequest request,
        CancellationToken cancellationToken);
}

public sealed record MirrorPulseWorkerUploadRequest(
    InstanceId InstanceId,
    string NormalizedPath,
    string? ExpectedRevision,
    Stream Content,
    long Length,
    Guid? OperationId = null,
    string? ExpectedContentSha256 = null);

public interface IMirrorPulseWorkerUploadTransport
{
    ValueTask<string> UploadAsync(
        MirrorPulseWorkerUploadRequest request,
        CancellationToken cancellationToken);
}

public sealed record MirrorPulseWorkerStatRequest(InstanceId InstanceId, string NormalizedPath, string? RootKey = null);

public interface IMirrorPulseWorkerStatTransport
{
    ValueTask<string?> StatAsync(
        MirrorPulseWorkerStatRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Carries one bounded remote-directory page request to an Adapter Worker.</summary>
public sealed record MirrorPulseWorkerDirectoryPageRequest(
    InstanceId InstanceId,
    string NormalizedPath,
    ReadOnlyMemory<byte> ContinuationCursor,
    int PageSize,
    string? RootKey = null);

/// <summary>Represents one remote entry returned by an Adapter Worker directory page.</summary>
public sealed record MirrorPulseWorkerDirectoryEntry(
    string RemoteId,
    string RemoteRevision,
    string ItemKind,
    string RelativePath,
    long? Length,
    DateTimeOffset? CreationTime,
    DateTimeOffset? LastWriteTime,
    bool IsDeleted);

/// <summary>One opaque-cursor directory page returned by an Adapter Worker.</summary>
public sealed record MirrorPulseWorkerDirectoryPage(
    IReadOnlyList<MirrorPulseWorkerDirectoryEntry> Entries,
    ReadOnlyMemory<byte> ContinuationCursor,
    bool IsComplete);

public interface IMirrorPulseWorkerDirectoryPageSource
{
    ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(
        MirrorPulseWorkerDirectoryPageRequest request,
        CancellationToken cancellationToken);
}
