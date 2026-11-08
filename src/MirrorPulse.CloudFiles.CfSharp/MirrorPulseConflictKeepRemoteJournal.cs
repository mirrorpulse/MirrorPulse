using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retained original SDK rows, separate from native application, a durable store identity or an ACK.</summary>
internal sealed record MirrorPulseKeepRemoteJournalOriginal(Guid OperationId, long Sequence, Guid ItemId,
    CloudStateOperationKind Kind, string PayloadSha256, DateTimeOffset CreatedAt, int AttemptCount, DateTimeOffset? RetryAfter);

internal sealed record MirrorPulseKeepRemoteJournalSnapshot(Guid CommandId, Guid PlanId, Guid ItemId,
    string IntentSha256, IReadOnlyList<MirrorPulseKeepRemoteJournalOriginal> Originals);

internal sealed class MirrorPulseKeepRemoteJournalDeferredException(bool requiresFullRescan) : IOException(
    requiresFullRescan ? "KeepRemote is waiting for official journal reconciliation."
        : "KeepRemote is waiting for later metadata to settle.")
{
    internal bool RequiresFullRescan { get; } = requiresFullRescan;
    internal CloudProtectedLocalOperationResult? SealResult { get; set; }
}

/// <summary>Checks complete original journal ownership before a separately authorized native KeepRemote application.</summary>
/// <remarks>
/// Uses only the public SDK store, typed feed and existing product mapper. It never decodes SDK
/// payloads, acknowledges rows, changes retry state or supplies native execution authority.
/// The feed's scan scope identifies a consumer lifetime and is deliberately not retained as a store identity.
/// </remarks>
internal sealed partial class MirrorPulseConflictKeepRemoteJournal(ICloudStateStore store, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router,
    Func<IReadOnlyList<MirrorPulseJournalOperationReference>, CancellationToken, ValueTask<IReadOnlyList<MirrorPulseLocalChangeObservation>>> readOriginals)
{
    internal MirrorPulseConflictKeepRemoteJournal(ICloudStateStore store, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, CloudLocalChangeFeed feed)
        : this(store, catalog, router, (members, token) => ReadPublicOriginalsAsync(feed, members, token)) { }

    internal Task<MirrorPulseKeepRemoteJournalSnapshot> CaptureAsync(MirrorPulseConflictKeepRemoteCopies copies,
        CancellationToken token = default) => CaptureCoreAsync(copies, true, token);

    private async Task<MirrorPulseKeepRemoteJournalSnapshot> CaptureCoreAsync(MirrorPulseConflictKeepRemoteCopies copies,
        bool requireNoSuccessors, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies);
        var retained = await RequireCopiesAsync(copies, token).ConfigureAwait(false);
        var intent = retained.Intent;
        var plan = await catalog.ReadJournalCoalescingOwnerAsync(intent.OriginalOperationIds[0], token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote journal group is missing.");
        if (plan.PlanId != intent.PlanId || plan.ItemId != intent.ItemId || plan.InstanceId != intent.InstanceId ||
            plan.RootKey != intent.RemoteSource.RootKey || !plan.Members.Select(member => member.OperationId).SequenceEqual(intent.OriginalOperationIds))
            throw new InvalidDataException("The original KeepRemote group changed its complete journal ownership.");
        var before = await ReadRowsAsync(plan, requireNoSuccessors, token).ConfigureAwait(false);
        var observations = await readOriginals(plan.Members, token).ConfigureAwait(false);
        if (observations.Count != plan.Members.Count || observations.Any(observation => observation.ItemId != plan.ItemId || observation.IsDirectory) ||
            observations.Select(observation => observation.OperationId).Distinct().Count() != observations.Count)
            throw new InvalidDataException("KeepRemote requires every original typed file observation, without a missing or foreign item.");
        var mapped = MirrorPulseLocalBatchMapper.MapObservations(observations, router);
        if (mapped.RequiresFullRescan || mapped.BlockedOperations is { Count: > 0 } || mapped.Commands.Count != plan.Members.Count)
            throw new InvalidDataException("The original KeepRemote observations cannot be routed completely.");
        var commands = mapped.Commands.ToDictionary(command => command.OperationId);
        foreach (var member in plan.Members)
        {
            if (!commands.TryGetValue(member.OperationId, out var command) || command.Sequence != member.Sequence ||
                command.ItemId != plan.ItemId || command.InstanceId != plan.InstanceId || command.RootKey != plan.RootKey ||
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command with { ItemId = plan.ItemId }))) != member.Fingerprint)
                throw new InvalidDataException("An original typed KeepRemote observation changed after its retained planning window.");
        }
        var after = await ReadRowsAsync(plan, requireNoSuccessors, token).ConfigureAwait(false);
        if (!before.SequenceEqual(after))
            throw new InvalidDataException("The original KeepRemote journal changed during public decoding.");
        var current = await RequireCopiesAsync(copies, token).ConfigureAwait(false);
        string fingerprint = Fingerprint(intent);
        if (Fingerprint(current.Intent) != fingerprint)
            throw new InvalidDataException("The selected KeepRemote decision changed during journal validation.");
        return new(intent.CommandId, plan.PlanId, plan.ItemId, fingerprint, Array.AsReadOnly(after));
    }

    internal async Task RevalidateAsync(MirrorPulseKeepRemoteJournalSnapshot original, MirrorPulseConflictKeepRemoteCopies copies,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        var current = await CaptureAsync(copies, token).ConfigureAwait(false);
        if (original.CommandId != current.CommandId || original.PlanId != current.PlanId || original.ItemId != current.ItemId ||
            original.IntentSha256 != current.IntentSha256 || !original.Originals.SequenceEqual(current.Originals))
            throw new InvalidDataException("KeepRemote cannot reuse an earlier original journal snapshot after its contents changed.");
    }

    private async Task<MirrorPulseConflictKeepRemotePreparation> RequireCopiesAsync(MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        var retained = await catalog.ReadConflictKeepRemoteAsync(copies.Preparation.Intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote decision disappeared.");
        if (Fingerprint(retained.Intent) != Fingerprint(copies.Preparation.Intent) || retained.LocalPreservation is null || retained.RemoteCopy is null ||
            retained.LocalPreservation.Copy != copies.Local?.Proof || retained.RemoteCopy != copies.Remote.Proof ||
            !copies.Remote.Data.CanRead || copies.Local is { Data.CanRead: false })
            throw new InvalidDataException("KeepRemote journal validation requires both complete originally selected private copy leases.");
        return retained;
    }

    private async Task<MirrorPulseKeepRemoteJournalOriginal[]> ReadRowsAsync(MirrorPulseJournalCoalescingPlan plan,
        bool requireNoSuccessors, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
        // Read one extra row to detect a successor during first admission. After a retained
        // permission action, preserve every later observation while checking the original set.
        var rows = await transaction.Operations.ListByItemIdAsync(plan.ItemId, checked(plan.Members.Count + 1), token).ConfigureAwait(false);
        if (rows.Count < plan.Members.Count)
            throw new InvalidDataException("KeepRemote requires every original pending row.");
        var ordered = rows.OrderBy(row => row.Sequence).ToArray();
        var result = new MirrorPulseKeepRemoteJournalOriginal[plan.Members.Count];
        for (int index = 0; index < result.Length; index++)
        {
            var row = ordered[index]; var member = plan.Members[index];
            if (row.OperationId != member.OperationId || row.Sequence != member.Sequence || row.ItemId != plan.ItemId ||
                row.CreatedAt == default || row.AttemptCount < 0 || !Enum.IsDefined(row.Kind))
                throw new InvalidDataException("A KeepRemote original lost its retained identity, sequence or item association.");
            result[index] = new(row.OperationId, row.Sequence, plan.ItemId, row.Kind,
                Convert.ToHexString(SHA256.HashData(row.Payload.Span)), row.CreatedAt, row.AttemptCount, row.RetryAfter);
        }
        if (requireNoSuccessors && ordered.Length > result.Length)
        {
            if (ordered[result.Length].Kind == CloudStateOperationKind.MetadataUpdate)
                throw new MirrorPulseKeepRemoteJournalDeferredException(requiresFullRescan: false);
            throw new InvalidDataException("KeepRemote cannot apply after a later target-item operation.");
        }
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return result;
    }

    private static string Fingerprint(MirrorPulseConflictKeepRemoteIntent intent) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent)));

    private static async ValueTask<IReadOnlyList<MirrorPulseLocalChangeObservation>> ReadPublicOriginalsAsync(CloudLocalChangeFeed feed,
        IReadOnlyList<MirrorPulseJournalOperationReference> members, CancellationToken token)
    {
        var scan = await feed.BeginScanAsync(token).ConfigureAwait(false);
        if (scan.RequiresFullRescan) throw new MirrorPulseKeepRemoteJournalDeferredException(requiresFullRescan: true);
        var result = new List<MirrorPulseLocalChangeObservation>(members.Count);
        foreach (var member in members)
        {
            if (scan.ThroughSequence < member.Sequence)
                throw new InvalidDataException("The official feed lost an original KeepRemote sequence.");
            var page = await feed.ReadPageAsync(scan, member.Sequence - 1, 1, token).ConfigureAwait(false);
            if (page.RequiresFullRescan) throw new MirrorPulseKeepRemoteJournalDeferredException(requiresFullRescan: true);
            if (page.Changes.Count != 1 || page.Changes[0].OperationId != member.OperationId ||
                page.Changes[0].Sequence != member.Sequence)
                throw new InvalidDataException("The original public KeepRemote observation disappeared or requires reconciliation.");
            var change = page.Changes[0];
            result.Add(new(change.OperationId, change.Sequence, change.Kind, change.ItemId, change.RelativePath,
                change.PreviousRelativePath, change.IsDirectory, change.ObservedAt));
        }
        return result.AsReadOnly();
    }
}
