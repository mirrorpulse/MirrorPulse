using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseProviderReadFailureTests
{
    [TestMethod]
    [DataRow("routing", "Routing", "InvalidData")]
    [DataRow("admission", "WorkAdmission", "InvalidOperation")]
    [DataRow("transport", "RangeTransport", "InvalidOperation")]
    [DataRow("content", "RangeContent", "IO")]
    public async Task ActualProviderFailuresKeepTheirOriginalResultAndReportOnlyClosedBoundaryFacts(string boundary, string expectedStage, string expectedKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-provider-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instance = InstanceId.New();
            var root = new RootRegistration(AdapterId.Parse("example.provider-stage"), instance, RootId.New(),
                "root", "Docs", "Docs", false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var router = new MirrorPulseRootRouter(Path.Combine(directory, "unused-sync"), [root]);
            var transport = new FailingTransport(boundary == "transport");
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var provider = new MirrorPulseDemandProvider(router, transport, namespaceWork: boundary == "admission" ? work : null);
            byte[] identity = boundary == "routing"
                ? MirrorPulsePlaceholderIdentity.Create(InstanceId.New(), "PRIVATE_REMOTE_ID").Encode()
                : router.CreateFileIdentity(instance, "root", "PRIVATE_REMOTE_ID").Encode();
            Exception original;
            await using (var diagnostics = new MirrorPulseCloudProviderDiagnostics(directory))
            {
                using var activity = CloudDiagnostics.ActivitySource.StartActivity("test-only real provider failure");
                Assert.IsNotNull(activity);
                activity.SetTag("cfsharp.operation", "CloudProviderSession.FetchData");
                using (var nested = new Activity("test-only nested provider work").Start())
                {
                    if (boundary == "routing")
                        original = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                            provider.OpenReadAsync("Docs/PRIVATE_FILENAME", identity, 1, 0, 1).AsTask());
                    else if (boundary == "admission")
                        original = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                            provider.OpenReadAsync("Docs/PRIVATE_FILENAME", identity, 1, 0, 1).AsTask());
                    else
                    {
                        await using var stream = await provider.OpenReadAsync("Docs/PRIVATE_FILENAME", identity, 1, 0, 1);
                        if (boundary == "transport")
                        {
                            original = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => { _ = await stream.ReadAsync(new byte[1]); });
                            Assert.AreSame(transport.Original, original);
                        }
                        else
                            original = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () => { _ = await stream.ReadAsync(new byte[1]); });
                    }
                }
                Assert.AreEqual(boundary is "transport" or "content" ? 1 : 0, transport.Calls);
                activity.SetTag("cfsharp.error.hresult", original.HResult);
                activity.SetTag("cfsharp.error.type", original.GetType().Name);
                activity.SetTag("exception.message", "PRIVATE_PASSWORD_AND_USER_PATH");
            }
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "mirrorpulse.log"));
            Assert.HasCount(1, lines); Assert.IsFalse(lines[0].Contains("PRIVATE_", StringComparison.Ordinal));
            using var log = JsonDocument.Parse(lines[0]); var fields = log.RootElement.GetProperty("Fields");
            Assert.AreEqual("FetchData", fields.GetProperty("cloudRequestKind").GetString());
            Assert.AreEqual(expectedStage, fields.GetProperty("providerReadStage").GetString());
            Assert.AreEqual(expectedKind, fields.GetProperty("providerReadFailureKind").GetString());
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "unused-sync")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("FetchData")]
    [DataRow("Rename")]
    public async Task ArbitraryActivityValuesCannotBecomeProviderBoundaryFields(string operation)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-provider-tags-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var diagnostics = new MirrorPulseCloudProviderDiagnostics(directory))
            {
                using var activity = CloudDiagnostics.ActivitySource.StartActivity("test-only unknown provider tags");
                Assert.IsNotNull(activity);
                activity.SetTag("cfsharp.operation", "CloudProviderSession." + operation);
                activity.SetTag("cfsharp.error.hresult", unchecked((int)0x80131509));
                activity.SetTag(MirrorPulseProviderReadFailure.StageTag, "PRIVATE_STAGE");
                activity.SetTag(MirrorPulseProviderReadFailure.KindTag, "PRIVATE_KIND");
            }
            string line = await File.ReadAllTextAsync(Path.Combine(directory, "mirrorpulse.log"));
            Assert.IsFalse(line.Contains("PRIVATE_", StringComparison.Ordinal));
            using var log = JsonDocument.Parse(line); var fields = log.RootElement.GetProperty("Fields");
            Assert.IsFalse(fields.TryGetProperty("providerReadStage", out _));
            Assert.IsFalse(fields.TryGetProperty("providerReadFailureKind", out _));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FailingTransport(bool failRequest) : IMirrorPulseWorkerRangeTransport
    {
        internal InvalidOperationException Original { get; } = new("PRIVATE_TRANSPORT_FAILURE");
        internal int Calls { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return failRequest ? ValueTask.FromException<Stream>(Original) : ValueTask.FromResult<Stream>(new MemoryStream([], writable: false));
        }
    }
}
