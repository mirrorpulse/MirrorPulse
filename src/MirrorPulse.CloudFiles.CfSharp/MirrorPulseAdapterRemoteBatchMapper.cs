using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Translates Adapter wire data once into CfSharp's validated remote batch contract.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public static class MirrorPulseAdapterRemoteBatchMapper
{
    public static CloudRemoteChangeBatch Map(
        InstanceId instanceId,
        IEnumerable<RootRegistration> registrations,
        AdapterRemoteChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Changes);
        var roots = registrations
            .Where(root => root.InstanceId == instanceId && root.State == RootRegistrationState.Active)
            .ToDictionary(root => root.UniquenessKey, StringComparer.Ordinal);
        CloudRemoteChange[] changes = batch.Changes.Select(change =>
        {
            ArgumentNullException.ThrowIfNull(change);
            RootRegistration root = GetRoot(roots, change.RootKey);
            CloudPlaceholderIdentity identity = MirrorPulsePlaceholderIdentity.CreateForRoot(root, change.RemoteId, change.RemoteRevision).ToCfSharp();
            string path = MapPath(root, change.RelativePath);
            string? previousPath = change.PreviousRelativePath is null
                ? null
                : MapPath(GetRoot(roots, change.PreviousRootKey ?? change.RootKey), change.PreviousRelativePath);
            CloudItemKind itemKind = change.ItemKind switch
            {
                AdapterRemoteItemKind.File => CloudItemKind.File,
                AdapterRemoteItemKind.Directory => CloudItemKind.Directory,
                _ => throw new ArgumentOutOfRangeException(nameof(change), "The Adapter item kind is invalid."),
            };
            return new CloudRemoteChange(
                root.IdentityScope == RootIdentityScope.InstanceRoot ? $"{instanceId}/{root.RootId}/{change.ChangeId}" : $"{instanceId}/{change.ChangeId}",
                ToCfSharpKind(change.Kind),
                identity.RemoteId,
                change.RemoteRevision,
                itemKind,
                path,
                identity.ItemId,
                change.PreviousRemoteRevision,
                previousPath,
                change.Length,
                ToCfSharpMetadata(itemKind, change.Metadata),
                change.CursorAfter);
        }).ToArray();

        return new CloudRemoteChangeBatch(
            $"{instanceId}/{batch.BatchId}",
            batch.InitialCursor,
            changes,
            batch.FinalCursor);
    }

    private static RootRegistration GetRoot(
        Dictionary<string, RootRegistration> roots,
        string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return roots.TryGetValue(key, out RootRegistration? root)
            ? root
            : throw new FileNotFoundException($"The Adapter root '{key}' is not active.");
    }

    private static string MapPath(RootRegistration root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Any(segment => segment is "." or ".." or ""))
        {
            throw new InvalidDataException("An Adapter change path must stay within its declared first-level root.");
        }

        return root.DirectoryName + "\\" + path.Replace('/', '\\');
    }

    private static CloudRemoteChangeKind ToCfSharpKind(AdapterRemoteChangeKind kind) => kind switch
    {
        AdapterRemoteChangeKind.FileUpsert => CloudRemoteChangeKind.FileUpsert,
        AdapterRemoteChangeKind.DirectoryUpsert => CloudRemoteChangeKind.DirectoryUpsert,
        AdapterRemoteChangeKind.MetadataUpdate => CloudRemoteChangeKind.MetadataUpdate,
        AdapterRemoteChangeKind.Move => CloudRemoteChangeKind.Move,
        AdapterRemoteChangeKind.Delete => CloudRemoteChangeKind.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static CloudPlaceholderMetadata? ToCfSharpMetadata(
        CloudItemKind kind,
        AdapterRemoteMetadata? source)
    {
        if (source is null)
        {
            return null;
        }

        CloudPlaceholderMetadata.Builder builder = kind == CloudItemKind.Directory
            ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
            : CloudPlaceholderMetadata.CreateFileBuilder();
        builder.WithAttributes(source.Attributes);
        if (source.CreationTime is DateTimeOffset created)
        {
            builder.WithCreationTime(created);
        }

        if (source.LastAccessTime is DateTimeOffset accessed)
        {
            builder.WithLastAccessTime(accessed);
        }

        if (source.LastWriteTime is DateTimeOffset written)
        {
            builder.WithLastWriteTime(written);
        }

        if (source.ChangeTime is DateTimeOffset changed)
        {
            builder.WithChangeTime(changed);
        }

        return builder.Build();
    }
}
