using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Validates an interrupted Move identity while the same object rotates its protection.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseDirectoryMovePermissionIdentity(MirrorPulseJournalDirectoryMoveProjection projection,
    Func<CancellationToken, ValueTask<(bool Original, bool Target)>> readOfficial)
{
    private readonly byte[] _originalIdentity = new CloudPlaceholderIdentity(projection.Original.OriginalItemId,
        projection.Original.OriginalRemoteId, projection.Original.OriginalNativeRevision).Encode();
    private readonly byte[] _targetIdentity = projection.TargetIdentity.Encode();

    internal MirrorPulseLocalFileBinding Binding => projection.Original.Binding;

    internal ValueTask RequireAsync(MirrorPulseProtectedLocalIdentityObservation observed, CancellationToken token) =>
        RequireAsync(observed.IsPlaceholder, observed.PlaceholderIdentity?.Encode() ?? [], observed.ItemId, observed.RemoteId, token);

    internal ValueTask RequireAsync(CloudItemSnapshot observed, CancellationToken token) =>
        RequireAsync(observed.Exists && observed.IsPlaceholder && !observed.IsTombstone,
            observed.PlaceholderIdentity, observed.ItemId, observed.RemoteId, token);

    private async ValueTask RequireAsync(bool placeholder, ReadOnlyMemory<byte> identity, Guid? itemId, string? remoteId,
        CancellationToken token)
    {
        if (!placeholder || !identity.Span.SequenceEqual(_originalIdentity) && !identity.Span.SequenceEqual(_targetIdentity))
            throw new InvalidDataException("The directory Move protection recovery lost its exact native identity.");
        var official = await readOfficial(token).ConfigureAwait(false);
        if ((!official.Original && !official.Target) ||
            ((itemId is not null || remoteId is not null) &&
                !(official.Original && itemId == projection.Original.OriginalItemId && remoteId == projection.Original.OriginalRemoteId) &&
                !(official.Target && itemId == projection.TargetIdentity.ItemId && remoteId == projection.TargetIdentity.RemoteId)))
            throw new InvalidDataException("The directory Move protection recovery lost its exact official identity.");
    }
}
