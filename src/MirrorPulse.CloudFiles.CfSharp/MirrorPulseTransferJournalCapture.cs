using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Captures exact content observations from the Host's already-running public feed without ACK.</summary>
/// <remarks>The caller retains the original write/mapping-excluding source data, complete private input,
/// native identity/parent guards and both root admissions. A finite scan never proves watcher drainage.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseTransferJournalCapture(CloudLocalChangeFeed feed)
{
    internal async Task<MirrorPulseNamespaceTransferJournalClaim> CaptureAsync(MirrorPulseNamespaceTransferPlan plan,
        CancellationToken token)
    {
        if (!feed.IsStarted) throw new InvalidOperationException("Transfer capture requires the Host's already-running public journal.");
        var scan = await feed.BeginScanAsync(token).ConfigureAwait(false);
        if (scan.RequiresFullRescan) throw new InvalidOperationException("The original journal requires reconciliation before transfer capture.");
        long after = 0; var members = new List<MirrorPulseTransferJournalObservation>();
        while (true)
        {
            var page = await feed.ReadPageAsync(scan, after, 64, token).ConfigureAwait(false);
            if (page.RequiresFullRescan) throw new InvalidOperationException("The original journal lost its captured reconciliation boundary.");
            long previous = after;
            foreach (var change in page.Changes)
            {
                if (change.Sequence <= previous || change.Sequence > scan.ThroughSequence)
                    throw new InvalidDataException("The public transfer scan returned a non-original or unordered sequence.");
                previous = change.Sequence;
                var member = SelectMember(plan.Source.Identity.ItemId!.Value, plan.Source.Context.Location.RelativePath,
                    new(change.OperationId, change.Sequence, change.Kind, change.ItemId, change.RelativePath,
                        change.PreviousRelativePath, change.IsDirectory, change.ObservedAt));
                if (member is not null) members.Add(member);
                if (members.Count > 4096) throw new InvalidOperationException("The original source content journal exceeds its bounded reservation.");
            }
            if (page.LastScannedSequence < previous || page.LastScannedSequence > scan.ThroughSequence)
                throw new InvalidDataException("The public transfer scan cursor disagrees with its original page.");
            if (!page.HasMore) break;
            if (page.LastScannedSequence <= after)
                throw new InvalidDataException("The public transfer scan did not advance inside its captured boundary.");
            after = page.LastScannedSequence;
        }
        return new(1, plan.OperationId, scan.ThroughSequence, DateTimeOffset.UtcNow, members.AsReadOnly());
    }

    internal static MirrorPulseTransferJournalObservation? SelectMember(Guid originalItemId, string originalPath,
        MirrorPulseLocalChangeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (originalItemId == Guid.Empty) throw new ArgumentException("An original public source identity is required.", nameof(originalItemId));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        string? path = observation.RelativePath?.Replace('\\', '/');
        string? previous = observation.PreviousRelativePath?.Replace('\\', '/');
        bool sameItem = observation.ItemId == originalItemId;
        bool sameName = string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(previous, originalPath, StringComparison.OrdinalIgnoreCase);
        if (!sameItem && !sameName) return null;
        // Content synchronization's separate metadata policy retains responsibility for these IDs.
        // They stay pending here; a transfer neither consumes nor claims a metadata acknowledgement.
        if (observation.Kind == CloudLocalChangeKind.MetadataUpdate && observation.PreviousRelativePath is null) return null;
        if (!sameItem || path != originalPath || observation.PreviousRelativePath is not null || observation.IsDirectory ||
            observation.Kind is not (CloudLocalChangeKind.Create or CloudLocalChangeKind.ContentUpdate))
            throw new InvalidOperationException("An affected original source observation requires separate proved journal ownership.");
        return new(observation.OperationId, observation.Sequence,
            observation.Kind == CloudLocalChangeKind.Create ? MirrorPulseWorkerChangeKind.Create : MirrorPulseWorkerChangeKind.ContentUpdate,
            observation.ItemId, observation.RelativePath!, observation.PreviousRelativePath, observation.IsDirectory, observation.ObservedAt);
    }
}
