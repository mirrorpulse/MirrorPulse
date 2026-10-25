using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The successful public CfSharp directory reconciliation inspected by the Host.</summary>
/// <remarks>Neither this result nor a product settlement acknowledges a source upload.</remarks>
public sealed record MirrorPulseNamespaceDirectoryMovePublicResult(MirrorPulseNamespaceDirectoryMovePublicOutcome Outcome, bool NativeMoveObserved,
    bool DurableProjectionCommitted, bool RequiresFullRescan, int DurableStateEntriesUpdated, DateTimeOffset ObservedAt);

/// <summary>The two successful public reconciliation classifications accepted for product settlement.</summary>
public enum MirrorPulseNamespaceDirectoryMovePublicOutcome { Projected, AlreadyProjected }

/// <summary>Independent native, public projection and fresh protected-object evidence for one sealed member.</summary>
public sealed record MirrorPulseNamespaceDirectoryMoveMemberCompletion(Guid MemberId, Guid ParentMemberId,
    MirrorPulseNamespaceMemberMoveObservation Native, MirrorPulseNamespaceMemberMoveProjection Projection,
    MirrorPulseNamespaceMemberMoveCompletion Completion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LegacyAnchor = false);

/// <summary>An entire sealed subtree settled atomically, retaining the original opaque public proof.</summary>
/// <remarks>
/// Only the Host supplies these facts after successful public CfSharp reconciliation and fresh
/// guarded inspection of every member. Original membership, birth and permission records stay
/// immutable. All member positions, root name handover and fence release share one transaction.
/// </remarks>
public sealed record MirrorPulseNamespaceDirectoryMoveCompletion(int Version, Guid OperationId,
    string PublicEvidenceSha256, MirrorPulseNamespaceDirectoryMovePublicResult PublicResult,
    MirrorPulseNamespaceMemberMoveCompletion Root, IReadOnlyList<MirrorPulseNamespaceDirectoryMoveMemberCompletion> Entries);

public sealed partial class MirrorPulseProductCatalog
{
    private const int NamespaceDirectoryMoveCompletionLimit = 16 * 1024 * 1024;

