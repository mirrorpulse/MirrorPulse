using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Persists the last successfully applied remote poll snapshot outside the CfSharp database.
/// The snapshot is a recovery hint: CfSharp remains authoritative for batch application and
/// safely replays a batch if the process stops between applying it and saving this file.
/// </summary>
public interface IMirrorPulseRemotePollSnapshotStore
{
    ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(
        InstanceId instanceId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        InstanceId instanceId,
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>One durable remote object observation used by the active poller.</summary>
public sealed record MirrorPulseRemoteSnapshotEntry(
    string RemoteId,
    string RemoteRevision,
    CloudItemKind ItemKind,
    string RelativePath,
    long? Length,
    MirrorPulseRemoteSnapshotMetadata Metadata);

/// <summary>JSON-safe projection of CfSharp placeholder metadata.</summary>
public sealed record MirrorPulseRemoteSnapshotMetadata(
    CloudItemKind Kind,
    FileAttributes Attributes,
    DateTimeOffset? CreationTime,
    DateTimeOffset? LastAccessTime,
    DateTimeOffset? LastWriteTime,
    DateTimeOffset? ChangeTime);

/// <summary>Stores each Adapter instance snapshot with replace-on-write semantics.</summary>
public sealed partial class MirrorPulseFileRemotePollSnapshotStore : IMirrorPulseRootRemotePollSnapshotStore
{
    private const int MaximumSnapshotBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _rootPath;

    public MirrorPulseFileRemotePollSnapshotStore(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        _rootPath = Path.Combine(Path.GetFullPath(dataRootPath), "remote-poll");
    }

    public async ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(
        InstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(instanceId);
        if (!File.Exists(path)) return null;

        FileInfo info = new(path);
        if (info.Length > MaximumSnapshotBytes)
            throw new InvalidDataException("The persisted remote poll snapshot is too large.");

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Dictionary<string, MirrorPulseRemoteSnapshotEntry>? entries =
            await JsonSerializer.DeserializeAsync<Dictionary<string, MirrorPulseRemoteSnapshotEntry>>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (entries is null) throw new InvalidDataException("The persisted remote poll snapshot is empty.");
        Validate(entries);
        return new ReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>(entries);
    }

    public async ValueTask SaveAsync(
        InstanceId instanceId,
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        Directory.CreateDirectory(_rootPath);
        string path = GetPath(instanceId);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch { /* The next successful save can remove a stale temporary file. */ }
            }
        }
    }

    private string GetPath(InstanceId instanceId) =>
        Path.Combine(_rootPath, instanceId.ToString() + ".json");

    internal static void Validate(IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> entries)
    {
        if (entries.Count > 1_000_000)
            throw new InvalidDataException("The remote poll snapshot contains too many entries.");
        foreach ((string key, MirrorPulseRemoteSnapshotEntry entry) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(entry.RemoteId) ||
                !string.Equals(key, entry.RemoteId, StringComparison.Ordinal))
                throw new InvalidDataException("The remote poll snapshot contains an invalid object identifier.");
            if (string.IsNullOrWhiteSpace(entry.RemoteRevision) || string.IsNullOrWhiteSpace(entry.RelativePath) ||
                entry.Metadata is null || entry.Metadata.Kind != entry.ItemKind)
                throw new InvalidDataException("The remote poll snapshot contains an incomplete object.");
        }
    }
}
