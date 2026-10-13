using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>An immutable before-scope restoration attempt for copied content on the original member.</summary>
/// <remarks>The Host owns scheduler and actor admission. Superseding an interrupted owner grants no authority to overlap a live native scope.</remarks>
public sealed record MirrorPulseNamespaceReplacementRestoration(int Version, Guid OperationId, Guid RestorationId,
    Guid? PreviousRestorationId, Guid ActionId, MirrorPulseNamespaceMemberProtectionSnapshot Parent, DateTimeOffset PreparedAt)
{
    /// <summary>Explicit before-scope revalidation for completion; historical status replay remains read-only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RevalidateForCompletion { get; init; }
}

/// <summary>The actual public scope result and a separate product protection observation.</summary>
/// <remarks>Native success and product verification are distinct. Neither releases a root, accepts content or acknowledges journal IDs.</remarks>
public sealed record MirrorPulseNamespaceReplacementRestorationResult(int Version, Guid OperationId, Guid RestorationId,
    string NativeOutcome, string NativeStage, MirrorPulseLocalFileBinding ExpectedObject,
    bool CallbackStarted, bool CallbackCompleted, bool NativeIdentityPrepared, bool NativeConverted,
    bool DurableProjectionCommitted, bool AccessDescriptorApplied, bool AccessDescriptorReadBack, bool Drained,
    int? PreparationHResult, long? PreparationUsn, int? ErrorHResult,
    MirrorPulseNamespacePermissionVerification? Verification, MirrorPulseNamespacePermissionVerification? ParentVerification,
    uint? LinkCount, bool? IsPlaceholder, bool? IsInSync, string? PlaceholderIdentity,
    bool ProductVerified, DateTimeOffset RecordedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceReplacementRestoration> PrepareNamespaceReplacementRestorationAsync(
        MirrorPulseNamespaceReplacementRestoration restoration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restoration); ValidateReplacementRestoration(restoration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(restoration.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            var replay = await ReadReplacementRestorationRowCoreAsync(restoration.RestorationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay.Value.Value, restoration)) throw new InvalidOperationException("The original restoration attempt is immutable.");
                transaction.Commit(); return replay.Value.Value;
            }
            await RequireReplacementIdAvailableCoreAsync(restoration.RestorationId, transaction, cancellationToken).ConfigureAwait(false);
            RequireReplacementRestorationReferences(restoration, execution, requireCurrent: true);
            if (restoration.PreviousRestorationId != execution.Restoration?.RestorationId ||
                execution.RestorationResult is { ProductVerified: true } && execution.Restoration?.ActionId == restoration.ActionId && !restoration.RevalidateForCompletion || execution.RestorationResult is { Drained: false } ||
                execution.Restoration is { } previous && restoration.PreparedAt < previous.PreparedAt ||
                execution.RestorationResult is { } result && restoration.PreparedAt < result.RecordedAt)
                throw new InvalidOperationException("A new restoration must retain its latest predecessor and cannot supersede verified or undrained work.");
            if (restoration.RevalidateForCompletion && (execution.RestorationResult is not { ProductVerified: true, Drained: true } ||
                execution.Restoration?.ActionId != restoration.ActionId))
                throw new InvalidOperationException("Completion revalidation must follow a drained verified scope under the same retained action.");
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(execution.Intent.RootId, null, transaction, cancellationToken,
                allowedReplacement: restoration.OperationId).ConfigureAwait(false);
            var currentParent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(restoration.Parent.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (currentParent is null || !SameNamespaceReplacementPayload(currentParent, restoration.Parent))
                throw new InvalidOperationException("The admitted current parent protection changed before restoration.");
            await using (var count = _connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM namespace_replacement_restorations WHERE operation_id=$operation;";
                count.Parameters.AddWithValue("$operation", restoration.OperationId.ToString("D"));
                if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= 4096)
                    throw new InvalidOperationException("Restoration reached its retained attempt bound.");
            }
            byte[] payload = NamespaceReplacementPayload(restoration);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_replacement_restorations(restoration_id,operation_id,previous_id,payload,fingerprint) VALUES($id,$operation,$previous,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", restoration.RestorationId.ToString("D")); insert.Parameters.AddWithValue("$operation", restoration.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", restoration.PreviousRestorationId is { } predecessor ? predecessor.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); transaction.Commit(); return restoration;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceReplacementRestorationResult> RecordNamespaceReplacementRestorationResultAsync(
        MirrorPulseNamespaceReplacementRestorationResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result); ValidateReplacementRestorationResult(result);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var execution = await ReadNamespaceReplacementExecutionCoreAsync(result.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            var restoration = await ReadReplacementRestorationRowCoreAsync(result.RestorationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The before-scope restoration attempt is missing.");
            RequireReplacementRestorationReferences(restoration.Value, execution);
            RequireReplacementRestorationResultReferences(result, restoration.Value, execution);
            var replay = await ReadReplacementRestorationResultCoreAsync(result.RestorationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!SameNamespaceReplacementPayload(replay, result)) throw new InvalidOperationException("The first native restoration result is immutable.");
                transaction.Commit(); return replay;
            }
            if (execution.Restoration?.RestorationId != result.RestorationId)
                throw new InvalidOperationException("A superseded restoration cannot acquire a new late result.");
            _ = RequireReplacementRestorationAuthority(restoration.Value, execution, result.RecordedAt, requireCurrent: true);
            await RequireNamespaceReplacementPendingCoreAsync(execution.Intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = NamespaceReplacementPayload(result);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_replacement_restoration_results(restoration_id,operation_id,payload,fingerprint) VALUES($id,$operation,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", result.RestorationId.ToString("D")); insert.Parameters.AddWithValue("$operation", result.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseNamespaceReplacementRestoration? Attempt, MirrorPulseNamespaceReplacementRestorationResult? Result)>
        ReadReplacementRestorationHeadCoreAsync(MirrorPulseNamespaceReplacementExecution execution, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? headId = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT restoration_id FROM namespace_replacement_restorations WHERE operation_id=$operation ORDER BY sequence DESC LIMIT 1;";
            query.Parameters.AddWithValue("$operation", execution.Intent.OperationId.ToString("D"));
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string stored) headId = ParseNamespaceMemberId(stored);
        }
        await using (var orphan = _connection.CreateCommand())
        {
            orphan.Transaction = transaction;
            orphan.CommandText = """
                SELECT 1 FROM namespace_replacement_restoration_results r
                WHERE r.operation_id=$operation AND NOT EXISTS
                    (SELECT 1 FROM namespace_replacement_restorations a WHERE a.restoration_id=r.restoration_id AND a.operation_id=r.operation_id) LIMIT 1;
                """;
            orphan.Parameters.AddWithValue("$operation", execution.Intent.OperationId.ToString("D"));
            if (await orphan.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A restoration result lost its original operation and before-scope attempt.");
        }
        if (headId is null) return (null, null);
        var head = await ReadReplacementRestorationRowCoreAsync(headId.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The latest restoration lost its original intent.");
        var headResult = await ReadReplacementRestorationResultCoreAsync(headId.Value, transaction, token).ConfigureAwait(false);
        var current = head;
        var visited = new HashSet<Guid>();
        try
        {
            while (true)
            {
                if (!visited.Add(current.Value.RestorationId) || visited.Count > 4096) throw new InvalidOperationException("The restoration chain is cyclic or unbounded.");
                RequireReplacementRestorationReferences(current.Value, execution);
                var result = await ReadReplacementRestorationResultCoreAsync(current.Value.RestorationId, transaction, token).ConfigureAwait(false);
                if (result is not null) RequireReplacementRestorationResultReferences(result, current.Value, execution);
                if (current.Value.PreviousRestorationId is not { } previous) break;
                var predecessor = await ReadReplacementRestorationRowCoreAsync(previous, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A restoration lost its predecessor.");
                var priorResult = await ReadReplacementRestorationResultCoreAsync(previous, transaction, token).ConfigureAwait(false);
                if (predecessor.Sequence >= current.Sequence || current.Value.PreparedAt < predecessor.Value.PreparedAt ||
                    current.Value.RevalidateForCompletion && (priorResult is not { ProductVerified: true, Drained: true } || predecessor.Value.ActionId != current.Value.ActionId) ||
                    priorResult is { ProductVerified: true } && predecessor.Value.ActionId == current.Value.ActionId && !current.Value.RevalidateForCompletion ||
                    priorResult is { Drained: false } || priorResult is not null && current.Value.PreparedAt < priorResult.RecordedAt)
                    throw new InvalidOperationException("The restoration predecessor order is inconsistent.");
                current = predecessor;
            }
            await using var count = _connection.CreateCommand(); count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM namespace_replacement_restorations WHERE operation_id=$operation;";
            count.Parameters.AddWithValue("$operation", execution.Intent.OperationId.ToString("D"));
            if ((long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))! != visited.Count)
                throw new InvalidOperationException("The restoration head does not own every retained attempt.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("The replacement restoration history is inconsistent.", error); }
        return (head.Value, headResult);
    }

    private async Task<(long Sequence, MirrorPulseNamespaceReplacementRestoration Value)?> ReadReplacementRestorationRowCoreAsync(
        Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT sequence,operation_id,previous_id,payload,fingerprint FROM namespace_replacement_restorations WHERE restoration_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementRestoration>((byte[])reader[3], (byte[])reader[4], ValidateReplacementRestoration);
        if (value.RestorationId != id || reader.GetString(1) != value.OperationId.ToString("D") ||
            (reader.IsDBNull(2) ? null : reader.GetString(2)) != value.PreviousRestorationId?.ToString("D"))
            throw new InvalidDataException("The restoration intent and its original indexes disagree.");
        return (reader.GetInt64(0), value);
    }

    private async Task<MirrorPulseNamespaceReplacementRestorationResult?> ReadReplacementRestorationResultCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,payload,fingerprint FROM namespace_replacement_restoration_results WHERE restoration_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var value = ReadNamespaceReplacementPayload<MirrorPulseNamespaceReplacementRestorationResult>((byte[])reader[1], (byte[])reader[2], ValidateReplacementRestorationResult);
        if (value.RestorationId != id || reader.GetString(0) != value.OperationId.ToString("D"))
            throw new InvalidDataException("The native restoration result and its original indexes disagree.");
        return value;
    }

    private static void RequireReplacementRestorationReferences(MirrorPulseNamespaceReplacementRestoration restoration, MirrorPulseNamespaceReplacementExecution execution,
        bool requireCurrent = false)
    {
        var action = RequireReplacementRestorationAuthority(restoration, execution, restoration.PreparedAt, requireCurrent);
        if (execution.ContentCompletion is not { } copied || restoration.OperationId != execution.Intent.OperationId ||
            restoration.Parent.Context.Member.MemberId != execution.Intent.Original.Context.Location.ParentMemberId ||
            restoration.Parent.Context.Member.RootId != execution.Intent.RootId || action.LocalObject != execution.Action!.LocalObject ||
            restoration.PreparedAt < copied.Verification.ObservedAt)
            throw new InvalidOperationException("Restoration requires copied content, its original action and the admitted current managed parent.");
    }

    private static void RequireReplacementRestorationResultReferences(MirrorPulseNamespaceReplacementRestorationResult result,
        MirrorPulseNamespaceReplacementRestoration restoration, MirrorPulseNamespaceReplacementExecution execution)
    {
        var action = RequireReplacementRestorationAuthority(restoration, execution, result.RecordedAt, requireCurrent: false);
        if (result.OperationId != restoration.OperationId || result.RestorationId != restoration.RestorationId ||
            result.ExpectedObject != action.LocalObject || result.RecordedAt < restoration.PreparedAt)
            throw new InvalidOperationException("The actual native result requires its before-scope intent and original requested binding.");
        if (!result.ProductVerified) return;
        var parent = restoration.Parent;
        if (result.NativeOutcome != "Completed" || !result.CallbackStarted || !result.CallbackCompleted || !result.NativeIdentityPrepared ||
            !result.DurableProjectionCommitted || !result.AccessDescriptorApplied || !result.AccessDescriptorReadBack || !result.Drained ||
            result.ErrorHResult is not (null or 0) || result.Verification is not { } verification || result.ParentVerification is not { } parentVerification ||
            verification.LocalObject != action.LocalObject || verification.OwnerSid != action.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, verification.Dacl) ||
            parentVerification.LocalObject != parent.Context.Member.LocalObject || parentVerification.OwnerSid != parent.Context.Verification.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parent.ExpectedDacl, parentVerification.Dacl) ||
            verification.ObservedAt < restoration.PreparedAt || parentVerification.ObservedAt < restoration.PreparedAt ||
            verification.ObservedAt > result.RecordedAt || parentVerification.ObservedAt > result.RecordedAt ||
            result.LinkCount != 1 || result.IsPlaceholder != true || result.IsInSync is null ||
            result.PlaceholderIdentity != execution.Intent.OriginalPlaceholderIdentity ||
            result.IsInSync == true && execution.Backup!.Content != execution.Intent.Content)
            throw new InvalidOperationException("Product restoration verification requires the completed public scope, drained projection and separate original-object and parent protection observations.");
    }

    private static MirrorPulseNamespaceReplacementAction RequireReplacementRestorationAuthority(MirrorPulseNamespaceReplacementRestoration restoration,
        MirrorPulseNamespaceReplacementExecution execution, DateTimeOffset observedAt, bool requireCurrent)
    {
        var first = execution.Action ?? throw new InvalidOperationException("The native restoration lost its first permission action.");
        return RequireReplacementCopyAuthority(execution, restoration.ActionId == first.ActionId ? null : restoration.ActionId, observedAt, requireCurrent);
    }

    private static void ValidateReplacementRestoration(MirrorPulseNamespaceReplacementRestoration restoration)
    {
        ArgumentNullException.ThrowIfNull(restoration); ArgumentNullException.ThrowIfNull(restoration.Parent);
        ArgumentNullException.ThrowIfNull(restoration.Parent.Context); ArgumentNullException.ThrowIfNull(restoration.Parent.Context.Member);
        ArgumentNullException.ThrowIfNull(restoration.Parent.Context.Verification);
        ArgumentNullException.ThrowIfNull(restoration.Parent.Context.Protection); ArgumentNullException.ThrowIfNull(restoration.Parent.Context.Location);
        if (restoration.Version != 1 || restoration.OperationId == Guid.Empty || restoration.RestorationId == Guid.Empty || restoration.ActionId == Guid.Empty ||
            restoration.RestorationId == restoration.OperationId || restoration.RestorationId == restoration.ActionId || restoration.PreviousRestorationId == Guid.Empty ||
            restoration.RevalidateForCompletion && restoration.PreviousRestorationId is null ||
            restoration.PreviousRestorationId == restoration.RestorationId || restoration.PreparedAt == default || !restoration.Parent.Context.Member.IsDirectory ||
            NamespaceReplacementPayload(restoration).Length > 262_144)
            throw new ArgumentException("Restoration requires its distinct IDs, coherent managed parent and before-scope time.", nameof(restoration));
    }

    private static void ValidateReplacementRestorationResult(MirrorPulseNamespaceReplacementRestorationResult result)
    {
        ArgumentNullException.ThrowIfNull(result); ValidatePermissionBinding(result.ExpectedObject);
        if (result.Version != 1 || result.OperationId == Guid.Empty || result.RestorationId == Guid.Empty || result.RecordedAt == default ||
            result.NativeOutcome is not ("Completed" or "Busy" or "Unsupported" or "LocalObjectMismatch" or "NotApplicable" or "ProtectionLost" or
                "NativeAppliedProjectionPending" or "CallbackFailed" or "Canceled" or "DeadlineExceeded" or "Failed") ||
            result.NativeStage is not ("Admission" or "Open" or "Inspection" or "NativePreparation" or "Projection" or "Callback" or "AccessDescriptor" or "Draining" or "Released") ||
            result.CallbackCompleted && !result.CallbackStarted || result.PlaceholderIdentity is { Length: > 5464 } || NamespaceReplacementPayload(result).Length > 262_144)
            throw new ArgumentException("The native restoration result requires its bounded actual scope fields.", nameof(result));
        foreach (var verification in new[] { result.Verification, result.ParentVerification })
        {
            if (verification is null) continue;
            ValidatePermissionBinding(verification.LocalObject); ValidatePermissionSid(verification.OwnerSid, role: false); ValidatePermissionDacl(verification.Dacl);
            if (verification.ObservedAt == default) throw new ArgumentException("A restoration observation requires its actual time.", nameof(result));
        }
    }

    private static async Task EnsureNamespaceReplacementRestorationSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacement_restorations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                restoration_id TEXT NOT NULL UNIQUE,
                operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
                previous_id TEXT NULL REFERENCES namespace_replacement_restorations(restoration_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE INDEX IF NOT EXISTS namespace_replacement_restorations_owner ON namespace_replacement_restorations(operation_id,sequence);
            CREATE TABLE IF NOT EXISTS namespace_replacement_restoration_results (
                restoration_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacement_restorations(restoration_id),
                operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
