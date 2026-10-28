using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliFixtureProbe
{
    private sealed record CleanupRootLabelRoute(string DirectoryName, MirrorPulseLocalFileBinding LocalObject);

    private static async Task<CleanupRootLabelRoute> AuthenticateRenamedCleanupRootAsync(
        MirrorPulseProductCatalog catalog, CliFixtureMarker marker, RootRegistration registration)
    {
        var label = await catalog.ReadManagedRootRenameAsync(registration.RootId);
        if (label is not { Phase: MirrorPulseRootRenamePhase.Completed })
            throw new InvalidDataException("Renamed fixture cleanup requires its completed original Label owner.");
        var latest = await catalog.ReadManagedRootRenameHistoryAsync(label.OperationId);
        var member = latest?.Proof is { } proof ? await catalog.ReadNamespaceMemberByBindingAsync(proof.LocalObject) : null;
        if (member is not { IsDirectory: true, ParentMemberId: { } anchor } || member.RootId != registration.RootId ||
            member.RelativePath != marker.DirectoryName)
            throw new InvalidDataException("Renamed fixture cleanup requires the original registered root and anchor.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId);
        if (location.ParentMemberId != anchor || location.RelativePath != registration.DirectoryName ||
            location.CurrentPositionOperationId != label.OperationId)
            throw new InvalidDataException("Renamed fixture cleanup requires the committed current placement head.");

        var cancellationLineage = CliFixtureRootCancellationCleanupLineage.Select(marker.DirectoryName, registration.DirectoryName);
        Guid? operation = label.OperationId;
        string name = registration.DirectoryName;
        var seen = new HashSet<Guid>();
        // The original lifecycle keeps its three-move bound. The two additional
        // cancellation Labels require their exact bounded transition sequence.
        while (operation is { } id)
        {
            if (!seen.Add(id) || seen.Count > (cancellationLineage?.ExpectedMoves ?? 3))
                throw new InvalidDataException("The fixture Label lineage is cyclic or exceeds its fixed bound.");
            var history = await catalog.ReadManagedRootRenameHistoryAsync(id);
            var move = await catalog.ReadNamespaceMemberMoveAsync(id);
            var completion = await catalog.ReadNamespaceDirectoryMoveCompletionAsync(id);
            if (history is not { Intent.Phase: MirrorPulseRootRenamePhase.Completed, Proof: { DirectoryMoveEvidence: { } evidence }, Observation: not null } ||
                move is not { Version: 3 } || completion is null || move.RootId != registration.RootId ||
                history.Intent.RootId != registration.RootId || move.MemberId != member.MemberId ||
                move.LocalObject != member.LocalObject || move.OwnerSid != member.OwnerSid || move.ItemId != history.Proof.ItemId ||
                history.Proof.LocalObject != member.LocalObject || history.Proof.DirectoryMoveEvidence != move.DirectoryMoveEvidence ||
                move.Source.ParentMemberId != anchor || move.Target.ParentMemberId != anchor ||
                move.Target.RelativePath != name || history.Intent.TargetName != name || history.Intent.SourceName != move.Source.RelativePath ||
                completion.Root.RelativePath != name || completion.Root.ItemId != move.ItemId ||
                completion.PublicEvidenceSha256 != Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(evidence))) ||
                history.Intent.UpdatedAt != completion.Root.VerifiedAt)
                throw new InvalidDataException("The fixture Label lineage lost its original complete protected subtree receipt.");
            cancellationLineage?.RequireTransition(seen.Count - 1, move.Source.RelativePath, move.Target.RelativePath);
            name = move.Source.RelativePath;
            operation = move.SourcePositionOperationId ?? move.PreviousMoveOperationId;
        }
        cancellationLineage?.RequireComplete(seen.Count);
        if (name != marker.DirectoryName)
            throw new InvalidDataException("The fixture Label lineage does not end at the exact original marker.");
        return new(registration.DirectoryName, member.LocalObject);
    }
}
