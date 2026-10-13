using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>References the actual retained restoration authority, not a retrospectively created native intent.</summary>
public sealed record MirrorPulseNamespaceReplacementPermissionReference(Guid OperationId, Guid ActionId, Guid RestorationId);

/// <summary>Fresh original-object and parent observations after a drained public restoration scope.</summary>
/// <remarks>The caller retains Host owner, scheduler, actor and native guards. Completion grants no content acceptance, journal ACK or tree readiness.</remarks>
public sealed record MirrorPulseNamespaceReplacementCompletion(int Version, Guid OperationId, Guid MemberId,
    Guid ActionId, Guid RestorationId, Guid ProtectionId, Guid RescanGeneration,
    MirrorPulseNamespaceMemberProtectionSnapshot Parent, MirrorPulseNamespacePermissionVerification Child,
    MirrorPulseNamespacePermissionVerification ParentVerification, uint LinkCount, bool IsPlaceholder,
    bool IsInSync, string PlaceholderIdentity, DateTimeOffset CompletedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Projects retained protection, queues a new root request and releases only this replacement in one transaction.</summary>
    public async Task<MirrorPulseNamespaceReplacementCompletion> CompleteNamespaceReplacementAsync(
        MirrorPulseNamespaceReplacementCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion); ValidateReplacementCompletion(completion, allowUnassignedRescan: true);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadReplacementCompletionCoreAsync(completion.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay, completion with { RescanGeneration = completion.RescanGeneration == Guid.Empty ? replay.RescanGeneration : completion.RescanGeneration }))
                    throw new InvalidOperationException("The original replacement completion is immutable.");
                transaction.Commit(); return replay;
            }
            if (completion.RescanGeneration != Guid.Empty)
                throw new InvalidOperationException("The catalog assigns a fresh rescan generation when completing replacement.");
            completion = completion with { RescanGeneration = Guid.NewGuid() };
            ValidateReplacementCompletion(completion);
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(completion.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            MirrorPulseNamespaceReplacementAction action = RequireReplacementCompletionReferences(completion, execution);
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(execution.Intent.RootId, null, transaction, cancellationToken,
                allowedReplacement: completion.OperationId).ConfigureAwait(false);
            await RequireReplacementIdAvailableCoreAsync(completion.ProtectionId, transaction, cancellationToken).ConfigureAwait(false);
            await using (var identities = _connection.CreateCommand())
            {
                identities.Transaction = transaction;
                identities.CommandText = "SELECT 1 FROM namespace_members WHERE member_id=$id UNION ALL SELECT 1 FROM namespace_birth_protections WHERE protection_id=$id;";
                identities.Parameters.AddWithValue("$id", completion.ProtectionId.ToString("D"));
                if (await identities.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("Completion cannot borrow an existing member or protection identity.");
            }
            var member = await ReadNamespaceMemberCoreAsync(completion.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The admitted member disappeared.");
            if (member != execution.Intent.Original.Context.Member ||
                await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Completion requires the original active membership.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(completion.Parent.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (location != execution.Intent.Original.Context.Location || identity != execution.Intent.Original.Identity || parent is null ||
                !SameNamespaceReplacementPayload(parent, completion.Parent) || parent.Context.Protection.RoleSid != action.RoleSid)
                throw new InvalidOperationException("Completion requires the retained member position and identity and the actual current protected parent.");
            await ProjectReplacementProtectionCoreAsync(completion, execution, member, action, transaction, cancellationToken).ConfigureAwait(false);
            await ReadRootRescanRequestsCoreAsync(_connection, transaction, cancellationToken).ConfigureAwait(false);
            await SaveRootRescanRequestCoreAsync(_connection, transaction,
                new(execution.Intent.RootId, completion.RescanGeneration, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            byte[] payload = NamespaceReplacementPayload(completion);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO namespace_replacement_completions(operation_id,member_id,restoration_id,protection_id,payload,fingerprint)
                    VALUES($operation,$member,$restoration,$protection,$payload,$fingerprint);
                    """;
                insert.Parameters.AddWithValue("$operation", completion.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$member", completion.MemberId.ToString("D"));
                insert.Parameters.AddWithValue("$restoration", completion.RestorationId.ToString("D"));
                insert.Parameters.AddWithValue("$protection", completion.ProtectionId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var release = _connection.CreateCommand())
            {
                release.Transaction = transaction;
                release.CommandText = "DELETE FROM namespace_replacement_pending WHERE root_id=$root AND member_id=$member AND operation_id=$operation;";
                release.Parameters.AddWithValue("$root", execution.Intent.RootId.ToString());
                release.Parameters.AddWithValue("$member", completion.MemberId.ToString("D"));
                release.Parameters.AddWithValue("$operation", completion.OperationId.ToString("D"));
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The original replacement recovery reservation disappeared.");
            }
            // A projection must also be readable through the existing coherent member view.
            var projected = await ReadNamespaceMemberProtectionSnapshotCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (projected?.Context.Protection.ProtectionId != completion.ProtectionId || projected.Context.Verification != completion.Child)
                throw new InvalidDataException("The completed protection is not coherent with current membership.");
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceReplacementCompletion?> ReadNamespaceReplacementCompletionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        RequireNamespaceReplacementId(operationId); await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadReplacementCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task ProjectReplacementProtectionCoreAsync(MirrorPulseNamespaceReplacementCompletion completion,
        MirrorPulseNamespaceReplacementExecution execution, MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceReplacementAction action, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var latest = await ReadLatestPermissionChangeCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false);
            if (latest?.Intent.OperationId != execution.Intent.Original.Context.Protection.ProtectionId ||
                latest.Phase != MirrorPulseNamespacePermissionPhase.Verified || latest.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                throw new InvalidOperationException("The original member's protection changed after replacement admission.");
            await WritePermissionChangeCoreAsync(ReplacementPermissionProjection(completion, execution, member, action), token, transaction, insert: true).ConfigureAwait(false);
        }
        else
        {
            var latest = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The born member's original protection is missing.");
            if (latest.ProtectionId != execution.Intent.Original.Context.Protection.ProtectionId)
                throw new InvalidOperationException("The born member's protection changed after replacement admission.");
            var protection = ReplacementBirthProtection(completion, execution, latest, action);
            ValidateNamespaceBirthProtection(protection);
            await RecordNamespaceBirthProtectionCoreAsync(protection, transaction, token, completion.OperationId).ConfigureAwait(false);
        }
    }

    private static MirrorPulseNamespaceBirthProtection ReplacementBirthProtection(MirrorPulseNamespaceReplacementCompletion completion,
        MirrorPulseNamespaceReplacementExecution execution, MirrorPulseNamespaceBirthProtection previous, MirrorPulseNamespaceReplacementAction action)
    {
        var placement = execution.Intent.Original.Context.Location;
        var parent = completion.Parent.Context;
        return previous with
        {
            Version = 3,
            ProtectionId = completion.ProtectionId,
            PreviousProtectionId = previous.ProtectionId,
            RoleSid = action.RoleSid,
            Verification = completion.Child,
            Placement = new(completion.MemberId, placement.CurrentPositionOperationId, parent.Member.MemberId,
                parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId, placement.RelativePath)
        };
    }

    private static MirrorPulseNamespacePermissionChange ReplacementPermissionProjection(MirrorPulseNamespaceReplacementCompletion completion,
        MirrorPulseNamespaceReplacementExecution execution, MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceReplacementAction action)
    {
        var location = execution.Intent.Original.Context.Location;
        var intent = new MirrorPulseNamespacePermissionIntent(completion.ProtectionId, member.OriginId, member.LocalObject, member.RootId,
            location.RelativePath, MirrorPulseNamespacePermissionChangeKind.ReplacementProjection, action.RoleSid, action.RoleOnlyDacl,
            action.RestoreDacl, execution.Restoration!.PreparedAt)
        {
            Relocation = location.CurrentPositionOperationId is { } position ? new(member.MemberId, position) : null,
            ReplacementProjection = new(completion.OperationId, completion.ActionId, completion.RestorationId)
        };
        // The before-scope preparation exists in the replacement ledger. AppliedAt is the
        // retained observation of application, never an invented exact native write time.
        return new(intent, MirrorPulseNamespacePermissionPhase.Verified, execution.RestorationResult!.Verification!.ObservedAt, completion.Child);
    }

    private async Task ValidateReplacementPermissionProjectionAsync(MirrorPulseNamespacePermissionChange change,
        SqliteTransaction? transaction, CancellationToken token)
    {
        if (change.Intent.ReplacementProjection is not { } reference) return;
        if (transaction is null)
        {
            using var owned = _connection.BeginTransaction(deferred: true);
            await ValidateReplacementPermissionProjectionAsync(change, owned, token).ConfigureAwait(false); owned.Commit(); return;
        }
        var completion = await ReadReplacementCompletionCoreAsync(reference.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Replacement permission projection lost its completed restoration.");
        var execution = await ReadNamespaceReplacementExecutionCoreAsync(reference.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Replacement permission projection lost its original admission.");
        var member = execution.Intent.Original.Context.Member;
        var action = RequireReplacementCompletionReferences(completion, execution);
        var expected = ReplacementPermissionProjection(completion, execution, member, action);
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || member.IsDirectory || change != expected)
            throw new InvalidDataException("Replacement permission projection does not match its retained original object and actual scope.");
    }

    private async Task<MirrorPulseNamespaceReplacementCompletion?> ReadReplacementCompletionCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseNamespaceReplacementCompletion completion;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id,restoration_id,protection_id,payload,fingerprint FROM namespace_replacement_completions WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            completion = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementCompletion>((byte[])reader[3], (byte[])reader[4], value => ValidateReplacementCompletion(value));
            if (completion.OperationId != operationId || reader.GetString(0) != completion.MemberId.ToString("D") ||
                reader.GetString(1) != completion.RestorationId.ToString("D") || reader.GetString(2) != completion.ProtectionId.ToString("D"))
                throw new InvalidDataException("Replacement completion disagrees with its retained indexes.");
        }
        var execution = await ReadNamespaceReplacementExecutionCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Replacement completion lost its original execution.");
        MirrorPulseNamespaceReplacementAction action;
        try { action = RequireReplacementCompletionReferences(completion, execution); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Replacement completion lost its actual restoration authority.", error); }
        var member = execution.Intent.Original.Context.Member;
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            // Read the projection row without following its backlink to this completion.
            // The exact derived row is checked here, so no recursive permission graph is needed.
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = PermissionChangeQuery + " WHERE c.operation_id=$id;";
            query.Parameters.AddWithValue("$id", completion.ProtectionId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || ReadPermissionChange(reader) != ReplacementPermissionProjection(completion, execution, member, action))
                throw new InvalidDataException("Replacement completion lost its exact original-member permission projection.");
        }
        else
        {
            var previous = await ReadNamespaceBirthProtectionRowAsync(execution.Intent.Original.Context.Protection.ProtectionId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Replacement completion lost the born member's earlier epoch.");
            var projected = await ReadNamespaceBirthProtectionRowAsync(completion.ProtectionId, transaction, token).ConfigureAwait(false);
            if (projected is null || projected.Value.Sequence <= previous.Sequence ||
                projected.Value.Protection != ReplacementBirthProtection(completion, execution, previous.Protection, action))
                throw new InvalidDataException("Replacement completion lost its exact born-member protection projection.");
        }
        return completion;
    }

    private static MirrorPulseNamespaceReplacementAction RequireReplacementCompletionReferences(MirrorPulseNamespaceReplacementCompletion completion,
        MirrorPulseNamespaceReplacementExecution execution)
    {
        var intent = execution.Intent;
        if (execution.Cancellation is not null || execution.Restoration is not { } scope ||
            execution.RestorationResult is not { ProductVerified: true, Drained: true } result || execution.ContentCompletion is null ||
            completion.OperationId != intent.OperationId || completion.MemberId != intent.MemberId ||
            completion.ActionId != scope.ActionId || completion.RestorationId != scope.RestorationId || completion.CompletedAt < result.RecordedAt)
            throw new InvalidOperationException("Replacement completion requires the latest verified and drained restoration of its admitted original member.");
        var action = RequireReplacementRestorationAuthority(scope, execution, completion.CompletedAt, requireCurrent: true);
        var parent = completion.Parent;
        if (parent.Context.Member.MemberId != intent.Original.Context.Location.ParentMemberId || !parent.Context.Member.IsDirectory ||
            parent.Context.Member.RootId != intent.RootId || parent.Context.Protection.RoleSid != action.RoleSid ||
            completion.Child.LocalObject != action.LocalObject || completion.Child.OwnerSid != action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, completion.Child.Dacl) ||
            completion.ParentVerification.LocalObject != parent.Context.Member.LocalObject || completion.ParentVerification.OwnerSid != action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parent.ExpectedDacl, completion.ParentVerification.Dacl) ||
            completion.Child.ObservedAt < result.RecordedAt || completion.ParentVerification.ObservedAt < result.RecordedAt ||
            completion.ParentVerification.ObservedAt < parent.Context.Verification.ObservedAt ||
            parent.Context.PositionVerification is { } position && completion.ParentVerification.ObservedAt < position.ObservedAt ||
            completion.Child.ObservedAt > completion.CompletedAt || completion.ParentVerification.ObservedAt > completion.CompletedAt ||
            completion.PlaceholderIdentity != intent.OriginalPlaceholderIdentity || completion.IsInSync && execution.Backup!.Content != intent.Content)
            throw new InvalidOperationException("Completion requires fresh original-object, current-role parent and closed descriptor observations without inventing accepted content.");
        return action;
    }

    private static void ValidateReplacementCompletion(MirrorPulseNamespaceReplacementCompletion completion, bool allowUnassignedRescan = false)
    {
        ArgumentNullException.ThrowIfNull(completion.Parent); ArgumentNullException.ThrowIfNull(completion.Parent.Context);
        ArgumentNullException.ThrowIfNull(completion.Parent.Context.Member); ArgumentNullException.ThrowIfNull(completion.Parent.Context.Protection);
        ArgumentNullException.ThrowIfNull(completion.Parent.Context.Verification); ValidatePermissionDacl(completion.Parent.ExpectedDacl);
        ArgumentNullException.ThrowIfNull(completion.Child); ArgumentNullException.ThrowIfNull(completion.ParentVerification);
        ValidatePermissionBinding(completion.Child.LocalObject); ValidatePermissionBinding(completion.ParentVerification.LocalObject);
        ValidatePermissionSid(completion.Child.OwnerSid, role: false); ValidatePermissionSid(completion.ParentVerification.OwnerSid, role: false);
        ValidatePermissionDacl(completion.Child.Dacl); ValidatePermissionDacl(completion.ParentVerification.Dacl);
        Guid[] ids = allowUnassignedRescan && completion.RescanGeneration == Guid.Empty ?
            [completion.OperationId, completion.MemberId, completion.ActionId, completion.RestorationId, completion.ProtectionId] :
            [completion.OperationId, completion.MemberId, completion.ActionId, completion.RestorationId, completion.ProtectionId, completion.RescanGeneration];
        if (completion.Version != 1 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length ||
            completion.Child.ObservedAt == default || completion.ParentVerification.ObservedAt == default || completion.CompletedAt == default ||
            completion.LinkCount != 1 || !completion.IsPlaceholder || string.IsNullOrWhiteSpace(completion.PlaceholderIdentity))
            throw new ArgumentException("Replacement completion requires separate identities and an original single-link placeholder.");
    }

    private static async Task EnsureNamespaceReplacementCompletionSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacement_completions (
                operation_id TEXT PRIMARY KEY REFERENCES namespace_replacements(operation_id),
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                restoration_id TEXT NOT NULL REFERENCES namespace_replacement_restoration_results(restoration_id),
                protection_id TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
