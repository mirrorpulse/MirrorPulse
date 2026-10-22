namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Classifies and routes original observations in bounded turns without exposing a partial plan.</summary>
/// <remarks>The input remains an immutable finite SDK window. The caller owns fresh loss and root
/// checks. Classification may durably settle an original retired observation; a restart must read the
/// authoritative journal again. This component never invents another operation ID or sequence.</remarks>
internal sealed class MirrorPulseJournalWindowPlanner(IReadOnlyList<MirrorPulseLocalChangeObservation> observations,
    MirrorPulseRootRouter router, long upperSequence, int observationsPerTurn = 512)
{
    private readonly int _budget = observationsPerTurn is >= 1 and <= 4096 ? observationsPerTurn :
        throw new ArgumentOutOfRangeException(nameof(observationsPerTurn));
    private readonly MirrorPulseLocalBatchPlanBuilder _builder = new(router);
    private int _next;
    private int _reading;
    private bool _failed;
    private MirrorPulseLocalBatchPlan? _plan;

    internal long UpperSequence { get; } = upperSequence;
    internal MirrorPulseLocalBatchPlan Plan => _plan ?? throw new InvalidOperationException("Classification and routing are incomplete.");

    internal async ValueTask<bool> AdvanceAsync(Func<MirrorPulseLocalChangeObservation, CancellationToken, ValueTask<bool>> include,
        CancellationToken token)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("The original planning window has one owner.");
        try
        {
            if (_failed) throw new InvalidOperationException("A failed planning window must restart from the official journal.");
            token.ThrowIfCancellationRequested();
            if (_plan is not null) return true;
            int remaining = _budget;
            while (_next < observations.Count && remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var observation = observations[_next];
                if (await include(observation, token).ConfigureAwait(false)) _builder.Add(observation);
                _next++; remaining--;
            }
            // Final dependency projection has its own bounded turn and retains the
            // old operation-ID semantics even if malformed input duplicates an ID.
            if (_next < observations.Count || remaining == 0) return false;
            if (!_builder.Finish(remaining)) return false;
            _plan = _builder.Result;
            return true;
        }
        catch { _failed = true; throw; }
        finally { Volatile.Write(ref _reading, 0); }
    }
}
