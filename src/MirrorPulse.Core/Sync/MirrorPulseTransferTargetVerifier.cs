using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Verifies a saved original copy receipt using its exact target revision and whole content.</summary>
/// <remarks>Target equality cannot create missing ownership. The Host holds both instance admissions, its
/// namespace actor and current native guards. A retained verification is history; it does not refresh a
/// delete authorization or promise that the remote target can never change after the last observation.</remarks>
public sealed class MirrorPulseTransferTargetVerifier(MirrorPulseProductCatalog catalog, IMirrorPulseWorkerStatTransport stats,
    IMirrorPulseWorkerRangeTransport ranges, IMirrorPulseWorkerDirectoryPageSource directories, TimeProvider? timeProvider = null)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IMirrorPulseWorkerStatTransport _stats = stats ?? throw new ArgumentNullException(nameof(stats));
    private readonly IMirrorPulseWorkerRangeTransport _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
    private readonly IMirrorPulseWorkerDirectoryPageSource _directories = directories ?? throw new ArgumentNullException(nameof(directories));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MirrorPulseNamespaceTransferCopy> VerifyAsync(Guid operationId, CancellationToken token = default)
    {
        var copy = await _catalog.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original transfer is missing.");
        if (copy.TargetVerification is not null) return copy;
        if (copy.CopyAcceptedAt is null || copy.AcceptedRevision is null)
            throw new MirrorPulseMutationAmbiguousException("Target readback cannot recover a missing original upload receipt.");
        copy = await _catalog.ValidateNamespaceTransferTargetReadAsync(operationId, token).ConfigureAwait(false);
        string acceptedRevision = copy.AcceptedRevision ?? throw new InvalidDataException("The validated copy lost its original acceptance receipt.");
        var plan = copy.Plan; var route = plan.TargetRoute;
        string path = plan.TargetRelativePath[(route.DirectoryName.Length + 1)..];
        if (await _stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != copy.AcceptedRevision)
            throw new IOException("The transfer target no longer has its original accepted revision.");
        var before = await ReadTargetMetadataAsync(copy, path, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < plan.Length)
        {
            int length = (int)Math.Min(1_048_576, plan.Length - offset);
            await using var content = await _ranges.ReadRangeAsync(new(route.InstanceId, path, ReadOnlyMemory<byte>.Empty,
                offset, length, route.RootKey)
            { ExpectedRevision = copy.AcceptedRevision }, token).ConfigureAwait(false);
            byte[] buffer = new byte[length];
            await content.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            byte[] excess = new byte[1];
            if (await content.ReadAsync(excess, token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Target readback exceeded its requested range.");
            hash.AppendData(buffer); offset += length;
        }
        if (Convert.ToHexString(hash.GetHashAndReset()) != plan.Sha256)
            throw new IOException("Target bytes differ from the complete original transfer content.");
        var after = await ReadTargetMetadataAsync(copy, path, token).ConfigureAwait(false);
        if (after.RemoteId != before.RemoteId ||
            await _stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != copy.AcceptedRevision)
            throw new IOException("Target identity or revision changed during whole-content readback.");
        return await _catalog.RecordNamespaceTransferTargetVerificationAsync(new(1, operationId, acceptedRevision,
            before.RemoteId, plan.Length, plan.Sha256, _time.GetUtcNow()), token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseWorkerDirectoryEntry> ReadTargetMetadataAsync(MirrorPulseNamespaceTransferCopy copy,
        string path, CancellationToken token)
    {
        var route = copy.Plan.TargetRoute; int separator = path.LastIndexOf('/');
        string parent = separator < 0 ? string.Empty : path[..separator];
        ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal); MirrorPulseWorkerDirectoryEntry? found = null;
        for (int page = 0; page < 128; page++)
        {
            var result = await _directories.ReadDirectoryPageAsync(new(route.InstanceId, parent, cursor, 512, route.RootKey), token).ConfigureAwait(false);
            if (result.Entries is null || result.Entries.Count > 512 || result.ContinuationCursor.Length > 1_048_576)
                throw new InvalidDataException("Target metadata exceeded its bounded page contract.");
            foreach (var entry in result.Entries)
            {
                if (entry is null) throw new InvalidDataException("Target metadata contains an empty entry.");
                if (entry.RelativePath != path) continue;
                if (found is not null) throw new InvalidDataException("More than one remote entry claims the transfer destination.");
                if (entry.IsDeleted || entry.ItemKind != "File" || entry.Length != copy.Plan.Length || entry.RemoteRevision != copy.AcceptedRevision ||
                    string.IsNullOrWhiteSpace(entry.RemoteId) || entry.RemoteId.Length > 4096 || entry.RemoteId.Contains('\0'))
                    throw new IOException("The target is not the original accepted file with the complete expected length.");
                found = entry;
            }
            if (result.IsComplete) return found ?? throw new IOException("The original transfer target is absent.");
            cursor = result.ContinuationCursor;
            if (cursor.IsEmpty || !seen.Add(Convert.ToHexString(SHA256.HashData(cursor.Span))))
                throw new InvalidDataException("Target directory pagination did not advance.");
        }
        throw new InvalidDataException("Target directory verification exceeded its bounded page inventory.");
    }
}
