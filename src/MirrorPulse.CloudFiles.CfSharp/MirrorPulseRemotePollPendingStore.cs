using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Immutable observations needed to recreate exactly the same CfSharp batch.</summary>
public sealed record MirrorPulsePendingRemotePoll(
    string BatchId, byte[] Fingerprint, string RootDirectoryName,
    IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Previous,
    IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Candidate, int ProjectionVersion = 1);

public interface IMirrorPulseRemotePollPendingStore
{
    ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(InstanceId instanceId, CancellationToken cancellationToken);
    ValueTask SaveAsync(InstanceId instanceId, MirrorPulsePendingRemotePoll pending, CancellationToken cancellationToken);
    ValueTask ClearAsync(InstanceId instanceId, string batchId, CancellationToken cancellationToken);
}

/// <summary>Stores product replay intent in the Host catalog, without CfSharp private tables.</summary>
public sealed class MirrorPulseCatalogRemotePollPendingStore : IMirrorPulseRemotePollPendingStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly MirrorPulseProductCatalog _catalog;
    public MirrorPulseCatalogRemotePollPendingStore(MirrorPulseProductCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async ValueTask SaveAsync(InstanceId instanceId, MirrorPulsePendingRemotePoll pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        Validate(pending);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(2, pending.BatchId,
            pending.Fingerprint, pending.RootDirectoryName, pending.Previous, pending.ProjectionVersion), Options);
        byte[] candidate = JsonSerializer.SerializeToUtf8Bytes(pending.Candidate, Options);
        await _catalog.SavePendingRemoteBatchAsync(new(instanceId, pending.BatchId, payload, candidate), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        MirrorPulsePendingRemoteBatchRecord? record = await _catalog.ReadPendingRemoteBatchAsync(instanceId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null) return null;
        Payload payload = JsonSerializer.Deserialize<Payload>(record.Payload, Options)
            ?? throw new InvalidDataException("The pending poll payload is empty.");
        var candidate = JsonSerializer.Deserialize<Dictionary<string, MirrorPulseRemoteSnapshotEntry>>(record.CandidateSnapshot, Options)
            ?? throw new InvalidDataException("The pending poll candidate snapshot is empty.");
        if (payload.SchemaVersion is not (1 or 2) || payload.BatchId != record.BatchId ||
            payload.SchemaVersion == 1 && payload.ProjectionVersion != 1)
            throw new InvalidDataException("The pending poll schema or batch identity is invalid.");
        var pending = new MirrorPulsePendingRemotePoll(payload.BatchId, payload.Fingerprint,
            payload.RootDirectoryName, payload.Previous, candidate, payload.ProjectionVersion);
        Validate(pending);
        return pending;
    }

    public ValueTask ClearAsync(InstanceId instanceId, string batchId, CancellationToken cancellationToken) =>
        new(_catalog.ClearPendingRemoteBatchAsync(instanceId, batchId, cancellationToken));

    private static void Validate(MirrorPulsePendingRemotePoll pending)
    {
        if (pending.ProjectionVersion is not (1 or 2) || pending.Fingerprint is null || pending.Fingerprint.Length != 32 ||
            string.IsNullOrWhiteSpace(pending.RootDirectoryName) || pending.RootDirectoryName is "." or ".." ||
            pending.RootDirectoryName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("The pending poll fingerprint or root directory is invalid.");
        ArgumentNullException.ThrowIfNull(pending.Previous);
        ArgumentNullException.ThrowIfNull(pending.Candidate);
        MirrorPulseFileRemotePollSnapshotStore.Validate(pending.Previous);
        MirrorPulseFileRemotePollSnapshotStore.Validate(pending.Candidate);
    }

    private sealed record Payload(int SchemaVersion, string BatchId, byte[] Fingerprint, string RootDirectoryName,
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> Previous, int ProjectionVersion = 1);
}
