namespace MirrorPulse.Core.Sync;

/// <summary>An existing original journal owner rejected a new transfer before any admission or effect.</summary>
/// <remarks>Only atomic admission raises this exception after its transaction is rolled back.
/// It never classifies an existing transfer, uncertain remote invocation or copy failure as retryable.</remarks>
public sealed class MirrorPulseTransferAdmissionBusyException : InvalidOperationException
{
    internal MirrorPulseTransferAdmissionBusyException()
        : base("An original product owner already retains this transfer or effect ID.") { }
}
