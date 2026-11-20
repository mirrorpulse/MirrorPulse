using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

internal static class MirrorPulseCfSharpJournalPages
{
    internal static readonly TimeSpan ValidationRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ValidationWaitLimit = TimeSpan.FromSeconds(2);

    // Without loss, CfSharp withholds an unvalidated metadata row and all rows after it.
    internal static bool IsAwaitingMetadataValidation(long after, long through, long lastScanned,
        int changeCount, bool hasMore, bool requiresFullRescan) =>
        hasMore && !requiresFullRescan && changeCount == 0 && lastScanned == after && after < through;

    [SupportedOSPlatform("windows10.0.16299")]
    internal static async ValueTask<CloudLocalChangePage> ReadAfterMetadataValidationAsync(this CloudLocalChangeFeed feed,
        CloudLocalChangeScan scan, long after, int limit, CancellationToken token)
    {
        long? waitStarted = null;
        while (true)
        {
            var page = await feed.ReadPageAsync(scan, after, limit, token).ConfigureAwait(false);
            if (!IsAwaitingMetadataValidation(after, scan.ThroughSequence, page.LastScannedSequence,
                page.Changes.Count, page.HasMore, page.RequiresFullRescan))
                return page;

            waitStarted ??= System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted.Value) >= ValidationWaitLimit)
                throw new TimeoutException("The public journal is still awaiting native metadata validation.");
            await Task.Delay(ValidationRetryDelay, token).ConfigureAwait(false);
        }
    }
}
