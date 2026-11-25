using System.Collections.ObjectModel;
using System.Security.Cryptography;
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

/// <summary>Stores the completed snapshot and its synthetic cursor as one durable baseline.</summary>
public interface IMirrorPulseRootRemotePollBaselineStore : IMirrorPulseRootRemotePollSnapshotStore
{
    ValueTask<MirrorPulseRemotePollBaseline?> LoadBaselineAsync(RootRegistration root, CancellationToken token = default);
    ValueTask SaveBaselineAsync(RootRegistration root, MirrorPulseRemotePollBaseline baseline, CancellationToken token = default);
}

public sealed record MirrorPulseRemotePollBaseline(
    IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Entries, ReadOnlyMemory<byte> Cursor);

public interface IMirrorPulseRootRemotePollPendingStore : IMirrorPulseRemotePollPendingStore
{
    ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(RootRegistration root, CancellationToken token);
    ValueTask SaveAsync(RootRegistration root, MirrorPulsePendingRemotePoll pending, CancellationToken token);
    ValueTask ClearAsync(RootRegistration root, string batchId, CancellationToken token);
}

public sealed partial class MirrorPulseFileRemotePollSnapshotStore
{
    private sealed record RootSnapshot(int Version, string InstanceId, string RootId, string RootKey,
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Entries,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? Cursor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null);

    public async ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(RootRegistration root, CancellationToken token = default) =>
        (await LoadBaselineAsync(root, token).ConfigureAwait(false))?.Entries;

    public async ValueTask<MirrorPulseRemotePollBaseline?> LoadBaselineAsync(RootRegistration root, CancellationToken token = default)
    {
        string path = RootPath(root);
        RootSnapshot? snapshot;
        try
        {
            snapshot = await ReadRootSnapshotAsync(root, path, token).ConfigureAwait(false);
        }
        catch (MirrorPulsePollBaselineCorruptException)
        {
            var recovery = await ReadRootSnapshotAsync(root, path + ".recovery", token).ConfigureAwait(false);
            if (recovery is not { Version: 3 }) throw;
            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            await ReplaceSnapshotFileAsync(path, JsonSerializer.SerializeToUtf8Bytes(recovery, JsonOptions), token).ConfigureAwait(false);
            snapshot = recovery;
        }
        if (snapshot is null)
        {
            // The original can be absent if recovery was interrupted after preserving it.
            snapshot = await ReadRootSnapshotAsync(root, path + ".recovery", token).ConfigureAwait(false);
            if (snapshot is not { Version: 3 }) return null;
            await ReplaceSnapshotFileAsync(path, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions), token).ConfigureAwait(false);
        }
        return new(new ReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>(new Dictionary<string, MirrorPulseRemoteSnapshotEntry>(snapshot.Entries, StringComparer.Ordinal)),
            snapshot.Cursor ?? Array.Empty<byte>());
    }

    private static async ValueTask<RootSnapshot?> ReadRootSnapshotAsync(RootRegistration root, string path, CancellationToken token)
    {
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using var stream = input;
        if (stream.Length > MaximumSnapshotBytes) throw new MirrorPulsePollBaselineCorruptException();
        RootSnapshot snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<RootSnapshot>(stream, JsonOptions, token).ConfigureAwait(false)
                ?? throw new MirrorPulsePollBaselineCorruptException();
        }
        catch (JsonException error) { throw new MirrorPulsePollBaselineCorruptException(error); }

        if (snapshot.Version is not (1 or 2 or 3)) throw new InvalidDataException("The root polling snapshot format is unsupported.");
        if (snapshot.Entries is null || (snapshot.Version switch
        {
            1 => snapshot.Cursor is not null || snapshot.Sha256 is not null,
            2 => snapshot.Cursor is not { Length: 32 } || snapshot.Sha256 is not null,
            _ => snapshot.Cursor is { Length: not 32 } || snapshot.Sha256 != ComputeIntegrity(snapshot),
        })) throw new MirrorPulsePollBaselineCorruptException();
        if (snapshot.InstanceId != root.InstanceId.ToString() || snapshot.RootId != root.RootId.ToString() || snapshot.RootKey != root.UniquenessKey)
            throw new InvalidDataException("The snapshot belongs to another stable root mapping.");
        try { Validate(snapshot.Entries); }
        catch (InvalidDataException error) { throw new MirrorPulsePollBaselineCorruptException(error); }
        return snapshot;
    }

    internal async ValueTask PreserveCorruptBaselineAsync(RootRegistration root, CancellationToken token)
    {
        string primary = RootPath(root);
        var copies = new[] { primary, primary + ".recovery" };
        var damaged = new List<string>(copies.Length);
        foreach (string path in copies)
        {
            try
            {
                if (await ReadRootSnapshotAsync(root, path, token).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("A stored baseline still requires its original snapshot mapping.");
            }
            catch (MirrorPulsePollBaselineCorruptException) { damaged.Add(path); }
        }
        foreach (string path in damaged)
        {
            token.ThrowIfCancellationRequested();
            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
        }
    }

    public ValueTask SaveAsync(RootRegistration root, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot, CancellationToken token = default) =>
        SaveBaselineAsync(root, new(snapshot, ReadOnlyMemory<byte>.Empty), token);

    public async ValueTask SaveBaselineAsync(RootRegistration root, MirrorPulseRemotePollBaseline baseline, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baseline.Entries);
        Validate(baseline.Entries);
        if (baseline.Cursor.Length is not (0 or 32)) throw new InvalidDataException("The polling baseline cursor has an invalid length.");
        var snapshot = new RootSnapshot(3,
            root.InstanceId.ToString(), root.RootId.ToString(), root.UniquenessKey, baseline.Entries,
            baseline.Cursor.IsEmpty ? null : baseline.Cursor.ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot with { Sha256 = ComputeIntegrity(snapshot) }, JsonOptions);
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("The root polling snapshot is too large.");
        string path = RootPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A durable pending intent precedes both writes and remains authoritative until both commit.
        await ReplaceSnapshotFileAsync(path + ".recovery", bytes, token).ConfigureAwait(false);
        await ReplaceSnapshotFileAsync(path, bytes, token).ConfigureAwait(false);
    }

    private static string ComputeIntegrity(RootSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(snapshot with { Sha256 = null }, JsonOptions)));

    private static async Task ReplaceSnapshotFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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

internal sealed class MirrorPulsePollBaselineCorruptException(Exception? inner = null)
    : IOException("The retained remote polling baseline could not be validated.", inner);

internal sealed class MirrorPulsePollCursorMismatchException()
    : IOException("The root polling baseline and its published cursor disagree.");

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
        if (pending.ProjectionVersion is < 3 or > MirrorPulseActiveRemotePoller.MetadataProjectionVersion || pending.RootId != root.RootId || pending.RootKey != root.UniquenessKey ||
            !pending.BatchId.StartsWith(MirrorPulseRemotePollBatchScope.Prefix(root), StringComparison.Ordinal))
            throw new InvalidDataException("The polling batch does not identify its original stable root.");
    }
}

internal sealed class MirrorPulsePollRootIdConverter : JsonConverter<RootId>
{
    public override RootId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => RootId.Parse(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, RootId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
