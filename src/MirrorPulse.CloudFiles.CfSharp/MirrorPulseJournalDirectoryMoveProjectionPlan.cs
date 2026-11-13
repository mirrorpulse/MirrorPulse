using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseJournalDirectoryMoveProjection(MirrorPulseJournalDirectoryMoveMember Original,
    CloudPlaceholderIdentity TargetIdentity);

/// <summary>Resolves new addresses for the original moved objects without accepting descendant content.</summary>
[SupportedOSPlatform("windows10.0.16299")]
internal static class MirrorPulseJournalDirectoryMoveProjectionPlan
{
    internal static async Task<IReadOnlyList<MirrorPulseJournalDirectoryMoveProjection>> CreateAsync(
        MirrorPulseJournalDirectoryMoveAdmission admission, string workerAcceptedRevision,
        MirrorPulseRootRouter router, IMirrorPulseWorkerDirectoryPageSource directories, CancellationToken token)
    {
        if (workerAcceptedRevision != admission.Root.OriginalRevision || string.IsNullOrEmpty(workerAcceptedRevision))
            throw new MirrorPulseMutationAmbiguousException("The durable Worker receipt does not match the original directory revision.");
        var root = router.GetRegistration(admission.InstanceId, admission.RootKey);
        if (root.RootId != admission.RootId)
            throw new MirrorPulseMutationAmbiguousException("The original directory Move root has changed.");

        var byParent = admission.Members.GroupBy(member => Parent(WorkerPath(member.TargetPath, root.DirectoryName)), StringComparer.Ordinal);
        var metadata = new Dictionary<string, MirrorPulseWorkerDirectoryEntry>(StringComparer.Ordinal);
        foreach (var parent in byParent)
        {
            var expected = parent.Select(member => WorkerPath(member.TargetPath, root.DirectoryName)).ToHashSet(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
            for (int pageNumber = 0; ; pageNumber++)
            {
                if (pageNumber == 4096) throw new InvalidDataException("The moved directory readback exceeded its page limit.");
                var page = await directories.ReadDirectoryPageAsync(new(admission.InstanceId, parent.Key, cursor, 256,
                    admission.RootKey), token).ConfigureAwait(false);
                foreach (var entry in page.Entries)
                {
                    string relative = entry.RelativePath.Replace('\\', '/');
                    if (expected.Contains(relative) && !metadata.TryAdd(relative, entry))
                        throw new InvalidDataException("The moved directory readback returned a duplicate original target.");
                }
                if (page.IsComplete) break;
                cursor = page.ContinuationCursor;
                if (cursor.IsEmpty || cursor.Length > 8192 || !cursors.Add(Convert.ToBase64String(cursor.Span)))
                    throw new InvalidDataException("The moved directory readback cursor did not advance.");
            }
        }

        var selected = new List<MirrorPulseJournalDirectoryMoveProjection>(admission.Members.Count);
        var ids = new HashSet<Guid>();
        foreach (var member in admission.Members)
        {
            string target = WorkerPath(member.TargetPath, root.DirectoryName);
            if (!metadata.TryGetValue(target, out var entry) || entry.IsDeleted || entry.RemoteId != target ||
                entry.ItemKind != (member.IsDirectory ? "Directory" : "File") ||
                !MirrorPulseLocalDirectoryMoveRevision.IsSameObject(member.OriginalRevision, entry.RemoteRevision, member.IsDirectory))
                throw new MirrorPulseMutationAmbiguousException("The moved directory readback cannot adopt a later replacement object.");
            // Retain the original baseline even when the same descendant now carries later content.
            var identity = await router.CreateCurrentFileIdentityAsync(admission.InstanceId, admission.RootKey,
                entry.RemoteId, member.OriginalRevision, token).ConfigureAwait(false);
            if (!ids.Add(identity.ItemId)) throw new InvalidDataException("Moved targets do not have distinct public identities.");
            selected.Add(new(member, identity));
        }
        return selected.AsReadOnly();
    }

    private static string WorkerPath(string path, string label)
    {
        if (!path.StartsWith(label + "/", StringComparison.Ordinal) || path.Length <= label.Length + 1)
            throw new InvalidDataException("The original directory member is outside the current Adapter root.");
        return path[(label.Length + 1)..];
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}
