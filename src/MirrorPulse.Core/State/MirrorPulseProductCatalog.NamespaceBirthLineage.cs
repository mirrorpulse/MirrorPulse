using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record NamespaceBirthParent(RootId? RootId, string RelativePath, bool IsDirectory,
        MirrorPulseLocalFileBinding LocalObject, string OwnerSid, string RoleSid, DateTimeOffset VerifiedAt,
        MirrorPulseNamespacePermissionBaseline? Original, MirrorPulseNamespacePermissionChange? Permission,
        MirrorPulseNamespaceBirthIntent? Birth, MirrorPulseNamespaceBirthProtection? Protection);

    // A new recovery intent can claim a current parent without rewriting the original birth
    // or inventing a placeholder observation for an ordinary object.
    private sealed record NamespaceBirthParentClaim(Guid ParentPermissionOperationId,
        MirrorPulseNamespaceBornParent? BornParent, string RoleSid, DateTimeOffset ClaimedAt);

    // Resolve the referenced graph iteratively. A born parent must be one exact, shorter
    // directory path. Do not recursively enumerate histories or impose a new nesting limit.
    private Task<NamespaceBirthParent> ValidateNamespaceBirthLineageAsync(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceBirthProtection? protection, bool requireCurrentParent,
        SqliteTransaction? transaction, CancellationToken token) =>
        ValidateNamespaceBirthLineageAsync(birth, protection, requireCurrentParent, null, transaction, token);

    private async Task<NamespaceBirthParent> ValidateNamespaceBirthLineageAsync(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceBirthProtection? protection, bool requireCurrentParent, NamespaceBirthParentClaim? directParentClaim,
        SqliteTransaction? transaction, CancellationToken token, List<MirrorPulseNamespaceBirthProtection>? locatedParents = null)
    {
        var pending = new Stack<(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthProtection? Protection, bool Current, bool CheckPrevious, NamespaceBirthParentClaim? Claim)>();
        var visited = new HashSet<(Guid Birth, Guid Protection, bool Current, bool CheckPrevious)>();
        pending.Push((birth, protection, requireCurrentParent, false, directParentClaim));
        NamespaceBirthParent? first = null;
        var located = locatedParents ?? new List<MirrorPulseNamespaceBirthProtection>();
        var currentLocated = new HashSet<Guid>();
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add((node.Birth.OperationId, node.Protection?.ProtectionId ?? Guid.Empty, node.Current, node.CheckPrevious))) continue;
            var parent = await ResolveNamespaceBirthParentAsync(node.Birth, node.Protection, node.Claim, transaction, token).ConfigureAwait(false);
            first ??= parent;
            int separator = node.Birth.RelativePath.LastIndexOf('/');
            string parentPath = separator < 0 ? string.Empty : node.Birth.RelativePath[..separator];
            string role = node.Claim?.RoleSid ?? (node.Protection?.Placement is not null ? node.Birth.RoleSid : node.Protection?.RoleSid) ?? node.Birth.RoleSid;
            DateTimeOffset observedAt = node.Claim?.ClaimedAt ?? node.Protection?.Verification.ObservedAt ?? node.Birth.PreparedAt;
            if (!parent.IsDirectory || parent.LocalObject != node.Birth.ParentLocalObject || parent.RelativePath != parentPath ||
                parent.RoleSid != role || observedAt < parent.VerifiedAt ||
                (parent.RootId is null ? !node.Birth.IsDirectory || separator >= 0 : parent.RootId != node.Birth.RootId))
                throw new InvalidOperationException("The birth requires its exact direct protected parent, role and location.");

            if (node.Protection is { } childProtection)
            {
                var observation = await ReadNamespaceBirthObservationRowAsync(node.Birth.OperationId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original native birth observation is missing.");
                await ValidateNamespaceBirthObservationLocalReferencesAsync(observation, node.Birth, parent.OwnerSid, transaction, token).ConfigureAwait(false);
                if (childProtection.BirthOperationId != node.Birth.OperationId || childProtection.Verification.LocalObject != observation.LocalObject ||
                    childProtection.Verification.OwnerSid != observation.OwnerSid || childProtection.Verification.ObservedAt < observation.ObservedAt)
                    throw new InvalidOperationException("Birth protection must retain the original native object and owner.");
                pending.Push((node.Birth, null, false, false, null));
                if (node.CheckPrevious && childProtection.PreviousProtectionId is { } previousId)
                {
                    var previous = await ReadNamespaceBirthProtectionRowAsync(previousId, transaction, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The parent protection predecessor is missing.");
                    var current = await ReadNamespaceBirthProtectionRowAsync(childProtection.ProtectionId, transaction, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The parent protection is missing.");
                    if (previous.Sequence >= current.Sequence || previous.Protection.BirthOperationId != childProtection.BirthOperationId ||
                        previous.Protection.Verification.ObservedAt > childProtection.Verification.ObservedAt)
                        throw new InvalidDataException("The parent protection predecessor is inconsistent.");
                    pending.Push((node.Birth, previous.Protection, false, false, null));
                }
            }
            if (node.Current)
            {
                if (parent.Original is { } original && parent.Permission is { } permission)
                {
                    var latest = await ReadLatestPermissionChangeCoreAsync(original.EvidenceId, token, transaction).ConfigureAwait(false);
                    if (latest?.Intent.OperationId != permission.Intent.OperationId)
                        throw new InvalidOperationException("Birth admission requires current protection throughout its parent lineage.");
                    if (transaction is null) throw new InvalidOperationException("Current admission requires an owned transaction.");
                    await ValidatePermissionTreeAdmissionAsync(original, permission.Intent, transaction, token).ConfigureAwait(false);
                }
                else
                {
                    await using SqliteCommand latest = _connection.CreateCommand();
                    latest.Transaction = transaction;
                    latest.CommandText = "SELECT protection_id FROM namespace_birth_protections WHERE birth_operation_id=$birth ORDER BY sequence DESC LIMIT 1;";
                    latest.Parameters.AddWithValue("$birth", parent.Birth!.OperationId.ToString("D"));
                    if ((string?)await latest.ExecuteScalarAsync(token).ConfigureAwait(false) != parent.Protection!.ProtectionId.ToString("D"))
                        throw new InvalidOperationException("Birth admission requires its born parent's latest protection epoch.");
                }
            }
            if (parent.Birth is { } parentBirth)
            {
                if (parent.Protection?.Placement is not null)
                {
                    located.Add(parent.Protection);
                    if (node.Current) currentLocated.Add(parent.Protection.ProtectionId);
                }
                pending.Push((parentBirth, parent.Protection, node.Current && parent.Protection?.Placement is null, true, null));
            }
        }
        if (locatedParents is null && located.Count != 0)
            await ValidateNamespaceLocatedBirthHistoryAsync(located, transaction, token).ConfigureAwait(false);
        if (currentLocated.Count != 0)
        {
            if (transaction is null) throw new InvalidOperationException("Current located birth lineage requires an owned transaction.");
            foreach (var epoch in located.Where(epoch => currentLocated.Contains(epoch.ProtectionId)).DistinctBy(epoch => epoch.ProtectionId))
                _ = await ReadNamespaceMemberProtectionLineageCoreAsync(epoch.Placement!.MemberId, transaction, token, requireUnfenced: false).ConfigureAwait(false);
        }
        return first ?? throw new InvalidDataException("The direct parent is missing.");
    }

    private async Task<NamespaceBirthParent> ResolveNamespaceBirthParentAsync(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceBirthProtection? protection, NamespaceBirthParentClaim? claim,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var born = claim is not null ? claim.BornParent : protection is not null ? protection.BornParent : birth.BornParent;
        if (protection?.Placement is not null && (protection.BornParent != birth.BornParent ||
            protection.ParentPermissionOperationId != birth.ParentPermissionOperationId))
            throw new InvalidOperationException("Located protection must retain the first birth's immutable parent references.");
        if ((protection is not null || claim is not null) &&
            (born?.BirthOperationId != birth.BornParent?.BirthOperationId || (born is null) != (birth.BornParent is null)))
            throw new InvalidOperationException("Protection or recovery cannot substitute another stable direct parent.");
        if (born is null)
        {
            var original = await ReadPermissionBaselineCoreAsync(birth.ParentEvidenceId, token, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original parent permissions are missing.");
            var permission = await ReadPermissionChangeCoreAsync(claim?.ParentPermissionOperationId ?? protection?.ParentPermissionOperationId ?? birth.ParentPermissionOperationId, token, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("Verified direct-parent protection is missing.");
            if (permission.Intent.EvidenceId != original.EvidenceId || permission.Intent.LocalObject != original.LocalObject ||
                permission.Phase != MirrorPulseNamespacePermissionPhase.Verified || permission.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                throw new InvalidOperationException("The original direct parent requires verified protection.");
            return new(permission.Intent.RootId, permission.Intent.RelativePath, original.IsDirectory, original.LocalObject,
                original.OwnerSid, permission.Intent.RoleSid, permission.Verification!.ObservedAt, original, permission, null, null);
        }
        var parentBirth = await ReadNamespaceBirthRowAsync(born.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original direct-parent birth is missing.");
        var parentObservation = await ReadNamespaceBirthObservationRowAsync(born.BirthOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The direct parent has no native birth observation.");
        var parentProtection = await ReadNamespaceBirthProtectionRowAsync(born.ProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The direct-parent protection epoch is missing.");
        if (parentProtection.Protection.BirthOperationId != parentBirth.OperationId ||
            parentProtection.Protection.Verification.LocalObject != parentObservation.LocalObject ||
            parentProtection.Protection.Verification.OwnerSid != parentObservation.OwnerSid)
            throw new InvalidOperationException("The parent protection epoch belongs to another native object.");
        return new(parentBirth.RootId, parentProtection.Protection.Placement?.RelativePath ?? parentBirth.RelativePath, parentBirth.IsDirectory, parentObservation.LocalObject,
            parentObservation.OwnerSid, parentProtection.Protection.RoleSid, parentProtection.Protection.Verification.ObservedAt,
            null, null, parentBirth, parentProtection.Protection);
    }
}