    public async Task<MirrorPulseNamespaceDirectoryMoveCompletion> CompleteNamespaceDirectoryMoveAsync(
        MirrorPulseNamespaceDirectoryMoveCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateNamespaceDirectoryMoveCompletion(completion);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completion, TopologyJsonOptions);
        if (payload.Length > NamespaceDirectoryMoveCompletionLimit)
            throw new ArgumentException("The complete directory receipt must be bounded.", nameof(completion));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceDirectoryMoveCompletionCoreAsync(completion.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(replay, TopologyJsonOptions)))
                    throw new InvalidOperationException("An entire directory settlement is immutable.");
                transaction.Commit(); return replay;
            }
            var intent = await RequireNamespaceMemberMoveExecutionCoreAsync(completion.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            var snapshot = await ValidateNamespaceDirectoryMoveCompletionReferencesAsync(completion, transaction, cancellationToken).ConfigureAwait(false);
            var positions = new List<MirrorPulseNamespaceMemberPosition>(snapshot.Entries.Count);
            // Resolve every predecessor before inserting any completion: the current product
            // graph still names the source throughout admission and partial native recovery.
            for (int index = 0; index < snapshot.Entries.Count; index++)
            {
                var entry = snapshot.Entries[index];
                var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The sealed directory member disappeared before settlement.");
                var predecessor = await PrepareNamespaceMemberPositionSuccessorAsync(member, transaction, cancellationToken).ConfigureAwait(false);
                if (predecessor.OperationId != (entry.PreviousPositionOperationId ?? entry.PreviousMoveOperationId) ||
                    predecessor.LegacyAnchor != completion.Entries[index].LegacyAnchor)
                    throw new InvalidOperationException("Every position must succeed its exact sealed predecessor.");
                positions.Add(DirectoryMovePosition(completion, entry, index));
            }
            await CommitNamespaceMemberMoveRootAsync(intent, completion.Root, transaction, cancellationToken).ConfigureAwait(false);
            await InsertNamespaceDirectoryMoveCompletionCoreAsync(completion, payload, transaction, cancellationToken).ConfigureAwait(false);
            foreach (var position in positions)
                await InsertNamespaceMemberPositionCoreAsync(position, transaction, cancellationToken).ConfigureAwait(false);
            if (intent.Version == 3)
                await CompleteManagedRootSubtreeRenameCoreAsync(intent, completion, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return completion;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads an immutable complete subtree receipt; it does not authorize a native replay.</summary>
    public async Task<MirrorPulseNamespaceDirectoryMoveCompletion?> ReadNamespaceDirectoryMoveCompletionAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A directory move ID is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceDirectoryMoveCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private static MirrorPulseNamespaceMemberPosition DirectoryMovePosition(MirrorPulseNamespaceDirectoryMoveCompletion completion,
        MirrorPulseNamespaceDirectoryMoveEntry entry, int index) =>
        new(1, completion.OperationId, entry.MemberId, entry.PreviousPositionOperationId ?? entry.PreviousMoveOperationId,
            completion.Entries[index].ParentMemberId, completion.Entries[index].Completion.RelativePath,
            entry.Depth == 0 ? completion.OperationId : entry.PreviousMoveOperationId, completion.Root.VerifiedAt,
            completion.Entries[index].LegacyAnchor);

    private static void ValidateNamespaceDirectoryMoveCompletion(MirrorPulseNamespaceDirectoryMoveCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion.PublicResult);
        ArgumentNullException.ThrowIfNull(completion.Root);
        ValidateNamespaceMemberMoveCompletion(completion.Root);
        if (completion.Version != 1 || completion.OperationId == Guid.Empty || completion.Root.OperationId != completion.OperationId ||
            !completion.Root.IsDirectory || completion.PublicEvidenceSha256 is not { Length: 64 } ||
            completion.PublicEvidenceSha256.Any(character => !char.IsAsciiHexDigitUpper(character)) ||
            completion.Entries is null || completion.Entries.Count == 0 || completion.Entries.Count > 65_536 ||
            !Enum.IsDefined(completion.PublicResult.Outcome) || !completion.PublicResult.NativeMoveObserved || !completion.PublicResult.DurableProjectionCommitted ||
            completion.PublicResult.RequiresFullRescan || completion.PublicResult.DurableStateEntriesUpdated < 0 ||
            completion.PublicResult.ObservedAt == default || completion.PublicResult.ObservedAt >= completion.Root.VerifiedAt)
            throw new ArgumentException("Directory settlement requires bounded members and a successful complete public reconciliation.");
        var members = new HashSet<Guid>();
        var items = new HashSet<Guid>();
        foreach (var entry in completion.Entries)
        {
            if (entry is null || entry.MemberId == Guid.Empty || entry.ParentMemberId == Guid.Empty ||
                entry.ParentMemberId == entry.MemberId || !members.Add(entry.MemberId))
                throw new ArgumentException("Directory settlement requires unique members and their actual direct parents.");
            ArgumentNullException.ThrowIfNull(entry.Native);
            ArgumentNullException.ThrowIfNull(entry.Projection);
            ArgumentNullException.ThrowIfNull(entry.Completion);
            ValidateNamespaceMoveFact(entry.Native);
            ValidateNamespaceMoveFact(entry.Projection);
            ValidateNamespaceMemberMoveCompletion(entry.Completion);
            if (entry.Native.OperationId != completion.OperationId || entry.Projection.OperationId != completion.OperationId ||
                entry.Completion.OperationId != completion.OperationId || !items.Add(entry.Native.ItemId))
                throw new ArgumentException("Every settled member must retain a distinct public item in this original operation.");
        }
    }
}
