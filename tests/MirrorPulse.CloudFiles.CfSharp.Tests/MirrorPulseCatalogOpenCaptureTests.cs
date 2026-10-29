using System.Diagnostics;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MirrorPulseCatalogOpenCaptureTests
{
    [TestMethod]
    public async Task ActualPrivateCatalogProducerAndVerificationConsumerAgreeOnEveryClosedPhase()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-catalog-capture-" + Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync-unused"), Path.Combine(directory, "private"));
        try
        {
            var actual = new List<(string Phase, Guid Trace, long Elapsed)>();
            using var capture = new MirrorPulseCatalogOpenCapture((phase, trace, elapsed) => actual.Add((phase, trace, elapsed)));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.HasCount(19, actual); Assert.AreEqual("OwnerLeaseOpening", actual[0].Phase); Assert.AreEqual("Completed", actual[^1].Phase);
            Assert.HasCount(19, actual.Select(item => item.Phase).Distinct(StringComparer.Ordinal).ToArray());
            Assert.HasCount(1, actual.Select(item => item.Trace).Distinct().ToArray()); Assert.AreNotEqual(Guid.Empty, actual[0].Trace);
            Assert.IsTrue(actual.All(item => item.Elapsed >= 0));
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath)); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void CaptureRetainsOnlyAnonymousKnownPhaseTimingAcrossMultipleSourcesAndDisposesAllSubscriptions()
    {
        using var first = new DiagnosticListener("MirrorPulse.ProductCatalog.Open");
        var actual = new List<(string Phase, Guid Trace, long Elapsed)>();
        using var capture = new MirrorPulseCatalogOpenCapture((phase, trace, elapsed) => actual.Add((phase, trace, elapsed)));
        using var second = new DiagnosticListener("MirrorPulse.ProductCatalog.Open");
        Guid original = Guid.NewGuid(), reopened = Guid.NewGuid();
        first.Write("OwnerLeaseOpening", (original, 0L)); second.Write("Completed", (reopened, 35L));
        Assert.HasCount(2, actual); Assert.AreEqual(("OwnerLeaseOpening", original, 0L), actual[0]); Assert.AreEqual(("Completed", reopened, 35L), actual[1]);
        capture.Dispose(); first.Write("Completed", (original, 50L)); second.Write("Completed", (reopened, 51L));
        Assert.HasCount(2, actual);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("phase")]
    [DataRow("payload")]
    [DataRow("empty-trace")]
    [DataRow("negative-elapsed")]
    [DataRow("wrong-numeric-type")]
    public void CaptureRejectsUnknownNamesPrivateValuesAndMalformedTimingPayloads(string damage)
    {
        using var source = new DiagnosticListener(damage == "source" ? "private-unrelated-source" : "MirrorPulse.ProductCatalog.Open");
        int writes = 0; using var capture = new MirrorPulseCatalogOpenCapture((_, _, _) => writes++);
        object payload = damage switch
        {
            "payload" => new { path = "private/path", sql = "private SQL", user = "private user" },
            "empty-trace" => (Guid.Empty, 1L),
            "negative-elapsed" => (Guid.NewGuid(), -1L),
            "wrong-numeric-type" => (Guid.NewGuid(), 1),
            _ => (Guid.NewGuid(), 1L)
        };
        source.Write(damage == "phase" ? "private/path-or-query" : "Completed", payload);
        Assert.AreEqual(0, writes);
    }
}
