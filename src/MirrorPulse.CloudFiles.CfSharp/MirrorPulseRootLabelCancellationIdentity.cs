namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Checks the original native root proof separately from optional durable projection metadata.</summary>
internal static class MirrorPulseRootLabelCancellationIdentity
{
    internal static void RequireOriginal(Guid originalItemId, Guid? projectedItemId,
        ReadOnlySpan<byte> actualIdentity, ReadOnlySpan<byte> expectedIdentity)
    {
        if (projectedItemId is { } projected && projected != originalItemId ||
            !actualIdentity.SequenceEqual(expectedIdentity))
            throw new InvalidDataException("Unsealed cancellation lost the exact original public root identity.");
    }
}
