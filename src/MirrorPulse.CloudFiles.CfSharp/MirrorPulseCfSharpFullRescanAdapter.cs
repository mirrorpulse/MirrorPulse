using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Connects CfSharp's durable rescan marker to MP's reconciliation policy.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseCfSharpFullRescanAdapter
{
    private readonly CloudLocalChangeFeed _feed;
    private readonly Func<CancellationToken, ValueTask<int>> _rescan;

    public MirrorPulseCfSharpFullRescanAdapter(
        CloudLocalChangeFeed feed,
        Func<CancellationToken, ValueTask<int>> rescan)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(rescan);
        _feed = feed;
        _rescan = rescan;
    }

    public ValueTask<MirrorPulseFullRescanResult> HandleAsync(
        CloudLocalChangeBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return HandleAsync(new MirrorPulseLocalChangeSignal(batch.RequiresFullRescan), cancellationToken);
    }

    public ValueTask<MirrorPulseFullRescanResult> HandleAsync(MirrorPulseLocalChangeSignal signal,
        CancellationToken cancellationToken = default)
    {
        CloudLocalChangeRescan? boundary = null;
        var coordinator = new MirrorPulseFullRescanCoordinator(token =>
        {
            boundary = _feed.BeginFullRescan();
            return _rescan(token);
        }, token => _feed.AcknowledgeFullRescanAsync(boundary!, token));
        return coordinator.HandleAsync(signal, cancellationToken);
    }
}
