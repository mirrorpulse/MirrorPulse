using System.Text.Json;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Conflicts;

/// <summary>Retains both complete private byte sources until the caller finishes its independently authorized application.</summary>
public sealed class MirrorPulseConflictKeepRemoteCopies : IAsyncDisposable
{
    internal MirrorPulseConflictKeepRemoteCopies(MirrorPulseConflictKeepRemotePreparation preparation,
        MirrorPulseConflictLocalCopyLease? local, MirrorPulseConflictRemoteCopyLease remote)
    { Preparation = preparation; Local = local; Remote = remote; }
    public MirrorPulseConflictKeepRemotePreparation Preparation { get; }
    public MirrorPulseConflictLocalCopyLease? Local { get; }
    public MirrorPulseConflictRemoteCopyLease Remote { get; }
    public ValueTask DisposeAsync() => new(MirrorPulseHostResourceCleanup.RunAsync(
        [Remote.DisposeAsync, () => Local?.DisposeAsync() ?? ValueTask.CompletedTask]));
}

/// <summary>Commits local preservation before staged remote bytes and holds both verified handles for the native caller.</summary>
/// <remarks>Runs as the normal user. This never mutates the sync root, applies remote changes, acknowledges journal IDs or resolves conflicts.</remarks>
public sealed class MirrorPulseConflictKeepRemotePreserver(MirrorPulseProductCatalog catalog,
    MirrorPulseConflictCopyStore copies, MirrorPulseConflictRemoteCopyStore remoteCopies)
{
    public async Task<MirrorPulseConflictKeepRemoteCopies> RetainAsync(Guid commandId,
        Func<MirrorPulseConflictKeepRemoteIntent, CancellationToken, ValueTask<Stream>> openOriginalLocal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openOriginalLocal);
        var original = await catalog.ReadConflictKeepRemoteAsync(commandId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original KeepRemote decision is missing.");
        var intent = original.Intent;
        var conflict = await catalog.ReadUploadConflictAsync(intent.ConflictId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected KeepRemote conflict disappeared.");
        MirrorPulseConflictLocalCopyLease? local = null;
        MirrorPulseConflictRemoteCopyLease? remote = null;
        try
        {
            if (intent.LocalContent is { } content)
            {
                if (original.LocalPreservation is null)
                    _ = await copies.PreserveDecisionAsync(commandId, conflict, MirrorPulseConflictPreservedSide.Local,
                        stop => openOriginalLocal(intent, stop), cancellationToken).ConfigureAwait(false);
                // An existing catalog receipt never authorizes reopening a changed live source
                // or silently recreating a deleted committed copy.
                local = await copies.OpenLocalDecisionAsync(commandId, conflict, new(content.Length, content.Sha256), cancellationToken).ConfigureAwait(false);
            }
            await catalog.RecordConflictKeepRemoteLocalPreservationAsync(commandId, new(local?.Proof), cancellationToken).ConfigureAwait(false);
            if (original.RemoteCopy is null)
                _ = await remoteCopies.PreserveAsync(commandId, conflict, intent.RemoteSource, cancellationToken).ConfigureAwait(false);
            remote = await copies.OpenRemoteDecisionAsync(commandId, conflict, intent.RemoteSource, cancellationToken).ConfigureAwait(false);
            await catalog.RecordConflictKeepRemoteRemoteCopyAsync(commandId, remote.Proof, cancellationToken).ConfigureAwait(false);
            var retained = await catalog.ReadConflictKeepRemoteAsync(commandId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retained KeepRemote decision disappeared.");
            if (JsonSerializer.Serialize(retained.Intent) != JsonSerializer.Serialize(intent) ||
                retained.LocalPreservation is null || retained.LocalPreservation.Copy != local?.Proof || retained.RemoteCopy != remote.Proof)
                throw new InvalidDataException("The selected original group or complete copy receipts changed before application.");
            return new(retained, local, remote);
        }
        catch (Exception failure)
        {
            await MirrorPulseHostResourceCleanup.RunAsync(
                [() => remote?.DisposeAsync() ?? ValueTask.CompletedTask, () => local?.DisposeAsync() ?? ValueTask.CompletedTask], failure).ConfigureAwait(false);
            throw;
        }
    }
}
