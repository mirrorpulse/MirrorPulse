using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationBornDiagnosticsTests
{
    private const string DetailCode = "RootLabelCancellationBornProgress";
    private const string LimitCode = "RootLabelCancellationBornProgressLimit";
    private const string Begin = "NamespaceBornRecoveryScopeBegin";
    private const string End = "NamespaceBornRecoveryScopeCompleted";

    [TestMethod]
    public async Task CapturePerformsNoObserverIoUntilTheCallerLeavesProtectedWorkAndFlushes()
    {
        var records = new List<SafeLogEntry>(); bool protectedWork = false, observedInside = false;
        Guid operation = Guid.NewGuid();
        int result = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation,
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.PendingBornPermissions, async () =>
            {
                protectedWork = true; MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin);
                MirrorPulseRootLabelCancellationBornDiagnostics.Capture(End);
                Assert.IsFalse(records.Any(record => record.Code == DetailCode));
                protectedWork = false; await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync();
                return 17;
            }), entry =>
            {
                if (entry.Message == DetailCode && protectedWork) observedInside = true;
                records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask;
            });
        Assert.AreEqual(17, result); Assert.IsFalse(observedInside);
        var details = records.Where(record => record.Code == DetailCode).ToArray(); Assert.HasCount(2, details);
        Assert.AreEqual(Begin, details[0].Fields["cancellationBornStep"]); Assert.AreEqual(End, details[1].Fields["cancellationBornStep"]);
        Assert.IsTrue(details.All(record => record.Fields["operationId"] == operation.ToString("D")));
        Assert.HasCount(4, records.Where(record => record.Code == "RootLabelCancellationProgress").ToArray());
    }

    [TestMethod]
    public async Task EveryFixedMilestoneRetainsItsOriginalOperationSequenceAndElapsedTime()
    {
        var records = new List<SafeLogEntry>(); Guid operation = Guid.NewGuid();
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation, async () =>
        {
            foreach (var step in Enum.GetValues<MirrorPulseCancellationBornStep>()) MirrorPulseRootLabelCancellationBornDiagnostics.Capture(step.ToString());
            await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1;
        }, entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        var details = records.Where(record => record.Code == DetailCode).ToArray(); Assert.HasCount(25, details);
        CollectionAssert.AreEqual(Enum.GetNames<MirrorPulseCancellationBornStep>(), details.Select(record => record.Fields["cancellationBornStep"]).ToArray());
        long elapsed = 0;
        for (int i = 0; i < details.Length; i++)
        {
            Assert.AreEqual(operation.ToString("D"), details[i].Fields["operationId"]);
            Assert.AreEqual((i + 1).ToString(CultureInfo.InvariantCulture), details[i].Fields["cancellationBornSequence"]);
            long current = long.Parse(details[i].Fields["cancellationBornElapsedMs"], CultureInfo.InvariantCulture);
            Assert.IsGreaterThanOrEqualTo(elapsed, current); elapsed = current;
            Assert.IsFalse(details[i].Fields.ContainsKey("redacted"));
        }
    }

    [TestMethod]
    public async Task PrivateUnknownAndNumericMilestonesAndCallsOutsideARequestAreIgnored()
    {
        MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin); await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync();
        var records = new List<SafeLogEntry>();
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(), async () =>
        {
            foreach (string? value in new[] { "PRIVATE_SECRET", "0", "NamespaceBornRecoveryScopeBegin PRIVATE_PATH", "namespacebornrecoveryscopebegin", null })
                MirrorPulseRootLabelCancellationBornDiagnostics.Capture(value);
            await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1;
        }, entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        Assert.IsFalse(records.Any(record => record.Code is DetailCode or LimitCode));
        Assert.IsFalse(JsonSerializer.Serialize(records).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DetailObserverFailurePreservesTheOriginalResultExceptionAndCoarseSequence()
    {
        foreach (bool fail in new[] { false, true })
        {
            var records = new List<SafeLogEntry>(); int detailCalls = 0; var expected = new object(); var original = new IOException("PRIVATE_PRODUCT");
            ValueTask Observe(LogEntry entry)
            {
                if (entry.Message == DetailCode) { detailCalls++; return ValueTask.FromException(new IOException("PRIVATE_OBSERVER")); }
                records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask;
            }
            async ValueTask<object> Act()
            {
                try { MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin); if (fail) throw original; return expected; }
                finally { await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); }
            }
            Task<object> Run() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
                () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.PendingBornPermissions, Act), Observe).AsTask();
            if (fail) Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(Run));
            else Assert.AreSame(expected, await Run());
            Assert.AreEqual(1, detailCalls);
            var coarse = records.Where(record => record.Code == "RootLabelCancellationProgress").ToArray(); Assert.HasCount(4, coarse);
            CollectionAssert.AreEqual(Enumerable.Range(1, 4).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(), coarse.Select(record => record.Fields["cancellationSequence"]).ToArray());
            Assert.IsFalse(JsonSerializer.Serialize(records).Contains("PRIVATE_", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ConcurrentRequestsKeepTheirOwnDeferredBuffersAndSequences()
    {
        var records = new ConcurrentQueue<SafeLogEntry>(); Guid[] operations = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(operations.Select(operation => MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation, async () =>
        {
            MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin); await Task.Yield();
            MirrorPulseRootLabelCancellationBornDiagnostics.Capture(End); await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1;
        }, entry => { records.Enqueue(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }).AsTask()));
        foreach (Guid operation in operations)
        {
            var own = records.Where(record => record.Code == DetailCode && record.Fields["operationId"] == operation.ToString("D")).ToArray();
            Assert.HasCount(2, own); Assert.AreEqual("1", own[0].Fields["cancellationBornSequence"]); Assert.AreEqual("2", own[1].Fields["cancellationBornSequence"]);
        }
    }

    [TestMethod]
    public async Task NestedRequestsRestoreTheOuterDeferredObservation()
    {
        var records = new List<SafeLogEntry>(); Guid outer = Guid.NewGuid(), inner = Guid.NewGuid();
        ValueTask Observe(LogEntry entry) { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(outer, async () =>
        {
            MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin);
            _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(inner, async () =>
            { MirrorPulseRootLabelCancellationBornDiagnostics.Capture(End); await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1; }, Observe);
            await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1;
        }, Observe);
        var details = records.Where(record => record.Code == DetailCode).ToArray(); Assert.HasCount(2, details);
        Assert.AreEqual(inner.ToString("D"), details[0].Fields["operationId"]); Assert.AreEqual(outer.ToString("D"), details[1].Fields["operationId"]);
        Assert.IsTrue(details.All(record => record.Fields["cancellationBornSequence"] == "1"));
    }

    [TestMethod]
    public async Task DetailBudgetHasOneExplicitTruncationAndNeverConsumesTheCoarseBudget()
    {
        var records = new List<SafeLogEntry>();
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.PendingBornPermissions, async () =>
            {
                for (int i = 0; i < 512; i++) MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin);
                await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync();
                for (int i = 0; i < 512; i++) MirrorPulseRootLabelCancellationBornDiagnostics.Capture(End);
                await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); return 1;
            }), entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        var details = records.Where(record => record.Code == DetailCode).ToArray(); Assert.HasCount(128, details);
        CollectionAssert.AreEqual(Enumerable.Range(1, 128).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(), details.Select(record => record.Fields["cancellationBornSequence"]).ToArray());
        var limits = records.Where(record => record.Code == LimitCode).ToArray(); Assert.HasCount(1, limits);
        Assert.AreEqual("true", limits[0].Fields["cancellationBornTruncated"]); Assert.AreEqual("129", limits[0].Fields["cancellationBornSequence"]);
        Assert.HasCount(4, records.Where(record => record.Code == "RootLabelCancellationProgress").ToArray());
    }

    [TestMethod]
    public async Task AChildContextCannotObserveAfterItsOriginalRequestWasDisposed()
    {
        var records = new List<SafeLogEntry>(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task? child = null;
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(), () =>
        {
            child = Task.Run(async () => { await release.Task; MirrorPulseRootLabelCancellationBornDiagnostics.Capture(Begin); await MirrorPulseRootLabelCancellationBornDiagnostics.FlushAsync(); });
            return ValueTask.FromResult(1);
        }, entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        release.SetResult(); await child!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(2, records); Assert.IsFalse(records.Any(record => record.Code == DetailCode));
    }

    [TestMethod]
    public void DetailSanitizationRefusesUntrustedTextAndInvalidCoordinates()
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "host", DetailCode, DateTimeOffset.UtcNow,
            [new("operationId", Guid.NewGuid().ToString("D")), new("cancellationBornStep", "PRIVATE_STEP"),
             new("cancellationBornSequence", "-1"), new("cancellationBornElapsedMs", "PRIVATE_PATH"), new("cancellationBornTruncated", "False")]));
        Assert.AreEqual("4", safe.Fields["omittedFieldCount"]); Assert.AreEqual(LogFieldPolicy.RedactedValue, safe.Fields["redacted"]);
        Assert.HasCount(3, safe.Fields); Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
    }
}
