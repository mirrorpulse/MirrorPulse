using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Freezes one wire batch's public addresses before SDK access and retains them after completion.</summary>
/// <remarks>The Host's instance scheduler and library owner serialize projection and application.</remarks>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseAdapterRemoteBatchProjector(MirrorPulseProductCatalog catalog, ICloudStateStore store,
    MirrorPulseRootRouter router, MirrorPulseRemoteIdentityResolver resolver)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private sealed record Root(string RootId, string RootKey, string DirectoryName, RootIdentityScope Scope);
    private sealed record Address(Guid ItemId, string RemoteId, CloudRemoteChangeKind Kind, string? PreviousRevision, string? PreviousPath);
    private sealed record Projection(int Version, byte[] LegacyFingerprint, byte[] PublicFingerprint, Root[] Roots, Address[] Addresses);

    public async ValueTask<CloudRemoteChangeBatch> ProjectAsync(InstanceId instance, AdapterRemoteChangeBatch source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(source.Changes);
        if (source.Changes.Count > 65_536) throw new InvalidDataException("The incoming projection has too many entries.");
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(source, Options);
        if (input.Length > 32 * 1024 * 1024) throw new InvalidDataException("The incoming projection input is too large.");
        byte[] sourceHash = SHA256.HashData(input);
        var legacy = MirrorPulseAdapterRemoteBatchMapper.Map(instance, router.Registrations, source);
        var referencedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in source.Changes)
        {
            referencedRoots.Add(change.RootKey);
            if (change.PreviousRelativePath is not null) referencedRoots.Add(change.PreviousRootKey ?? change.RootKey);
        }
        Root[] roots = router.Registrations.Where(root => root.InstanceId == instance && root.State == RootRegistrationState.Active)
            .Where(root => source.Changes.Count == 0 || referencedRoots.Contains(root.UniquenessKey))
            .OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal)
            .Select(root => new Root(root.RootId.ToString(), root.UniquenessKey, root.DirectoryName, root.IdentityScope)).ToArray();
        if (roots.Length is 0 or > 4096) throw new InvalidDataException("The incoming projection requires bounded original active root mappings.");
        var retained = await catalog.ReadAdapterRemoteProjectionAsync(instance, legacy.BatchId, token).ConfigureAwait(false);
        if (retained is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(retained.SourceFingerprint, sourceHash))
                throw new InvalidDataException("The incoming batch changed its original wire input.");
            var replay = Recreate(retained.Payload, legacy, roots);
            ValidateAddresses(instance, source, replay); return replay;
        }

        bool preserveLegacy = false;
        var pending = await catalog.ReadRetainedRemoteBatchAsync(instance, token).ConfigureAwait(false);
        if (pending is not null)
        {
            if (pending.BatchId != legacy.BatchId) throw new InvalidOperationException("The original incoming batch must converge before another projection.");
            var original = MirrorPulseRetainedRemoteBatchCoordinator.ReadOriginalBatch(pending);
            if (!original.Fingerprint.Span.SequenceEqual(legacy.Fingerprint.Span))
                throw new InvalidDataException("The pre-upgrade incoming batch cannot change its original public projection.");
            preserveLegacy = true;
        }
        await using (var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var progress = await transaction.RemoteBatches.GetAsync(legacy.BatchId, token).ConfigureAwait(false);
            if (progress is not null)
            {
                if (progress.TotalEntryCount != legacy.Changes.Count || !progress.Fingerprint.Span.SequenceEqual(legacy.Fingerprint.Span))
                    throw new InvalidDataException("The pre-upgrade SDK batch cannot be rebound to a later generation.");
                preserveLegacy = true;
            }
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        Address[] addresses = preserveLegacy ? legacy.Changes.Select(AddressOf).ToArray() :
            await SelectAddressesAsync(instance, source, legacy, token).ConfigureAwait(false);
        var projected = ReplaceAddresses(legacy, addresses);
        ValidateAddresses(instance, source, projected);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Projection(1, legacy.Fingerprint.ToArray(), projected.Fingerprint.ToArray(), roots, addresses), Options);
        await catalog.RetainAdapterRemoteProjectionAsync(new(instance, legacy.BatchId, sourceHash, payload), token).ConfigureAwait(false);
        return projected;
    }

    private async Task<Address[]> SelectAddressesAsync(InstanceId instance, AdapterRemoteChangeBatch source, CloudRemoteChangeBatch legacy, CancellationToken token)
    {
        var addresses = new Address[source.Changes.Count];
        var selected = new Dictionary<(RootId, string), (CloudPlaceholderIdentity Identity, bool Birth)>();
        for (int ordinal = 0; ordinal < source.Changes.Count; ordinal++)
        {
            var wire = source.Changes[ordinal]; var original = legacy.Changes[ordinal];
            var root = router.GetRegistration(instance, wire.RootKey); var key = (root.RootId, wire.RemoteId);
            bool first = !selected.TryGetValue(key, out var current);
            if (first)
            {
                var identity = wire.Kind == AdapterRemoteChangeKind.Delete
                    ? await resolver.ReadCurrentAsync(root, wire.RemoteId, wire.RemoteRevision, token).ConfigureAwait(false)
                    : await resolver.ResolveAsync(root, wire.RemoteId, wire.RemoteRevision, token).ConfigureAwait(false);
                bool birth = false;
                if (MirrorPulseGenerationalPlaceholderIdentity.TryDecode(instance, identity, out _))
                {
                    await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
                    var existing = await transaction.Items.GetByItemIdAsync(identity.ItemId, token).ConfigureAwait(false);
                    if (existing?.IsTombstone == true) throw new InvalidOperationException("A retired public generation requires its original retirement reconciliation.");
                    birth = existing is null;
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                }
                current = (identity, birth); selected.Add(key, current);
            }
            bool newBirth = first && current.Birth && wire.Kind != AdapterRemoteChangeKind.Delete;
            if (newBirth && wire.ItemKind == AdapterRemoteItemKind.File && wire.Length is null)
                throw new InvalidDataException("A replacement file generation requires a complete upsert observation.");
            addresses[ordinal] = new(current.Identity.ItemId, current.Identity.RemoteId,
                newBirth ? wire.ItemKind == AdapterRemoteItemKind.Directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert : original.Kind,
                newBirth ? null : original.PreviousRemoteRevision, newBirth ? null : original.PreviousRelativePath);
        }
        return addresses;
    }

    private static Address AddressOf(CloudRemoteChange change) => new(change.ItemId!.Value, change.RemoteId,
        change.Kind, change.PreviousRemoteRevision, change.PreviousRelativePath);

    private void ValidateAddresses(InstanceId instance, AdapterRemoteChangeBatch source, CloudRemoteChangeBatch projected)
    {
        for (int ordinal = 0; ordinal < source.Changes.Count; ordinal++)
        {
            var wire = source.Changes[ordinal]; var change = projected.Changes[ordinal];
            var identity = new CloudPlaceholderIdentity(change.ItemId!.Value, change.RemoteId, change.RemoteRevision);
            if (!MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(router.GetRegistration(instance, wire.RootKey), wire.RemoteId, identity))
                throw new InvalidDataException("The incoming projection changed its original backend object or stable root.");
        }
    }

    private static CloudRemoteChangeBatch Recreate(byte[] payload, CloudRemoteChangeBatch legacy, Root[] roots)
    {
        try
        {
            var projection = JsonSerializer.Deserialize<Projection>(payload, Options) ?? throw new InvalidDataException("The retained projection is empty.");
            if (projection.Version != 1 || projection.Addresses is null || projection.Roots is null ||
                projection.LegacyFingerprint is not { Length: 32 } || projection.PublicFingerprint is not { Length: 32 } ||
                !projection.LegacyFingerprint.AsSpan().SequenceEqual(legacy.Fingerprint.Span) || !projection.Roots.SequenceEqual(roots) ||
                !JsonSerializer.SerializeToUtf8Bytes(projection, Options).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The retained projection changed its original input, format or root mapping.");
            var result = ReplaceAddresses(legacy, projection.Addresses);
            if (!projection.PublicFingerprint.AsSpan().SequenceEqual(result.Fingerprint.Span))
                throw new InvalidDataException("The incoming projection cannot recreate its original public fingerprint.");
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("The incoming projection cannot be reconstructed safely.", error); }
    }

    private static CloudRemoteChangeBatch ReplaceAddresses(CloudRemoteChangeBatch legacy, Address[] addresses)
    {
        if (addresses.Length != legacy.Changes.Count) throw new InvalidDataException("The incoming projection changed its entry count.");
        var changes = legacy.Changes.Select((change, ordinal) =>
        {
            var address = addresses[ordinal];
            return new CloudRemoteChange(change.ChangeId, address.Kind, address.RemoteId, change.RemoteRevision, change.ItemKind,
                change.RelativePath, address.ItemId, address.PreviousRevision, address.PreviousPath, change.Length, change.Metadata, change.CursorAfter);
        }).ToArray();
        return new(legacy.BatchId, legacy.InitialCursor, changes, legacy.FinalCursor);
    }
}
