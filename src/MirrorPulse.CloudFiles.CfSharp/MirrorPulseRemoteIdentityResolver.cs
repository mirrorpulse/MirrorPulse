using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Selects a durable logical generation while leaving backend IDs and historical identities intact.</summary>
/// <remarks>This selects an address only. Native publication, root admission and original journal settlement remain separate.</remarks>
public sealed class MirrorPulseRemoteIdentityResolver(MirrorPulseProductCatalog catalog, TimeProvider? timeProvider = null)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Reads the selected address without treating a delete or historical retry as a new observation.</summary>
    public async ValueTask<CloudPlaceholderIdentity> ReadCurrentAsync(RootRegistration root, string remoteId,
        string? revision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        var original = MirrorPulsePlaceholderIdentity.CreateForRoot(root, remoteId, revision).ToCfSharp();
        var selected = await _catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.ItemId, token).ConfigureAwait(false);
        return selected is null ? original : ValidateCurrent(root, remoteId, revision, original, selected);
    }

    /// <summary>Rejects callbacks for retired generations without allocating a successor from a stale callback.</summary>
    public async ValueTask RequireCurrentAsync(RootRegistration root, CloudPlaceholderIdentity identity, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(identity);
        if (!MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity)) throw new InvalidDataException("The callback belongs to another root.");
        string backend = MirrorPulseGenerationalPlaceholderIdentity.TryDecode(root.InstanceId, identity, out var decoded)
            ? decoded!.RemoteId : root.IdentityScope == RootIdentityScope.LegacyInstance ? identity.RemoteId :
                MirrorPulsePlaceholderIdentity.Decode(root.InstanceId, identity.Encode()).RemoteId;
        var original = MirrorPulsePlaceholderIdentity.CreateForRoot(root, backend, identity.RemoteRevision).ToCfSharp();
        var selected = await _catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.ItemId, token).ConfigureAwait(false);
        var current = selected is null ? original : ValidateCurrent(root, backend, identity.RemoteRevision, original, selected);
        if (current.ItemId != identity.ItemId || current.RemoteId != identity.RemoteId ||
            (await _catalog.ReadNamespaceTransferSourceGenerationsAsync(current.ItemId, token).ConfigureAwait(false)).Count != 0 ||
            (await _catalog.ReadNamespaceRemoteRetirementGenerationsAsync(current.ItemId, token).ConfigureAwait(false)).Count != 0)
            throw new InvalidDataException("A retired callback cannot read a replacement backend generation.");
    }

    public async ValueTask<CloudPlaceholderIdentity> ResolveAsync(RootRegistration root, string remoteId,
        string? revision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        var original = MirrorPulsePlaceholderIdentity.CreateForRoot(root, remoteId, revision).ToCfSharp();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await _catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.ItemId, token).ConfigureAwait(false);
            CloudPlaceholderIdentity predecessor = current is null ? original : ValidateCurrent(root, remoteId, revision, original, current);
            var retired = await _catalog.ReadNamespaceTransferSourceGenerationsAsync(predecessor.ItemId, token).ConfigureAwait(false);
            var remote = await _catalog.ReadNamespaceRemoteRetirementGenerationsAsync(predecessor.ItemId, token).ConfigureAwait(false);
            if (retired.Count + remote.Count == 0) return predecessor;
            if (retired.Count + remote.Count != 1) throw new InvalidDataException("An ambiguous retired public item requires explicit generation reconciliation.");
            var retirement = retired.Count == 1 ? retired[0].Retirement : remote[0];
            Guid transferId = Guid.Empty; long nameEpoch;
            if (retired.Count == 1)
            {
                var copy = retired[0].Transfer;
                if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.TransferSourceNameEpoch is null ||
                    retirement.TransferSourceProjection?.RemoteId != predecessor.RemoteId || copy.Acknowledgement?.AcknowledgedAt is null ||
                    copy.Plan.SourceRoute.RootId != root.RootId || copy.Plan.SourceRoute.InstanceId != root.InstanceId ||
                    copy.Plan.SourceRoute.RootKey != root.UniquenessKey)
                    throw new InvalidOperationException("An unfinished or foreign transfer cannot select a replacement generation.");
                transferId = copy.Plan.OperationId; nameEpoch = retirement.TransferSourceNameEpoch.Value;
            }
            else
            {
                var owner = retirement.Intent.RemoteEntry!;
                if (owner.Version != 2 || retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed ||
                    retirement.RemoteSourceNameEpoch is null || owner.RemoteId != predecessor.RemoteId ||
                    retirement.Intent.RootId != root.RootId || owner.InstanceId != root.InstanceId || owner.RootKey != root.UniquenessKey)
                    throw new InvalidOperationException("An unfinished, legacy or foreign remote deletion cannot select a replacement generation.");
                nameEpoch = retirement.RemoteSourceNameEpoch.Value;
            }
            Guid generation = Guid.NewGuid();
            var successor = MirrorPulseGenerationalPlaceholderIdentity.Create(root, remoteId, generation, revision);
            var selection = new MirrorPulseRemoteIdentityGeneration(retired.Count == 1 ? 1 : 2, root.RootId, root.InstanceId, root.UniquenessKey,
                original.ItemId, original.RemoteId, current?.Generation, generation, predecessor.ItemId, predecessor.RemoteId,
                successor.ItemId, successor.RemoteId, transferId, retirement.OperationId, nameEpoch, _clock.GetUtcNow());
            try
            {
                _ = await _catalog.SelectRemoteIdentityGenerationAsync(selection, token).ConfigureAwait(false);
                return successor;
            }
            catch (InvalidOperationException)
            {
                // Another callback may have selected the same next generation after our indexed read.
                // Retry only an observed pointer change; original ownership/proof rejections still escape.
                var observed = await _catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.ItemId, token).ConfigureAwait(false);
                if (observed?.Generation == current?.Generation) throw;
            }
        }
        throw new InvalidOperationException("Concurrent identity generation selection must retry at its original boundary.");
    }

    private static CloudPlaceholderIdentity ValidateCurrent(RootRegistration root, string backendId, string? revision,
        CloudPlaceholderIdentity original, MirrorPulseRemoteIdentityGeneration selected)
    {
        var expected = MirrorPulseGenerationalPlaceholderIdentity.Create(root, backendId, selected.Generation, revision);
        if (selected.InstanceId != root.InstanceId || selected.RootKey != root.UniquenessKey || selected.BaseItemId != original.ItemId ||
            selected.BaseRemoteId != original.RemoteId || selected.ItemId != expected.ItemId || selected.RemoteId != expected.RemoteId)
            throw new InvalidDataException("The selected generation does not encode this original backend address and stable root.");
        return expected;
    }
}
