using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateRemoteUpdatePlan(MirrorPulseNamespaceRemoteUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateRemoteBirthLookup(plan.InstanceId, plan.BatchId, plan.Ordinal);
        if (plan.Version != 1 || plan.OperationId == Guid.Empty || plan.ItemId == Guid.Empty || plan.PreparedAt == default ||
            plan.BatchEntryCount is < 1 or > 65_536 || plan.Ordinal >= plan.BatchEntryCount ||
            plan.BatchFingerprint is not { Length: 64 } || plan.BatchFingerprint.Any(character => !char.IsAsciiHexDigitUpper(character)) ||
            InvalidRemoteUpdateText(plan.ChangeId, 16_384) || InvalidRemoteUpdateText(plan.RemoteId, 4096) ||
            InvalidRemoteUpdateText(plan.PreviousRemoteRevision, 4096) || InvalidRemoteUpdateText(plan.RemoteRevision, 4096) ||
            new[] { plan.NativeRemoteRevision, plan.OfficialRemoteRevision }.Any(value => value is not null && InvalidRemoteUpdateText(value, 4096)) ||
            !MirrorPulseRemoteLibraryProvenance.IsSupported(plan.PackageVersion, plan.PackageSource) ||
            plan.Policy is not { PreserveUnsynchronizedLocalContent: true } policy || policy.EchoSuppressionObservationCount is < 1 or > 64 ||
            policy.EchoSuppressionLifetimeTicks <= 0 || policy.EchoSuppressionLifetimeTicks > TimeSpan.FromHours(1).Ticks ||
            plan.Child is not { Context: { } child } || plan.Parent is not { Context: { } parent } ||
            child.Member is null || parent.Member is null || child.Location is null || parent.Location is null ||
            child.Protection is null || parent.Protection is null || child.Verification is null || parent.Verification is null ||
            child.Member.RootId is null || child.Member.RootId != parent.Member.RootId || child.Member.MemberId == parent.Member.MemberId ||
            child.Location.MemberId != child.Member.MemberId || parent.Location.MemberId != parent.Member.MemberId ||
            child.Protection.MemberId != child.Member.MemberId || parent.Protection.MemberId != parent.Member.MemberId ||
            child.Location.ParentMemberId != parent.Member.MemberId || !parent.Member.IsDirectory ||
            child.Member.OwnerSid != parent.Member.OwnerSid || child.Protection.RoleSid != parent.Protection.RoleSid ||
            plan.Child.Identity is not { } identity || identity.MemberId != child.Member.MemberId ||
            identity.ItemId != plan.ItemId || identity.RemoteId != plan.RemoteId ||
            plan.Parent.Identity is null || plan.Parent.Identity.MemberId != parent.Member.MemberId ||
            InvalidRemoteUpdateText(child.Location.RelativePath, 32_767) || InvalidRemoteUpdateText(parent.Location.RelativePath, 32_767))
            throw new ArgumentException("A remote update requires its original versioned entry and the same registered child and protected direct parent.", nameof(plan));
        int separator = child.Location.RelativePath.LastIndexOf('/');
        if (separator < 1 || child.Location.RelativePath[..separator] != parent.Location.RelativePath)
            throw new ArgumentException("A remote update cannot move a member or replace a managed root.", nameof(plan));
        ValidateNamespaceMember(child.Member); ValidateNamespaceMember(parent.Member);
        if (plan.PreparedAt < child.Member.RegisteredAt || plan.PreparedAt < parent.Member.RegisteredAt)
            throw new InvalidOperationException("A remote update cannot predate its registered child or direct parent.");
        var childEarliest = child.Verification.ObservedAt > child.Member.RegisteredAt ? child.Verification.ObservedAt : child.Member.RegisteredAt;
        var parentEarliest = parent.Verification.ObservedAt > parent.Member.RegisteredAt ? parent.Verification.ObservedAt : parent.Member.RegisteredAt;
        RequireRemoteUpdateVerification(plan.Child, plan.ChildBefore, childEarliest, plan.PreparedAt);
        RequireRemoteUpdateVerification(plan.Parent, plan.ParentBefore, parentEarliest, plan.PreparedAt);
    }

    private static bool InvalidRemoteUpdateText(string? value, int limit) =>
        string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Contains('\0');

    private static void RequireRemoteUpdateVerification(MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespacePermissionVerification verification, DateTimeOffset earliest, DateTimeOffset latest)
    {
        if (verification is null || verification.LocalObject != original.Context.Member.LocalObject ||
            verification.OwnerSid != original.Context.Member.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(original.ExpectedDacl, verification.Dacl) ||
            verification.ObservedAt < earliest || verification.ObservedAt > latest ||
            original.Context.PositionVerification is { } position && verification.ObservedAt < position.ObservedAt)
            throw new InvalidOperationException("Remote update verification requires the same actual object, owner, protected descriptor and fresh chronology.");
    }

    private static void ValidateRemoteUpdateOutcome(MirrorPulseNamespaceRemoteUpdate retained, MirrorPulseNamespaceRemoteUpdateOutcome outcome)
    {
        var plan = retained.Plan;
        if (outcome.Version != 1 || outcome.OperationId != plan.OperationId || retained.StartedAt is not { } start ||
            !Enum.IsDefined(outcome.Disposition) || !Enum.IsDefined(outcome.Source) || outcome.VerifiedAt < start ||
            new[] { outcome.NativeRemoteRevision, outcome.OfficialRemoteRevision }.Any(value => value is not null && InvalidRemoteUpdateText(value, 4096)))
            throw new InvalidOperationException("Remote update settlement requires its original SDK start and supported observed outcome.");
        RequireRemoteUpdateVerification(plan.Child, outcome.Child, start, outcome.VerifiedAt);
        RequireRemoteUpdateVerification(plan.Parent, outcome.Parent, start, outcome.VerifiedAt);
        if (outcome.Disposition == MirrorPulseNamespaceRemoteUpdateDisposition.Applied)
        {
            if (outcome.ConflictId is not null || outcome.ConflictDetectedAt is not null || !outcome.NativeIsPlaceholder || !outcome.NativeIsInSync ||
                plan.OfficialRemoteRevision != plan.PreviousRemoteRevision || outcome.NativeRemoteRevision != plan.RemoteRevision ||
                outcome.OfficialRemoteRevision != plan.RemoteRevision)
                throw new InvalidOperationException("An applied update requires the exact incoming native and official revision, in-sync state and no conflict.");
        }
        else if (outcome.ConflictId is null || outcome.ConflictId == Guid.Empty || outcome.ConflictDetectedAt is not { } detected ||
            detected < start || detected > outcome.VerifiedAt || outcome.Child.ObservedAt < detected || outcome.Parent.ObservedAt < detected ||
            outcome.NativeIsPlaceholder != plan.NativeIsPlaceholder || outcome.NativeRemoteRevision != plan.NativeRemoteRevision ||
            outcome.OfficialRemoteRevision != plan.OfficialRemoteRevision)
            throw new InvalidOperationException("A conflicted update requires the actual durable SDK conflict and unchanged original native and official revisions.");
    }

    private async Task RequireRemoteUpdateHistoricalSnapshotAsync(MirrorPulseNamespaceMemberProtectionSnapshot snapshot,
        DateTimeOffset preparedAt, SqliteTransaction transaction, CancellationToken token)
    {
        var context = snapshot.Context;
        var member = await ReadNamespaceMemberCoreAsync(context.Member.MemberId, transaction, token).ConfigureAwait(false);
        if (member is null || member != context.Member) throw new InvalidDataException("The remote update lost its original member provenance.");
        var position = await ReadNamespaceMemberPositionEvidenceCoreAsync(member, context.Location.CurrentPositionOperationId,
            preparedAt, transaction, token).ConfigureAwait(false);
        if (position.ParentMemberId != context.Location.ParentMemberId || position.RelativePath != context.Location.RelativePath)
            throw new InvalidDataException("The remote update lost its retained original position.");
        var proof = await ReadNamespaceMemberProofAsync(member, false, transaction, token, context.Protection.ProtectionId, preparedAt).ConfigureAwait(false);
        MirrorPulseNamespacePermissionVerification verification; Guid? epochPosition;
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var permission = await ReadPermissionChangeCoreAsync(proof.ProtectionId, token, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote update lost its original permission epoch.");
            verification = permission.Verification!; epochPosition = permission.Intent.Relocation?.CompletedMoveOperationId;
            if (snapshot.PermissionOperationId != permission.Intent.OperationId || snapshot.BornParent is not null || snapshot.Observation is not null ||
                snapshot.ExpectedDacl != permission.Intent.TargetDacl)
                throw new InvalidDataException("The remote update changed its captured-original protection provenance.");
        }
        else
        {
            var protection = await ReadNamespaceBirthProtectionCoreAsync(proof.ProtectionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote update lost its birth protection epoch.");
            verification = protection.Verification; epochPosition = protection.Placement?.PositionOperationId;
            if (snapshot.PermissionOperationId != Guid.Empty || snapshot.BornParent != new MirrorPulseNamespaceBornParent(member.OriginId, protection.ProtectionId) ||
                snapshot.Observation != await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false) ||
                snapshot.ExpectedDacl != protection.Verification.Dacl)
                throw new InvalidDataException("The remote update changed its first birth observation or protected epoch.");
        }
        if (context.Protection != new MirrorPulseNamespaceMemberProtectionEpoch(member.MemberId, proof.ProtectionId, proof.RoleSid, proof.VerifiedAt) ||
            context.Verification != verification || context.PositionVerification != await ValidateNamespaceContextPositionAsync(member,
                context.Location, proof, verification, epochPosition, transaction, token).ConfigureAwait(false))
            throw new InvalidDataException("The remote update changed its retained protection or completed position verification.");
        var original = await ReadOriginalNamespaceMemberIdentityAsync(member, transaction, token).ConfigureAwait(false);
        var identity = snapshot.Identity;
        if (identity.OriginalItemId != original.ItemId || identity.OriginalRemoteId != original.RemoteId)
            throw new InvalidDataException("The remote update changed its first public identity.");
        if (identity.AcceptanceOperationId is { } acceptedId)
        {
            var accepted = await ReadNamespaceIdentityAcceptanceCoreAsync(acceptedId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote update lost its confirmed upload identity.");
            if (accepted.MemberId != member.MemberId || accepted.Confirmation.ObservedAt > preparedAt ||
                identity.ItemId != accepted.Proof.AcceptedItemId || identity.RemoteId != accepted.Proof.RemoteId)
                throw new InvalidDataException("The remote update changed its historical identity acceptance.");
        }
        else if (identity.ItemId != original.ItemId || identity.RemoteId != original.RemoteId)
            throw new InvalidDataException("The remote update cannot invent an unconfirmed identity.");
    }
}
