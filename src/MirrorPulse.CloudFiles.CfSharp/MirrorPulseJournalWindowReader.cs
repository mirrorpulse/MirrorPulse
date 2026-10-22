namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseJournalWindowPage(IReadOnlyList<MirrorPulseLocalChangeObservation> Changes,
    long LastScannedSequence, bool HasMore, bool RequiresFullRescan);

internal sealed record MirrorPulseJournalWindowRead(IReadOnlyList<MirrorPulseLocalChangeObservation>? Observations,
    long UpperSequence, bool ScanInProgress, bool RequiresFullRescan);

/// <summary>Reads one complete finite planning window over bounded scheduling turns.</summary>
/// <remarks>No partial window is dispatched or acknowledged. The continuation is owned by this
/// running reader only; loss, cancellation, read failure or Host restart starts again from zero
/// against the authoritative pending journal. Final mapping and retained memory have separate budgets.</remarks>
internal sealed class MirrorPulseJournalWindowReader<TScan>(
    Func<CancellationToken, ValueTask<TScan>> begin,
    Func<TScan, long> upperSequence,
    Func<TScan, bool> requiresFullRescan,
    Func<TScan, long, int, CancellationToken, ValueTask<MirrorPulseJournalWindowPage>> readPage,
    int pagesPerRead = 8, int pageSize = 64) where TScan : class
{
    private readonly int _pagesPerRead = pagesPerRead is >= 1 and <= 64 ? pagesPerRead : throw new ArgumentOutOfRangeException(nameof(pagesPerRead));
    private readonly int _pageSize = pageSize is >= 1 and <= 4096 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
    private TScan? _scan;
    private List<MirrorPulseLocalChangeObservation> _observations = [];
    private long _afterSequence;
    private int _reading;

    internal bool HasPendingScan => Volatile.Read(ref _scan) is not null;

    internal void Reset()
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("A running journal window cannot be reset concurrently.");
        try { ResetCore(); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    internal async ValueTask<MirrorPulseJournalWindowRead> ReadAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException("The planning window has a single scheduling owner.");
        try
        {
            token.ThrowIfCancellationRequested();
            _scan ??= await begin(token).ConfigureAwait(false);
            long upper = upperSequence(_scan);
            if (upper < 0) throw new InvalidDataException("A journal scan has an invalid finite boundary.");
            if (requiresFullRescan(_scan))
            {
                ResetCore(); return new(null, upper, false, true);
            }
            for (int pageIndex = 0; pageIndex < _pagesPerRead; pageIndex++)
            {
                token.ThrowIfCancellationRequested();
                var page = await readPage(_scan, _afterSequence, _pageSize, token).ConfigureAwait(false);
                if (page.RequiresFullRescan)
                {
                    ResetCore(); return new(null, upper, false, true);
                }
                if (page.Changes.Count > _pageSize || page.LastScannedSequence < _afterSequence ||
                    page.LastScannedSequence > upper || page.HasMore && (page.LastScannedSequence <= _afterSequence || page.LastScannedSequence >= upper) ||
                    !page.HasMore && page.LastScannedSequence != upper)
                    throw new InvalidDataException("The public journal page did not advance inside its captured boundary.");
                long sequence = _afterSequence;
                foreach (var observation in page.Changes)
                {
                    if (observation.Sequence <= sequence || observation.Sequence > page.LastScannedSequence)
                        throw new InvalidDataException("The public journal page lost its original strict sequence order.");
                    sequence = observation.Sequence;
                }
                _observations.AddRange(page.Changes);
                if (!page.HasMore)
                {
                    var completed = _observations.AsReadOnly();
                    ResetCore(); return new(completed, upper, false, false);
                }
                _afterSequence = page.LastScannedSequence;
            }
            return new(null, upper, true, false);
        }
        catch { ResetCore(); throw; }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private void ResetCore() { _scan = null; _afterSequence = 0; _observations = []; }
}
