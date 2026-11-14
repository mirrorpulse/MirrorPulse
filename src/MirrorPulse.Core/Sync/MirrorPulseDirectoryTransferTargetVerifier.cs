using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

internal sealed record MirrorPulseDirectoryTransferTargetMember(Guid CopyOperationId, string RelativePath,
    string RemoteId, string Revision, bool IsDirectory, MirrorPulseNamespaceImportContent? Content);

internal sealed record MirrorPulseDirectoryTransferTargetVerification(int Version, Guid OperationId,
    IReadOnlyList<MirrorPulseDirectoryTransferTargetMember> Members, DateTimeOffset VerifiedAt);

/// <summary>Verifies complete target content and exact membership after the correlated directory copy effects.</summary>
/// <remarks>Directory metadata may advance as children are created. The first final readback selects
/// its current opaque revisions, and every later readback requires those same selected revisions.
/// Readback cannot supply a missing original create or upload acceptance.</remarks>
internal sealed class MirrorPulseDirectoryTransferTargetVerifier(MirrorPulseTransferTargetVerifier files,
    IMirrorPulseWorkerStatTransport stats, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal async Task<MirrorPulseDirectoryTransferTargetVerification> ReadCurrentAsync(MirrorPulseDirectoryTransferCopy copy,
        MirrorPulseDirectoryTransferTargetVerification? original, CancellationToken token)
    {
        var plan = copy.Admission;
        if (copy.Steps.Count != plan.Members.Count || copy.Steps.Any(step => step.AcceptedAt is null))
            throw new MirrorPulseMutationAmbiguousException("Directory target readback requires every original copy acceptance.");
        if (original is not null && (original.Version != 1 || original.OperationId != plan.Command.CommandId || original.Members.Count != plan.Members.Count))
            throw new InvalidDataException("The original target verification belongs to another directory transfer.");
        var observed = new MirrorPulseWorkerDirectoryEntry[plan.Members.Count];
        var selected = new List<MirrorPulseDirectoryTransferTargetMember>(plan.Members.Count);
        var remoteIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Members.Count; index++)
        {
            var member = plan.Members[index];
            var retained = original?.Members[index];
            bool directory = member.Source.Protection.Context.Member.IsDirectory;
            string path = member.TargetRelativePath[(plan.TargetRoute.DirectoryName.Length + 1)..];
            if (retained is not null && (retained.CopyOperationId != member.CopyOperationId || retained.RelativePath != path ||
                retained.IsDirectory != directory || retained.Content != member.Content))
                throw new InvalidDataException("The original target verification changed its member or complete content.");
            string revision = copy.Steps[index].AcceptedRevision!;
            MirrorPulseWorkerDirectoryEntry entry;
            if (directory)
            {
                string current = await stats.StatAsync(new(plan.TargetRoute.InstanceId, path, plan.TargetRoute.RootKey), token).ConfigureAwait(false)
                    ?? throw new IOException("A copied target directory is missing.");
                if (retained is not null && retained.Revision != current)
                    throw new IOException("A target directory changed after its selected whole-tree verification.");
                entry = await files.ReadDirectoryMetadataAsync(plan.TargetRoute, path, current, token).ConfigureAwait(false);
            }
            else
            {
                if (retained is not null && retained.Revision != revision)
                    throw new InvalidDataException("The target file lost its original accepted revision.");
                entry = await files.VerifyFileAsync(plan.TargetRoute, path, member.Content!.Length, member.Content.Sha256,
                    revision, retained?.RemoteId, token).ConfigureAwait(false);
            }
            if (retained is not null && retained.RemoteId != entry.RemoteId)
                throw new IOException("A selected directory transfer target changed its remote identity.");
            if (!remoteIds.Add(entry.RemoteId))
                throw new IOException("Multiple directory transfer targets claim the same remote identity.");
            observed[index] = entry;
            selected.Add(new(member.CopyOperationId, path, entry.RemoteId, entry.RemoteRevision, directory, member.Content));
        }

        var byParent = selected.Select((member, ordinal) => (member, ordinal)).ToLookup(value =>
        {
            int separator = value.member.RelativePath.LastIndexOf('/');
            return separator < 0 ? "" : value.member.RelativePath[..separator];
        }, StringComparer.Ordinal);
        foreach (var directory in selected.Where(member => member.IsDirectory))
        {
            var children = byParent[directory.RelativePath].Select(value => observed[value.ordinal]).ToArray();
            await files.RequireDirectoryInventoryAsync(plan.TargetRoute, directory.RelativePath, children, token).ConfigureAwait(false);
            var current = await files.ReadDirectoryMetadataAsync(plan.TargetRoute, directory.RelativePath, directory.Revision, token).ConfigureAwait(false);
            if (current.RemoteId != directory.RemoteId ||
                await stats.StatAsync(new(plan.TargetRoute.InstanceId, directory.RelativePath, plan.TargetRoute.RootKey), token).ConfigureAwait(false) != directory.Revision)
                throw new IOException("The target directory changed during whole-tree readback.");
        }
        return new(1, plan.Command.CommandId, selected.AsReadOnly(), _time.GetUtcNow());
    }
}
