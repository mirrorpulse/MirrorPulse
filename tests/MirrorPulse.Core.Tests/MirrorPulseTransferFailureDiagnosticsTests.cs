using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseTransferFailureDiagnosticsTests
{
    [TestMethod]
    [DataRow(MirrorPulseTransferCommandPhase.Prepare)]
    [DataRow(MirrorPulseTransferCommandPhase.Read)]
    [DataRow(MirrorPulseTransferCommandPhase.Copy)]
    [DataRow(MirrorPulseTransferCommandPhase.VerifyTarget)]
    [DataRow(MirrorPulseTransferCommandPhase.PrepareTargetBirth)]
    [DataRow(MirrorPulseTransferCommandPhase.PublishTarget)]
    [DataRow(MirrorPulseTransferCommandPhase.RetireSource)]
    [DataRow(MirrorPulseTransferCommandPhase.Complete)]
    [DataRow(MirrorPulseTransferCommandPhase.ReadStatus)]
    public async Task FailedTransferPhaseRetainsItsOriginalExceptionAndOnlyClosedDiagnosticFields(MirrorPulseTransferCommandPhase phase)
    {
        Guid operation = Guid.NewGuid(); var original = new InvalidOperationException("synthetic-password-and-user-path");
        LogEntry observed = null!; int calls = 0; int observations = 0;
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            MirrorPulseTransferFailureDiagnostics.RunAsync<int>(operation, phase, () =>
            {
                calls++; return ValueTask.FromException<int>(original);
            }, entry => { observations++; observed = entry; return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, thrown); Assert.AreEqual(1, calls); Assert.AreEqual(1, observations);
        var safe = SafeDiagnosticPolicy.Sanitize(observed);
        Assert.AreEqual("TransferCommandFailed", safe.Code);
        Assert.AreEqual(operation.ToString("D"), safe.Fields["operationId"]);
        Assert.AreEqual(phase.ToString(), safe.Fields["transferCommandPhase"]);
        Assert.AreEqual("InvalidOperation", safe.Fields["transferFailureKind"]);
        Assert.AreEqual(original.HResult.ToString("X8", CultureInfo.InvariantCulture), safe.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains(original.Message, StringComparison.Ordinal));
        Assert.HasCount(6, safe.Fields);
    }

    [TestMethod]
    public async Task NativeTransferFailureWritesItsActualWin32CodeWithoutExceptionTextToRollingLog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-transfer-diagnostics", Guid.NewGuid().ToString("N"));
        var original = new Win32Exception(32, "synthetic-secret-at-private-file"); Guid operation = Guid.NewGuid();
        try
        {
            using var writer = new LocalRollingLogWriter(directory);
            var thrown = await Assert.ThrowsExactlyAsync<Win32Exception>(() => MirrorPulseTransferFailureDiagnostics.RunAsync<int>(
                operation, MirrorPulseTransferCommandPhase.PublishTarget, () => ValueTask.FromException<int>(original),
                async entry => await writer.WriteAsync(entry)).AsTask());
            Assert.AreSame(original, thrown);
            string raw = await File.ReadAllTextAsync(writer.FilePath);
            Assert.IsFalse(raw.Contains(original.Message, StringComparison.Ordinal));
            var saved = JsonSerializer.Deserialize<SafeLogEntry>(raw)!;
            Assert.AreEqual("32", saved.Fields["nativeErrorCode"]);
            Assert.AreEqual("Native", saved.Fields["transferFailureKind"]);
            Assert.AreEqual(operation.ToString("D"), saved.Fields["operationId"]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.Delete(Path.Combine(directory, "mirrorpulse.log")); Directory.Delete(directory);
            }
        }
    }

    [TestMethod]
    public async Task TransferLogStorageFailureCannotReplaceOrRetryTheOriginalCommandFailure()
    {
        string file = Path.GetTempFileName(); var original = new IOException("synthetic-source-sharing-boundary"); int calls = 0;
        try
        {
            var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseTransferFailureDiagnostics.RunAsync<int>(
                Guid.NewGuid(), MirrorPulseTransferCommandPhase.RetireSource,
                () => { calls++; return ValueTask.FromException<int>(original); }, entry =>
                {
                    using var unavailable = new LocalRollingLogWriter(file);
                    return new(unavailable.WriteAsync(entry));
                }).AsTask());
            Assert.AreSame(original, thrown); Assert.AreEqual(1, calls);
            Assert.IsTrue(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task SuccessfulTransferCallReturnsItsOriginalResultWithoutFailureObservation()
    {
        object expected = new(); int calls = 0; int observations = 0;
        var actual = await MirrorPulseTransferFailureDiagnostics.RunAsync(Guid.NewGuid(), MirrorPulseTransferCommandPhase.Copy,
            () => { calls++; return ValueTask.FromResult(expected); }, _ => { observations++; return ValueTask.CompletedTask; });
        Assert.AreSame(expected, actual); Assert.AreEqual(1, calls); Assert.AreEqual(0, observations);
    }

    [TestMethod]
    public void TransferDiagnosticPolicyRejectsArbitraryAndNumericPhaseValues()
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Error, "host", "TransferCommandFailed", DateTimeOffset.UtcNow,
            [new("transferCommandPhase", "0"), new("transferFailureKind", "synthetic-secret"), new("nativeErrorCode", "private-path"),
                new("exception", "synthetic-private-exception") ]));
        Assert.IsFalse(safe.Fields.ContainsKey("transferCommandPhase"));
        Assert.IsFalse(safe.Fields.ContainsKey("transferFailureKind")); Assert.IsFalse(safe.Fields.ContainsKey("nativeErrorCode"));
        Assert.AreEqual("4", safe.Fields["omittedFieldCount"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("synthetic-", StringComparison.Ordinal));
    }
}
