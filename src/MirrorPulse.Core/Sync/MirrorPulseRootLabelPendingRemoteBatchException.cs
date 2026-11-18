namespace MirrorPulse.Core.Sync;

/// <summary>A durable remote batch is still pending before a new Label operation has been admitted.</summary>
public sealed class MirrorPulseRootLabelPendingRemoteBatchException : InvalidOperationException
{
    internal MirrorPulseRootLabelPendingRemoteBatchException()
        : base("The original remote batch must converge before preparing a root rename.") { }
}
