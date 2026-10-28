namespace MirrorPulse.CfSharp.CrashProbe;

/// <summary>Authenticates only the fixed extra Label endpoints used by the signed cancellation fixture.</summary>
/// <remarks>This shape check supplies no native authority; every original move receipt is still required.</remarks>
public sealed class CliFixtureRootCancellationCleanupLineage
{
    private readonly string _original;
    private readonly string[] _targets;

    private CliFixtureRootCancellationCleanupLineage(string original, string[] targets) => (_original, _targets) = (original, targets);

    public int ExpectedMoves => _targets.Length;

    public static CliFixtureRootCancellationCleanupLineage? Select(string original, string current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        string label = original + " Label";
        string caseLabel = label.ToUpperInvariant();
        if (current == original + " After active")
            return new(original, [current, original, caseLabel, label]);
        if (current == original + " After disabled")
            return new(original, [current, original, original + " After active", original, caseLabel, label]);
        return null;
    }

    public void RequireTransition(int index, string source, string target)
    {
        if ((uint)index >= (uint)_targets.Length || target != _targets[index] ||
            source != (index + 1 == _targets.Length ? _original : _targets[index + 1]))
            throw new InvalidDataException("Cancellation fixture cleanup requires its exact original Label transitions.");
    }

    public void RequireComplete(int observedMoves)
    {
        if (observedMoves != _targets.Length)
            throw new InvalidDataException("Cancellation fixture cleanup requires its complete original Label lineage.");
    }
}
