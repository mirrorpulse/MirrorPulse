using System.Runtime.Versioning;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseRetiredTransferRootWindow(IReadOnlyList<RootRegistration> Roots, bool ScanInProgress);

/// <summary>Bounds recovery root inspection as well as the number of journal replays.</summary>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseRetiredTransferRootCursor(int rootsPerTurn = 4)
{
    private readonly int _rootsPerTurn = rootsPerTurn is >= 1 and <= 64 ? rootsPerTurn :
        throw new ArgumentOutOfRangeException(nameof(rootsPerTurn));
    private IReadOnlyList<RootRegistration>? _snapshot;
    private int _nextRoot;
    private int _remaining;
    private bool _pendingReplay;

    internal bool RecoveryInProgress => _remaining != 0 || _pendingReplay;
    internal void RetainPendingReplay() => _pendingReplay = true;

    internal MirrorPulseRetiredTransferRootWindow Read(MirrorPulseRootRouter router,
        Func<InstanceId, bool> mayDispatch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var roots = router.Registrations;
        if (!ReferenceEquals(roots, _snapshot) || _remaining == 0)
        {
            _snapshot = roots; _nextRoot = 0; _remaining = roots.Count; _pendingReplay = false;
        }
        int count = Math.Min(_remaining, _rootsPerTurn);
        var selected = new List<RootRegistration>(count);
        for (int inspected = 0; inspected < count; inspected++)
        {
            token.ThrowIfCancellationRequested();
            var root = roots[_nextRoot];
            if (root.State == RootRegistrationState.Active && mayDispatch(root.InstanceId)) selected.Add(root);
            _nextRoot++; _remaining--;
        }
        return new(selected.AsReadOnly(), _remaining != 0);
    }
}
