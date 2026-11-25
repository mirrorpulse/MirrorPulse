using System.Collections.Concurrent;
using System.Threading.Channels;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseActiveRemotePoller
{
    private enum PollTrigger { Scheduled, Manual, Forced }

    private sealed record RetrySchedule(MirrorPulseRootPollingStatus Status, int Failures, DateTimeOffset DueAt);

    private readonly ConcurrentDictionary<RootId, RetrySchedule> _pollSchedules = new();
    private readonly Dictionary<InstanceId, PollWakeSignal> _wakeSignals;

    private sealed class PollWakeSignal
    {
        private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(1);
        private int _reconnected;

        internal void Notify(bool reconnected)
        {
            if (reconnected) Interlocked.Exchange(ref _reconnected, 1);
            _wake.Writer.TryWrite(0);
        }

        internal bool TryRead(out PollTrigger trigger)
        {
            trigger = PollTrigger.Scheduled;
            if (!_wake.Reader.TryRead(out _)) return false;
            if (Interlocked.Exchange(ref _reconnected, 0) != 0) trigger = PollTrigger.Forced;
            return true;
        }

        internal ValueTask<bool> WaitAsync(CancellationToken token) => _wake.Reader.WaitToReadAsync(token);
    }

    public MirrorPulseRootPollingStatus ReadRootStatus(RootRegistration root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.State != RootRegistrationState.Active) return new(root.RootId.ToString(), "Offline", null, null, null);
        return _pollSchedules.TryGetValue(root.RootId, out var schedule) ? schedule.Status :
            new(root.RootId.ToString(), "Waiting", null, null, null);
    }

    /// <summary>Wakes the deployment when a new Worker session is actually ready.</summary>
    public void NotifyWorkerConnected(InstanceId instance)
    {
        if (_wakeSignals.TryGetValue(instance, out var signal)) signal.Notify(reconnected: true);
    }

    private bool CanAttempt(RootId root, PollTrigger trigger, DateTimeOffset now) =>
        trigger == PollTrigger.Forced || !_pollSchedules.TryGetValue(root, out var schedule) ||
        now >= schedule.DueAt || (trigger == PollTrigger.Manual && schedule.Failures == 0);

    private static string PollFailureReason(Exception failure) => failure switch
    {
        MirrorPulsePollBaselineCorruptException => "PollBaselineCorrupt",
        MirrorPulsePollCursorMismatchException => "PollCursorMismatch",
        AdapterWorkerOperationException
        {
            FailureCode: "RootOffline" or "CredentialRejected" or "AccessDenied" or
                "SourceUnavailable" or "InvalidCursor" or "DirectoryEnumerationIncomplete"
        } worker => worker.FailureCode,
        _ => SafeDiagnosticPolicy.ClassifyFailure(failure),
    };

    private void RememberAttempt(RootId root, MirrorPulseRemotePollStage stage, string? failure)
    {
        var prior = _pollSchedules.GetValueOrDefault(root);
        var now = DateTimeOffset.UtcNow;
        failure ??= stage == MirrorPulseRemotePollStage.ApplyAndCommit ? "RemoteBatchRetry" : null;
        var (phase, delay, failures, successful) = (failure, stage) switch
        {
            (not null, _) => Failure(prior?.Failures ?? 0),
            (null, MirrorPulseRemotePollStage.BatchProgress) => ("Applying", TimeSpan.FromMilliseconds(250), 0, false),
            (null, MirrorPulseRemotePollStage.RetainedIntent or MirrorPulseRemotePollStage.IdentitySelection) => ("WaitingForRecovery", _interval, 0, false),
            _ => ("Current", _interval, 0, true),
        };
        var due = now + delay;
        var status = new MirrorPulseRootPollingStatus(root.ToString(), phase, failure,
            successful ? null : due, successful ? now : prior?.Status.LastSuccessfulScan);
        _pollSchedules[root] = new(status, failures, due);

        (string Phase, TimeSpan Delay, int Failures, bool Successful) Failure(int previous)
        {
            int count = Math.Min(previous + 1, 10);
            double milliseconds = Math.Min(Math.Max(_interval.TotalMilliseconds, 300_000),
                _interval.TotalMilliseconds * (1 << (count - 1)));
            return ("Retrying", TimeSpan.FromMilliseconds(milliseconds), count, false);
        }
    }

    private async Task RunInstanceAsync(InstanceId instance, CancellationToken token)
    {
        var signal = _wakeSignals[instance];
        var trigger = PollTrigger.Scheduled;
        while (!token.IsCancellationRequested)
        {
            bool loopFault = false;
            try { await RunTurnAsync(instance, trigger, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { loopFault = true; }

            if (signal.TryRead(out trigger)) continue;

            var active = (_currentRoots?.Invoke() ?? _roots.GetValueOrDefault(instance) ?? []).Where(root =>
                root.InstanceId == instance && root.State == RootRegistrationState.Active);
            var now = DateTimeOffset.UtcNow;
            var due = active.Select(root => _pollSchedules.TryGetValue(root.RootId, out var schedule)
                ? schedule.DueAt : loopFault ? now + _interval : now).DefaultIfEmpty(now + _interval).Min();
            if (due <= now) continue;

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            wake.CancelAfter(due - now);
            try
            {
                if (await signal.WaitAsync(wake.Token).ConfigureAwait(false)) signal.TryRead(out trigger);
            }
            catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
        }
    }
}
