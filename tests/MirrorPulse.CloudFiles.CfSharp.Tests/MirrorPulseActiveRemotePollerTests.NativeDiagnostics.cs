using System.ComponentModel;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    [DataRow(2, "NativeItemMissing")]
    [DataRow(3, "NativeItemMissing")]
    [DataRow(5, "NativeAccessDenied")]
    [DataRow(32, "NativeSharingViolation")]
    [DataRow(144, "NativeOther")]
    public async Task NativePollFailurePreservesItsExceptionAndExportsOnlyTheClosedCategory(int code, string expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-poll-diagnostic", Guid.NewGuid().ToString("N"));
        InstanceId instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.native-poll-diagnostic"), instance,
            new AdapterRootDefinition("local", "Local", "Local", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Local",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var failure = new Win32Exception(code, "PRIVATE_PATH PRIVATE_VALUE");
        try
        {
            await using var poller = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, _, _) =>
                ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(failure), diagnosticsDirectory: directory);
            source.Set(new FakeEntry("original", "v1", CloudItemKind.File, "file.bin", 3));
            Assert.IsFalse(await poller.PollOnceAsync(instance));
            source.Set(new FakeEntry("original", "v2", CloudItemKind.File, "file.bin", 4));
            Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<Win32Exception>(() => poller.PollOnceAsync(instance).AsTask()));
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "mirrorpulse.log"));
            Assert.HasCount(1, lines);
            using var json = JsonDocument.Parse(lines[0]);
            Assert.AreEqual(expected, json.RootElement.GetProperty("Fields").GetProperty("remoteFailureBoundary").GetString());
            Assert.AreEqual("ApplyAndCommit", json.RootElement.GetProperty("Fields").GetProperty("remotePollStage").GetString());
            Assert.IsFalse(lines[0].Contains("PRIVATE", StringComparison.Ordinal));
            Assert.AreEqual(code, failure.NativeErrorCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
