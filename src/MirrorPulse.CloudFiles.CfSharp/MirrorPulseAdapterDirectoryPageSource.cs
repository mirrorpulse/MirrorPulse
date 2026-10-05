using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Adapts the Worker directory-page DTO to CfSharp's remote catalog contract.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseAdapterDirectoryPageSource : IMirrorPulseDirectoryPageSource
{
    private readonly IMirrorPulseWorkerDirectoryPageSource _source;

    public MirrorPulseAdapterDirectoryPageSource(IMirrorPulseWorkerDirectoryPageSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
        InstanceId instanceId,
        string normalizedPath,
        ReadOnlyMemory<byte> continuationCursor,
        int pageSize,
        CancellationToken cancellationToken) => await ReadPageCoreAsync(instanceId, null, normalizedPath,
            continuationCursor, pageSize, cancellationToken).ConfigureAwait(false);

    public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string rootKey,
        string normalizedPath, ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken) =>
        ReadPageCoreAsync(instanceId, rootKey, normalizedPath, continuationCursor, pageSize, cancellationToken);

    private async ValueTask<CloudRemoteDirectoryPage> ReadPageCoreAsync(InstanceId instanceId, string? rootKey,
        string normalizedPath, ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken)
    {
        MirrorPulseWorkerDirectoryPage page = await _source.ReadDirectoryPageAsync(
            new MirrorPulseWorkerDirectoryPageRequest(instanceId, normalizedPath,
                continuationCursor, pageSize, rootKey), cancellationToken).ConfigureAwait(false);

        var entries = new List<CloudRemoteDirectoryEntry>(page.Entries.Count);
        foreach (MirrorPulseWorkerDirectoryEntry entry in page.Entries)
        {
            CloudItemKind kind = entry.ItemKind switch
            {
                "file" or "File" => CloudItemKind.File,
                "directory" or "Directory" => CloudItemKind.Directory,
                _ => throw new InvalidDataException($"The Worker returned unknown item kind '{entry.ItemKind}'."),
            };
            CloudPlaceholderMetadata.Builder metadataBuilder = kind == CloudItemKind.Directory
                ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
                : CloudPlaceholderMetadata.CreateFileBuilder();
            if (entry.CreationTime is { } creation)
            {
                metadataBuilder.WithCreationTime(creation);
            }

            if (entry.LastWriteTime is { } lastWrite)
            {
                metadataBuilder.WithLastWriteTime(lastWrite);
            }

            CloudPlaceholderMetadata metadata = metadataBuilder.Build();

            entries.Add(new CloudRemoteDirectoryEntry(entry.RemoteId, entry.RemoteRevision, kind,
                entry.RelativePath, null, entry.Length, metadata, entry.IsDeleted));
        }

        return new CloudRemoteDirectoryPage(entries, page.ContinuationCursor, page.IsComplete);
    }
}
