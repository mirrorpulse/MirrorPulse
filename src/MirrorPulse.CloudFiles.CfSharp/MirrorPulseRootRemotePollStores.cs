using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Separates stable root observations from the legacy instance-only snapshot.</summary>
public interface IMirrorPulseRootRemotePollSnapshotStore : IMirrorPulseRemotePollSnapshotStore
{
    ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(RootRegistration root, CancellationToken token = default);
    ValueTask SaveAsync(RootRegistration root, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot, CancellationToken token = default);
    ValueTask ClearLegacyAsync(InstanceId instance, CancellationToken token = default);
}

public interface IMirrorPulseRootRemotePollPendingStore : IMirrorPulseRemotePollPendingStore
{
    ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(RootRegistration root, CancellationToken token);
    ValueTask SaveAsync(RootRegistration root, MirrorPulsePendingRemotePoll pending, CancellationToken token);
    ValueTask ClearAsync(RootRegistration root, string batchId, CancellationToken token);
}

public sealed partial class MirrorPulseFileRemotePollSnapshotStore
{
    private sealed record RootSnapshot(int Version, string InstanceId, string RootId, string RootKey,
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Entries);

    public async ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(RootRegistration root, CancellationToken token = default)
    {
        string path = RootPath(root);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumSnapshotBytes) throw new InvalidDataException("The root polling snapshot is too large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var snapshot = await JsonSerializer.DeserializeAsync<RootSnapshot>(stream, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root polling snapshot is empty.");
        if (snapshot.Version != 1 || snapshot.InstanceId != root.InstanceId.ToString() || snapshot.RootId != root.RootId.ToString() ||
            snapshot.RootKey != root.UniquenessKey || snapshot.Entries is null)
            throw new InvalidDataException("The snapshot belongs to another stable root mapping.");
        Validate(snapshot.Entries);
        return new ReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>(new Dictionary<string, MirrorPulseRemoteSnapshotEntry>(snapshot.Entries, StringComparer.Ordinal));
    }

    public async ValueTask SaveAsync(RootRegistration root, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); Validate(snapshot);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new RootSnapshot(1, root.InstanceId.ToString(), root.RootId.ToString(), root.UniquenessKey, snapshot), JsonOptions);
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("The root polling snapshot is too large.");
        string path = RootPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ValueTask ClearLegacyAsync(InstanceId instance, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); File.Delete(GetPath(instance)); return ValueTask.CompletedTask;
    }

    private string RootPath(RootRegistration root) => Path.Combine(_rootPath, root.InstanceId.ToString(), root.RootId + ".json");
}

public sealed partial class MirrorPulseCatalogRemotePollPendingStore
{
    private sealed record RootPayload(int Version, string InstanceId, string RootId, string RootKey, MirrorPulsePendingRemotePoll Pending);

    public async ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(RootRegistration root, CancellationToken token)
    {
        var record = await _catalog.ReadRootPendingRemoteBatchAsync(root.InstanceId, root.RootId, token).ConfigureAwait(false);
        if (record is null) return null;
        var payload = JsonSerializer.Deserialize<RootPayload>(record.Payload, Options)
            ?? throw new InvalidDataException("The root polling intent is empty.");
        var pending = payload.Pending;
        if (payload.Version != 1 || payload.InstanceId != root.InstanceId.ToString() || payload.RootId != root.RootId.ToString() ||
            payload.RootKey != root.UniquenessKey || pending is null || pending.BatchId != record.BatchId)
            throw new InvalidDataException("The original polling intent belongs to another root mapping.");
        ValidateRoot(root, pending);
        byte[] candidate = JsonSerializer.SerializeToUtf8Bytes(pending.Candidate, Options);
        if (!candidate.AsSpan().SequenceEqual(record.CandidateSnapshot)) throw new InvalidDataException("The retained candidate changed independently of its intent.");
        return pending;
    }

    public async ValueTask SaveAsync(RootRegistration root, MirrorPulsePendingRemotePoll pending, CancellationToken token)
    {
        ValidateRoot(root, pending);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new RootPayload(1, root.InstanceId.ToString(), root.RootId.ToString(), root.UniquenessKey, pending), Options);
        byte[] candidate = JsonSerializer.SerializeToUtf8Bytes(pending.Candidate, Options);
        await _catalog.SaveRootPendingRemoteBatchAsync(new(root.InstanceId, pending.BatchId, payload, candidate) { RootId = root.RootId }, token).ConfigureAwait(false);
    }

    public ValueTask ClearAsync(RootRegistration root, string batchId, CancellationToken token) =>
        new(_catalog.ClearRootPendingRemoteBatchAsync(root.InstanceId, root.RootId, batchId, token));

    private static void ValidateRoot(RootRegistration root, MirrorPulsePendingRemotePoll pending)
    {
        ArgumentNullException.ThrowIfNull(pending); Validate(pending);
        if (pending.ProjectionVersion != 3 || pending.RootId != root.RootId || pending.RootKey != root.UniquenessKey ||
            !pending.BatchId.StartsWith(MirrorPulseRemotePollBatchScope.Prefix(root), StringComparison.Ordinal))
            throw new InvalidDataException("The polling batch does not identify its original stable root.");
    }
}

internal sealed class MirrorPulsePollRootIdConverter : JsonConverter<RootId>
{
    public override RootId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => RootId.Parse(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, RootId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
