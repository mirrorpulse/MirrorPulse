namespace MirrorPulse.Tests.Fixtures;

/// <summary>Interrupts only the selected original's final product ACK receipt commit.</summary>
internal static class TransferAcknowledgementFaultSql
{
    internal static string BeforeOriginalReceipt(Guid originalOperationId)
    {
        if (originalOperationId == Guid.Empty)
            throw new ArgumentException("The selected original journal ID is required.", nameof(originalOperationId));
        return $"CREATE TRIGGER fail_late_transfer_ack BEFORE UPDATE ON retired_transfer_observations WHEN OLD.operation_id='{originalOperationId:D}' AND NEW.status=1 BEGIN SELECT RAISE(ABORT,'late-ack-return-gap'); END;";
    }
}
