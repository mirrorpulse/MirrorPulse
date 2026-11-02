using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed partial class MirrorPulseConflictKeepRemoteJournal
{
    /// <summary>Retains complete original row hashes, then revalidates actual public SDK rows after the product commit.</summary>
    /// <remarks>No SDK store identity, native application, restart replay permission or ACK is inferred from this receipt.</remarks>
    internal async Task<MirrorPulseConflictKeepRemoteJournalBinding> RetainAsync(MirrorPulseConflictKeepRemoteCopies copies,
        CancellationToken token = default)
    {
        var snapshot = await CaptureAsync(copies, token).ConfigureAwait(false);
        var retained = await catalog.ReadConflictKeepRemoteJournalAsync(snapshot.CommandId, token).ConfigureAwait(false);
        var current = Bind(snapshot, copies.Preparation, retained?.ObservedAt ?? DateTimeOffset.UtcNow);
        if (retained is not null) RequireSameBinding(retained, current);
        else retained = await catalog.RetainConflictKeepRemoteJournalAsync(current, token).ConfigureAwait(false);
        // These are separate databases. A change during the product commit leaves the old
        // correlations retained and the decision pending; it never authorizes recapture or ACK.
        await RevalidateAsync(snapshot, copies, token).ConfigureAwait(false);
        return retained;
    }

    internal async Task<MirrorPulseConflictKeepRemoteJournalBinding> RevalidateRetainedAsync(MirrorPulseConflictKeepRemoteCopies copies,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(copies);
        var retained = await catalog.ReadConflictKeepRemoteJournalAsync(copies.Preparation.Intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote journal correlations are missing.");
        var current = await CaptureAsync(copies, token).ConfigureAwait(false);
        RequireSameBinding(retained, Bind(current, copies.Preparation, retained.ObservedAt));
        return retained;
    }

    private static MirrorPulseConflictKeepRemoteJournalBinding Bind(MirrorPulseKeepRemoteJournalSnapshot snapshot,
        MirrorPulseConflictKeepRemotePreparation preparation, DateTimeOffset observedAt) =>
        new(1, snapshot.CommandId, snapshot.PlanId, snapshot.ItemId, snapshot.IntentSha256,
            preparation.LocalPreservation ?? throw new InvalidDataException("The selected local preservation is missing."),
            preparation.RemoteCopy ?? throw new InvalidDataException("The selected remote copy is missing."),
            Array.AsReadOnly(snapshot.Originals.Select(row => new MirrorPulseConflictKeepRemoteJournalMember(row.OperationId,
                row.Sequence, row.ItemId, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row))))).ToArray()), observedAt);

    private static void RequireSameBinding(MirrorPulseConflictKeepRemoteJournalBinding original, MirrorPulseConflictKeepRemoteJournalBinding current)
    {
        if (!JsonSerializer.SerializeToUtf8Bytes(original).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current)))
            throw new InvalidDataException("KeepRemote cannot replace its originally retained official row correlations or selected copies.");
    }
}
