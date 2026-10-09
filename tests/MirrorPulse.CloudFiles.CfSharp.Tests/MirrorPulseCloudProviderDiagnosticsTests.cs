using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MirrorPulseCloudProviderDiagnosticsTests
{
    [TestMethod]
    [DataRow("CloudProviderSession.FetchData", "FetchData", "IOException", "IO")]
    [DataRow("CloudProviderSession.FetchPlaceholders", "FetchPlaceholders", "InvalidDataException", "InvalidData")]
    [DataRow("secret/path/operation", "Other", "secret exception type", "Internal")]
    public async Task PublicActivityFailureKeepsNumericErrorAndClosedKindsWithoutArbitraryTags(
        string operation, string expectedKind, string errorType, string expectedFailure)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-provider-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var diagnostics = new MirrorPulseCloudProviderDiagnostics(root))
            {
                using var activity = CloudDiagnostics.ActivitySource.StartActivity("test-only failure");
                Assert.IsNotNull(activity);
                activity.SetTag("cfsharp.operation", operation);
                activity.SetTag("cfsharp.error.type", errorType);
                activity.SetTag("cfsharp.error.hresult", unchecked((int)0x80070185));
                activity.SetTag("exception.message", "secret password and user path");
                activity.SetTag("cfsharp.provider.path_fingerprint", "private path fingerprint");
            }
            string[] lines = await File.ReadAllLinesAsync(Path.Combine(root, "mirrorpulse.log"));
            Assert.HasCount(1, lines);
            Assert.IsFalse(lines[0].Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(lines[0].Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
            using JsonDocument json = JsonDocument.Parse(lines[0]);
            Assert.AreEqual("CloudProviderRequestFailed", json.RootElement.GetProperty("Code").GetString());
            JsonElement fields = json.RootElement.GetProperty("Fields");
            Assert.AreEqual("80070185", fields.GetProperty("hresult").GetString());
            Assert.AreEqual(expectedKind, fields.GetProperty("cloudRequestKind").GetString());
            Assert.AreEqual(expectedFailure, fields.GetProperty("failureCategory").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeFailureWithoutAnExceptionIsRecordedAndDisposalCanBeRepeated()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-provider-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new MirrorPulseCloudProviderDiagnostics(root);
            try
            {
                using var activity = CloudDiagnostics.ActivitySource.StartActivity("test-only native failure");
                Assert.IsNotNull(activity);
                activity.SetTag("cfsharp.operation", "CloudProviderSession.ValidateData");
                activity.SetTag("cfsharp.native.hresult", unchecked((int)0x80070185));
            }
            finally { await diagnostics.DisposeAsync(); }
            await diagnostics.DisposeAsync();
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "mirrorpulse.log")));
            Assert.AreEqual("ValidateData", json.RootElement.GetProperty("Fields").GetProperty("cloudRequestKind").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
