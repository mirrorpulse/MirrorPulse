using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseTransferAdmissionRetryTests
{
    private static readonly double[] OriginalAdmissionWaitMilliseconds = [250, 500, 1000, 2000];

    [TestMethod]
    public async Task BusyAdmissionReleasesItsActualNamespaceLeaseBeforeEachWaitAndReturnsTheOriginalResult()
    {
        var root = MirrorPulse.Core.Contracts.RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using (var recovery = await admission.BeginRecoveryAsync([root], CancellationToken.None)) recovery.CompleteAfterFreshAudit();
        int attempts = 0, waits = 0;
        object expected = new();
        using var stop = new CancellationTokenSource();
        object actual = await MirrorPulseTransferAdmissionRetry.RunAsync<object>(token =>
        {
            Assert.AreEqual(stop.Token, token);
            using var lease = admission.Enter([root]);
            if (++attempts <= 2) throw new MirrorPulseTransferAdmissionBusyException();
            return ValueTask.FromResult(expected);
        }, async (delay, token) =>
        {
            Assert.AreEqual(stop.Token, token);
            Assert.AreEqual(TimeSpan.FromMilliseconds(waits++ == 0 ? 250 : 500), delay);
            // A real recovery admission can drain only after the rejected
            // preparation's ordinary lease is disposed. No root is reopened by retry.
            await using var recovery = await admission.BeginRecoveryAsync([root], token);
            recovery.CompleteAfterFreshAudit();
        }, stop.Token);
        Assert.AreSame(expected, actual);
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(2, waits);
    }

    [TestMethod]
    public async Task ExhaustedBusyAdmissionPreservesItsFinalExceptionAndHasAFixedFiniteBudget()
    {
        int attempts = 0;
        var delays = new List<TimeSpan>();
        var original = new MirrorPulseTransferAdmissionBusyException();
        var actual = await Assert.ThrowsExactlyAsync<MirrorPulseTransferAdmissionBusyException>(() =>
            MirrorPulseTransferAdmissionRetry.RunAsync<object>(_ =>
            {
                attempts++; return ValueTask.FromException<object>(original);
            }, (delay, _) => { delays.Add(delay); return ValueTask.CompletedTask; }, CancellationToken.None).AsTask());
        Assert.AreSame(original, actual);
        Assert.AreEqual(5, attempts);
        CollectionAssert.AreEqual(OriginalAdmissionWaitMilliseconds, delays.Select(delay => delay.TotalMilliseconds).ToArray());
        var error = MirrorPulseControlErrorMapper.FromException(actual);
        Assert.AreEqual(MirrorPulseControlErrorCodes.HostBusy, error.Code);
        Assert.IsTrue(error.Retryable);
        Assert.IsFalse(error.Message.Contains("transfer or effect ID", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("ambiguous")]
    [DataRow("generic-ownership")]
    [DataRow("io")]
    [DataRow("cancelled")]
    public async Task OtherFailuresNeverAuthorizeAnotherPreparationOrWait(string failure)
    {
        Exception original = failure switch
        {
            "ambiguous" => new MirrorPulseMutationAmbiguousException("private source uncertainty"),
            "generic-ownership" => new InvalidOperationException("An original product owner already retains this transfer or effect ID."),
            "io" => new IOException("private path"),
            _ => new OperationCanceledException("private cancellation")
        };
        int calls = 0, waits = 0;
        try
        {
            await MirrorPulseTransferAdmissionRetry.RunAsync<object>(_ =>
            {
                calls++; return ValueTask.FromException<object>(original);
            }, (_, _) => { waits++; return ValueTask.CompletedTask; }, CancellationToken.None);
            Assert.Fail("The original failure must be retained.");
        }
        catch (Exception actual) when (ReferenceEquals(actual, original)) { }
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, waits);
        Assert.AreNotEqual(MirrorPulseControlErrorCodes.HostBusy, MirrorPulseControlErrorMapper.FromException(original).Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalCancellationStopsBeforeAdmissionOrWhileWaitingWithoutAnotherAttempt(bool duringWait)
    {
        using var stop = new CancellationTokenSource();
        if (!duringWait) stop.Cancel();
        int attempts = 0, waits = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseTransferAdmissionRetry.RunAsync<object>(_ =>
        {
            attempts++; throw new MirrorPulseTransferAdmissionBusyException();
        }, (_, token) =>
        {
            waits++; stop.Cancel(); return ValueTask.FromCanceled(token);
        }, stop.Token).AsTask());
        Assert.AreEqual(duringWait ? 1 : 0, attempts);
        Assert.AreEqual(duringWait ? 1 : 0, waits);
    }
}
