using MirrorPulse.CfSharp.CrashProbe;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliFixtureRootCancellationCleanupLineageTests
{
    [TestMethod]
    [DataRow("active", 4)]
    [DataRow("disabled", 6)]
    public void OnlyTheExactCancellationFixtureEndpointsSelectItsCompleteAdditionalHistory(string phase, int expectedMoves)
    {
        var lineage = CliFixtureRootCancellationCleanupLineage.Select("Docs", "Docs After " + phase)!;
        Assert.AreEqual(expectedMoves, lineage.ExpectedMoves);
        string[] targets = phase == "active"
            ? ["Docs After active", "Docs", "DOCS LABEL", "Docs Label"]
            : ["Docs After disabled", "Docs", "Docs After active", "Docs", "DOCS LABEL", "Docs Label"];
        for (int index = 0; index < targets.Length; index++)
            lineage.RequireTransition(index, index + 1 == targets.Length ? "Docs" : targets[index + 1], targets[index]);
        lineage.RequireComplete(expectedMoves);
        foreach (string other in new[] { "Docs", "Docs Label", "DOCS LABEL", "Docs After Active", "Docs After disabled/foreign", "Foreign After active" })
            Assert.IsNull(CliFixtureRootCancellationCleanupLineage.Select("Docs", other), "Other names must retain the original three-move cleanup boundary.");
    }

    [TestMethod]
    [DataRow("active", "source")]
    [DataRow("disabled", "source")]
    [DataRow("active", "target")]
    [DataRow("disabled", "target")]
    [DataRow("active", "case")]
    [DataRow("disabled", "case")]
    [DataRow("active", "negative-index")]
    [DataRow("disabled", "negative-index")]
    [DataRow("active", "extra-move")]
    [DataRow("disabled", "extra-move")]
    [DataRow("active", "missing-move")]
    [DataRow("disabled", "missing-move")]
    [DataRow("active", "invented-move")]
    [DataRow("disabled", "invented-move")]
    public void AForeignOrIncompleteAdditionalLineageCannotWidenFixtureCleanup(string phase, string damage)
    {
        string current = "Docs After " + phase;
        var lineage = CliFixtureRootCancellationCleanupLineage.Select("Docs", current)!;
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            switch (damage)
            {
                case "source": lineage.RequireTransition(0, "Foreign", current); break;
                case "target": lineage.RequireTransition(0, "Docs", "Foreign"); break;
                case "case": lineage.RequireTransition(0, "Docs", current.ToUpperInvariant()); break;
                case "negative-index": lineage.RequireTransition(-1, "Docs", current); break;
                case "extra-move": lineage.RequireTransition(lineage.ExpectedMoves, "Docs", current); break;
                case "missing-move": lineage.RequireComplete(lineage.ExpectedMoves - 1); break;
                case "invented-move": lineage.RequireComplete(lineage.ExpectedMoves + 1); break;
                default: Assert.Fail("Unknown damaged cleanup boundary."); break;
            }
        });
    }
}
