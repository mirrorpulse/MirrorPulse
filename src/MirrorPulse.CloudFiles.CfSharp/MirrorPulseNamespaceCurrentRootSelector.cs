using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Selects retained current root provenance using public native bindings, never by adopting a path.</summary>
/// <remarks>This is recovery input, not a native authorization or live readiness receipt.
/// The caller retains the current library owner; recovery still performs its fresh guarded audit.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal static class MirrorPulseNamespaceCurrentRootSelector
{
    internal static async Task<IReadOnlyList<MirrorPulseNamespaceInitializationRoot>> SelectAsync(CloudFileSystem fileSystem,
        MirrorPulseProductCatalog catalog, IReadOnlyList<RootRegistration> registrations, CancellationToken token)
    {
        var anchor = await ReadMemberAsync(fileSystem.Root).ConfigureAwait(false);
        if (anchor.ParentMemberId is not null || anchor.RootId is not null || anchor.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
            throw new InvalidDataException("Current recovery requires its original registered sync-root anchor.");
        var result = new List<MirrorPulseNamespaceInitializationRoot>(registrations.Count);
        foreach (var registration in registrations)
        {
            var member = await ReadMemberAsync(fileSystem.GetDirectory(registration.DirectoryName)).ConfigureAwait(false);
            var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
            if (member.RootId != registration.RootId || location.ParentMemberId != anchor.MemberId ||
                location.RelativePath != registration.DirectoryName)
                throw new InvalidDataException("A configured root lost its actual binding, stable owner or committed top-level placement.");
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
            {
                var roots = await MirrorPulseNamespaceInitializationCoordinator.SelectOriginalRootsAsync(catalog, [registration], true, token).ConfigureAwait(false);
                var capture = await catalog.ReadNamespacePermissionTreeAsync(roots[0].ManifestId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The current original root lost its capture.");
                if (capture.Definition.Anchor.Baseline.EvidenceId != member.OriginId || capture.Definition.Anchor.Baseline.LocalObject != member.LocalObject)
                    throw new InvalidDataException("The root capture belongs to another original object.");
                result.Add(roots[0]);
            }
            else
            {
                Guid? after = null;
                while (true)
                {
                    var captures = await catalog.ReadNamespacePermissionTreesAsync(registration.RootId, after, cancellationToken: token).ConfigureAwait(false);
                    if (captures.Count == 0) break;
                    if (captures.Any(capture => capture.Phase != MirrorPulseNamespacePermissionTreePhase.Cancelled))
                        throw new InvalidDataException("A born root cannot replace an unexplained original capture.");
                    after = captures[^1].Definition.ManifestId;
                }
                result.Add(new(registration.RootId, Guid.Empty) { BornRootMemberId = member.MemberId });
            }
        }
        return result.AsReadOnly();

        async Task<MirrorPulseNamespaceMemberRegistration> ReadMemberAsync(CloudDirectory directory)
        {
            var snapshot = await directory.InspectAsync(token).ConfigureAwait(false);
            if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.Directory || snapshot.LocalBinding is not { } binding)
                throw new InvalidDataException("A current recovery directory has no actual native binding.");
            var member = await catalog.ReadNamespaceMemberByBindingAsync(new(binding.VolumeSerialNumber, binding.SyncRootFileId,
                binding.LocalFileId), token).ConfigureAwait(false);
            if (member is not { IsDirectory: true })
                throw new InvalidDataException("A current recovery directory has no retained registered provenance.");
            return member;
        }
    }
}
