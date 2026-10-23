using System.ComponentModel;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRemoteNativeFailureClassifierTests
{
    [TestMethod]
    [DataRow(2, MirrorPulseRemoteFailureBoundary.NativeItemMissing)]
    [DataRow(3, MirrorPulseRemoteFailureBoundary.NativeItemMissing)]
    [DataRow(5, MirrorPulseRemoteFailureBoundary.NativeAccessDenied)]
    [DataRow(32, MirrorPulseRemoteFailureBoundary.NativeSharingViolation)]
    [DataRow(144, MirrorPulseRemoteFailureBoundary.NativeOther)]
    public void NativeFailuresKeepOnlyTheClosedCodeCategoryAndOriginalException(int code, MirrorPulseRemoteFailureBoundary expected)
    {
        var original = new Win32Exception(code, "PRIVATE_PATH PRIVATE_VALUE");
        int hresult = original.HResult;
        Assert.AreEqual(expected, MirrorPulseRemoteFailureClassifier.ReadBoundary(original));
        Assert.AreEqual(code, original.NativeErrorCode); Assert.AreEqual(hresult, original.HResult);
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
            [new("remoteFailureBoundary", MirrorPulseRemoteFailureClassifier.ReadBoundary(original).ToString()),
                new("exception", original.Message), new("path", "PRIVATE_PATH")]));
        Assert.AreEqual(expected.ToString(), safe.Fields["remoteFailureBoundary"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE", StringComparison.Ordinal));
    }
}
