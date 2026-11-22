using System.Runtime.Versioning;
using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Indexes only the exact sealed target members of an original root Label operation.</summary>
/// <remarks>This copied path index grants no native access, routing, source access, or journal authority.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRootLabelTargetPaths
{
    private readonly Dictionary<string, MirrorPulseNamespaceDirectoryMoveEntry> _targets = new(StringComparer.Ordinal);
    internal MirrorPulseNamespaceMemberMoveIntent Intent { get; }
    internal RootRegistration Registration { get; }

    internal MirrorPulseRootLabelTargetPaths(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot, RootRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registration);
        if (intent.Version != 3 || intent.RemoteEntry is not null || intent.OperationId == Guid.Empty ||
            intent.DirectoryMoveEvidence is null || snapshot.Version != 1 || snapshot.OperationId != intent.OperationId ||
            snapshot.Entries is null || snapshot.Entries.Count is 0 or > 65_536 ||
            registration.RootId != intent.RootId || registration.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) ||
            registration.DirectoryName != intent.Source.RelativePath || registration.Label != intent.Source.RelativePath ||
            !IsSegment(intent.Source.RelativePath) || !IsSegment(intent.Target.RelativePath) || intent.Source.RelativePath == intent.Target.RelativePath ||
            intent.Source.ParentMemberId == Guid.Empty || intent.Source.ParentMemberId != intent.Target.ParentMemberId ||
            intent.Source.ParentProtectionId == Guid.Empty || intent.Source.ParentProtectionId != intent.Target.ParentProtectionId ||
            intent.Source.ParentPositionOperationId != intent.Target.ParentPositionOperationId ||
            intent.Source.ParentLocalObject != intent.Target.ParentLocalObject ||
            intent.Source.ParentLocalObject.LocalFileId != intent.LocalObject.SyncRootFileId ||
            intent.Source.ParentLocalObject.SyncRootFileId != intent.LocalObject.SyncRootFileId ||
            intent.Source.ParentLocalObject.VolumeSerialNumber != intent.LocalObject.VolumeSerialNumber ||
            intent.LocalObject.LocalFileId == intent.LocalObject.SyncRootFileId ||
            snapshot.PublicEvidenceSha256 != Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(intent.DirectoryMoveEvidence))))
            throw new InvalidDataException("Root Label target inspection requires its original sealed operation and sync-root anchor.");

        var entries = new Dictionary<Guid, MirrorPulseNamespaceDirectoryMoveEntry>();
        var bindings = new HashSet<MirrorPulseLocalFileBinding>();
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Identity is null || entry.MemberId == Guid.Empty || entry.ParentMemberId is null ||
                entry.ProtectionId == Guid.Empty || entry.RelativePath is null || entry.OwnerSid != intent.OwnerSid || entry.Depth < 0 ||
                entry.ProtectionVerifiedAt == default || entry.ProtectionVerifiedAt > intent.PreparedAt ||
                entry.Identity.MemberId != entry.MemberId || entry.Identity.ItemId is null || entry.Identity.ItemId == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.Identity.RemoteId) || entry.LocalObject.VolumeSerialNumber != intent.LocalObject.VolumeSerialNumber ||
                entry.LocalObject.SyncRootFileId != intent.LocalObject.SyncRootFileId ||
                entry.LocalObject.LocalFileId == intent.LocalObject.SyncRootFileId || !bindings.Add(entry.LocalObject) ||
                !entries.TryAdd(entry.MemberId, entry))
                throw new InvalidDataException("Root Label target inspection requires unique original members, bindings and public identities.");
        }
        if (!entries.TryGetValue(intent.MemberId, out var root) || !root.IsDirectory || root.Depth != 0 ||
            root.ParentMemberId != intent.Source.ParentMemberId || root.RelativePath != intent.Source.RelativePath ||
            root.LocalObject != intent.LocalObject || root.ProtectionId != intent.ProtectionId ||
            root.Identity.ItemId != intent.ItemId || root.Identity.RemoteId != intent.RemoteId ||
            root.Identity.AcceptanceOperationId != intent.IdentityAcceptanceOperationId ||
            root.PreviousMoveOperationId != intent.PreviousMoveOperationId ||
            (root.PreviousPositionOperationId ?? root.PreviousMoveOperationId) != (intent.SourcePositionOperationId ?? intent.PreviousMoveOperationId) ||
            entries.ContainsKey(intent.Source.ParentMemberId))
            throw new InvalidDataException("Root Label target inspection cannot substitute the original root member.");

        foreach (var entry in snapshot.Entries)
        {
            if (entry.MemberId != root.MemberId && (entry.Depth == 0 ||
                !entries.TryGetValue(entry.ParentMemberId!.Value, out var parent) || !parent.IsDirectory ||
                entry.Depth != parent.Depth + 1 || !entry.RelativePath.StartsWith(intent.Source.RelativePath + "/", StringComparison.Ordinal) ||
                ParentPath(entry.RelativePath) != parent.RelativePath ||
                !IsSegment(entry.RelativePath[(entry.RelativePath.LastIndexOf('/') + 1)..])))
                throw new InvalidDataException("Root Label target inspection cannot adopt an unsealed path or parent.");
            string target = intent.Target.RelativePath + entry.RelativePath[intent.Source.RelativePath.Length..];
            if (!targetNames.Add(target) || !_targets.TryAdd(target, entry))
                throw new InvalidDataException("Root Label target inspection cannot adopt duplicate target paths.");
        }
        Intent = intent;
        Registration = registration;
    }

    internal MirrorPulseNamespaceDirectoryMoveEntry RequireMember(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!_targets.TryGetValue(relativePath, out var entry) || entry.IsDirectory != isDirectory)
            throw new InvalidDataException("Only an exact original sealed target member may be inspected during a root Label change.");
        return entry;
    }

    internal void RequireCurrentRouter(MirrorPulseRootRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (router.FindRegistration(Registration.RootId) != Registration)
            throw new InvalidOperationException("Root Label target inspection requires its unchanged current source registration.");
    }

    private static string ParentPath(string value) => value.LastIndexOf('/') is var separator && separator > 0
        ? value[..separator] : string.Empty;

    private static bool IsSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        !value.Any(character => character < ' ' || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') &&
        !value.EndsWith('.') && !value.EndsWith(' ');
}
