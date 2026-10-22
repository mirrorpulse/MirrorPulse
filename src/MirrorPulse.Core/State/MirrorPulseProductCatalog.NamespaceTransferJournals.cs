using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>One original public journal observation, never a replacement operation or inferred path match.</summary>
public sealed record MirrorPulseTransferJournalObservation(Guid OperationId, long Sequence, MirrorPulseWorkerChangeKind Kind,
    Guid? ItemId, string RelativePath, string? PreviousRelativePath, bool IsDirectory, DateTimeOffset ObservedAt);

/// <summary>An immutable original-ID reservation. It neither acknowledges the SDK nor completes a transfer.</summary>
/// <remarks>The Host must capture these observations from its running public feed while retaining the verified
/// original source and both root admissions. ThroughSequence is a finite scan boundary, not a native watcher drain.
/// An empty reservation represents an explicit controlled request without captured original notifications.</remarks>
public sealed record MirrorPulseNamespaceTransferJournalClaim(int Version, Guid OperationId, long ThroughSequence,
    DateTimeOffset CapturedAt, IReadOnlyList<MirrorPulseTransferJournalObservation> Members)
{
    public bool Equals(MirrorPulseNamespaceTransferJournalClaim? other) => other is not null &&
        Version == other.Version && OperationId == other.OperationId && ThroughSequence == other.ThroughSequence && CapturedAt == other.CapturedAt &&
        (Members is null || other.Members is null ? ReferenceEquals(Members, other.Members) : Members.SequenceEqual(other.Members));

    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Version); hash.Add(OperationId); hash.Add(ThroughSequence); hash.Add(CapturedAt);
        if (Members is not null) foreach (var member in Members) hash.Add(member);
        return hash.ToHashCode();
    }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Atomically reserves exact original observations before the first copy invocation.</summary>
    /// <remarks>This initial contract supports original file create/content observations with an explicit item
    /// association. Moves, directory work and item-less observations require separate proved ownership.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferJournalClaimAsync(
        MirrorPulseNamespaceTransferJournalClaim claim, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(claim); ArgumentNullException.ThrowIfNull(claim.Members);
        if (claim.Members.Count > 4096) throw new ArgumentException("The original journal reservation exceeds its recovery bound.", nameof(claim));
        claim = claim with { Members = Array.AsReadOnly(claim.Members.ToArray()) };
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(claim.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer admission is missing.");
            ValidateNamespaceTransferJournalClaim(copy.Plan, claim);
            if (copy.JournalClaim is { } retained)
            {
                if (JsonSerializer.Serialize(retained, TopologyJsonOptions) != JsonSerializer.Serialize(claim, TopologyJsonOptions))
                    throw new InvalidOperationException("The original journal reservation is immutable.");
                transaction.Commit(); return copy;
            }
            if (copy.CopyStartedAt is not null)
                throw new InvalidOperationException("Original journal ownership cannot be adopted after the first remote copy start.");
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true).ConfigureAwait(false);
            foreach (var member in claim.Members)
                await RequireNamespaceTransferIdAvailableAsync(member.OperationId, transaction, token).ConfigureAwait(false);
            copy = copy with { JournalClaim = claim };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            foreach (var member in claim.Members)
            {
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_transfer_journal_members(operation_id,transfer_id,sequence) VALUES($id,$transfer,$sequence);";
                insert.Parameters.AddWithValue("$id", member.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$transfer", copy.Plan.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$sequence", member.Sequence);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> ReadNamespaceTransferJournalOwnerAsync(Guid originalOperationId, CancellationToken token = default)
    {
        if (originalOperationId == Guid.Empty) throw new ArgumentException("An original journal ID is required.", nameof(originalOperationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            Guid? owner;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT transfer_id FROM namespace_transfer_journal_members WHERE operation_id=$id;";
                query.Parameters.AddWithValue("$id", originalOperationId.ToString("D"));
                owner = await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string id ? ParseNamespaceMemberId(id) : null;
            }
            if (owner is { } transfer)
            {
                var copy = await ReadNamespaceTransferCoreAsync(transfer, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("An original journal owner lost its transfer admission.");
                if (copy.JournalClaim is null || !copy.JournalClaim.Members.Any(member => member.OperationId == originalOperationId))
                    throw new InvalidDataException("The original journal owner disagrees with its immutable reservation.");
            }
            transaction.Commit(); return owner;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferJournalClaim(MirrorPulseNamespaceTransferPlan plan,
        MirrorPulseNamespaceTransferJournalClaim claim)
    {
        if (plan.Version != 2 || plan.CommandOrigin is null || claim.Version != 1 || claim.OperationId != plan.OperationId ||
            claim.ThroughSequence < 0 || claim.CapturedAt < plan.PreparedAt || claim.Members is null || claim.Members.Count > 4096)
            throw new ArgumentException("Original journal ownership requires the explicit original command and a bounded captured scan.");
        long previous = 0; var ids = new HashSet<Guid>();
        foreach (var member in claim.Members)
        {
            if (member is null || member.OperationId == Guid.Empty || !ids.Add(member.OperationId) ||
                new[] { plan.OperationId, plan.CopyOperationId, plan.DeleteOperationId, plan.PrivateContentId }.Contains(member.OperationId) ||
                member.Sequence <= previous || member.Sequence > claim.ThroughSequence ||
                member.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
                member.ItemId != plan.Source.Identity.ItemId || member.ItemId is null || member.ItemId == Guid.Empty ||
                member.RelativePath is null || member.RelativePath.Replace('\\', '/') != plan.Source.Context.Location.RelativePath ||
                member.PreviousRelativePath is not null || member.IsDirectory ||
                member.ObservedAt == default || member.ObservedAt > claim.CapturedAt)
                throw new ArgumentException("Every claimed original must retain its exact source item, canonical path, sequence, kind and observation time.");
            previous = member.Sequence;
        }
    }

    private async Task ValidateNamespaceTransferJournalIndexesAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,sequence FROM namespace_transfer_journal_members WHERE transfer_id=$id ORDER BY sequence LIMIT 4097;";
        query.Parameters.AddWithValue("$id", copy.Plan.OperationId.ToString("D"));
        var rows = new List<(Guid, long)>();
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add((ParseNamespaceMemberId(reader.GetString(0)), reader.GetInt64(1)));
        if (!rows.SequenceEqual(copy.JournalClaim?.Members.Select(member => (member.OperationId, member.Sequence)) ?? []))
            throw new InvalidDataException("The transfer lost its exact original journal ownership indexes.");
    }

    private static async Task EnsureNamespaceTransferJournalSchemaAsync(SqliteConnection connection,
        bool requireExisting, bool stampVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        if (requireExisting)
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='namespace_transfer_journal_members';";
            if (Convert.ToInt64(await schema.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Schema82 lost its original transfer journal ownership; it cannot recreate empty history.");
        }
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_transfer_journal_members(
                operation_id TEXT PRIMARY KEY,transfer_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),
                sequence INTEGER NOT NULL CHECK(sequence>0),UNIQUE(transfer_id,sequence));
            CREATE INDEX IF NOT EXISTS namespace_transfer_journal_owners ON namespace_transfer_journal_members(transfer_id,sequence);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (stampVersion) { schema.CommandText = "PRAGMA user_version=82;"; await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        transaction.Commit();
    }
}
