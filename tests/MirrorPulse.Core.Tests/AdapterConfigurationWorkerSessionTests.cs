using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterConfigurationWorkerSessionTests
{
    [TestMethod]
    [DataRow(false, false, "authorize")]
    [DataRow(true, true, "authorize")]
    [DataRow(false, false, "desktop-authorize")]
    public async Task RealWorkerAuthorizesBeforeDeploymentOrWhileAnExistingDeploymentIsDisabled(bool existing, bool cancel,
        string callback)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string directory = Directory.CreateTempSubdirectory("MirrorPulse-authorization-test-").FullName;
        string authorizationRoot = Path.Combine(directory, "auth");
        string liveWorkerState = Path.Combine(directory, "deployed");
        string marker = Path.Combine(liveWorkerState, ".fixture-live-worker-state");
        try
        {
            Directory.CreateDirectory(liveWorkerState);
            await File.WriteAllTextAsync(marker, "current deployment", timeout.Token);
            InstalledAdapter installation = NewFixture(callback);
            AdapterInstance? instance = existing
                ? new(installation.AdapterId, installation.InstallId, InstanceId.New(), "Existing",
                    new Dictionary<string, string> { ["endpoint"] = "https://configured.example.test/" }, [],
                    liveWorkerState, liveWorkerState, false, AdapterLifecycleState.Disabled, null, DateTimeOffset.UtcNow)
                : null;
            var request = new AdapterConfigurationCallbackRequest(Guid.NewGuid(), callback, DateTimeOffset.UtcNow.AddMinutes(1));
            await using (var session = await AdapterConfigurationWorkerSession.OpenAsync(installation, instance,
                new Dictionary<string, string>(), authorizationRoot, request, new WindowsCredentialManagerStore(), timeout.Token))
            {
                AdapterConfigurationCallbackResult started = await session.ExchangeAsync(AdapterConfigurationCallbackContract.StartMessage, timeout.Token);
                Assert.AreEqual(AdapterConfigurationCallbackState.Pending, started.State);
                Assert.AreEqual(callback == "desktop-authorize" ? null : new Uri("https://example.test/authorize"), started.InteractionUri);
                string workspace = Directory.EnumerateDirectories(authorizationRoot).Single();
                Assert.IsTrue(File.Exists(Path.Combine(workspace, "state", ".fixture-authorization-worker-state")));

                AdapterConfigurationCallbackResult finished = await session.ExchangeAsync(cancel
                    ? AdapterConfigurationCallbackContract.CancelMessage : AdapterConfigurationCallbackContract.PollMessage, timeout.Token);
                Assert.AreEqual(request.InvocationId, finished.InvocationId);
                Assert.AreEqual(cancel ? AdapterConfigurationCallbackState.Cancelled : AdapterConfigurationCallbackState.Completed,
                    finished.State);
                if (cancel) Assert.IsNull(finished.Writes);
                else Assert.AreEqual("token", finished.Writes!.Single().Key);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    session.ExchangeAsync(AdapterConfigurationCallbackContract.PollMessage, timeout.Token));
            }
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(authorizationRoot));
            Assert.AreEqual("current deployment", await File.ReadAllTextAsync(marker, timeout.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("other-session", false)]
    [DataRow("other-invocation", true)]
    [DataRow("other-field", true)]
    [DataRow("insecure-page", false)]
    [DataRow("sync-message", false)]
    public async Task RealWorkerRejectsUnboundFramesInvalidResultsAndSyncTraffic(string callback, bool respondOnPoll)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string directory = Directory.CreateTempSubdirectory("MirrorPulse-authorization-test-").FullName;
        try
        {
            var request = new AdapterConfigurationCallbackRequest(Guid.NewGuid(), callback, DateTimeOffset.UtcNow.AddMinutes(1));
            await using var session = await AdapterConfigurationWorkerSession.OpenAsync(NewFixture(callback), null,
                new Dictionary<string, string>(), directory, request, new WindowsCredentialManagerStore(), timeout.Token);
            if (respondOnPoll) await session.ExchangeAsync(AdapterConfigurationCallbackContract.StartMessage, timeout.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => session.ExchangeAsync(respondOnPoll
                ? AdapterConfigurationCallbackContract.PollMessage : AdapterConfigurationCallbackContract.StartMessage, timeout.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ExpiredOrUndeclaredCallbacksNeverOpenAnAuthorizationWorkspace()
    {
        string directory = Directory.CreateTempSubdirectory("MirrorPulse-authorization-test-").FullName;
        try
        {
            InstalledAdapter installation = NewFixture("allowed");
            var empty = new Dictionary<string, string>();
            var credentials = new WindowsCredentialManagerStore();
            var expired = new AdapterConfigurationCallbackRequest(Guid.NewGuid(), "allowed", DateTimeOffset.UtcNow.AddSeconds(-1));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => AdapterConfigurationWorkerSession.OpenAsync(
                installation, null, empty, directory, expired, credentials, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => AdapterConfigurationWorkerSession.OpenAsync(
                installation, null, empty, directory, expired with { CallbackId = "other", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) },
                credentials, CancellationToken.None));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task NewDeploymentWorkerCannotReadAnExistingCredential()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string directory = Directory.CreateTempSubdirectory("MirrorPulse-authorization-test-").FullName;
        try
        {
            const string callback = "foreign-credential";
            var request = new AdapterConfigurationCallbackRequest(Guid.NewGuid(), callback, DateTimeOffset.UtcNow.AddMinutes(1));
            await using var session = await AdapterConfigurationWorkerSession.OpenAsync(NewFixture(callback), null,
                new Dictionary<string, string>(), directory, request, new WindowsCredentialManagerStore(), timeout.Token);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                session.ExchangeAsync(AdapterConfigurationCallbackContract.StartMessage, timeout.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static InstalledAdapter NewFixture(string callback, string adapterId = "example.protocolfixture")
    {
        string executable = Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe");
        var manifest = new AdapterManifest(2, AdapterId.Parse(adapterId), "Fixture", "1.0.0", new(2, 2),
            new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
            new(null), new(null, 1), new(true, false, true, true), ["en-US"], "1.0.0", [new("files", "Files", "Files", false)])
        {
            Presentation = new("logo.png", new Dictionary<string, AdapterPackageLocale>
            {
                ["en-us"] = new("Fixture", "Authorization fixture",
                [new(AdapterPackageFormKind.Text, "Endpoint", "endpoint", Required: true),
                    new(AdapterPackageFormKind.Password, "Token", "token", Required: true),
                    new(AdapterPackageFormKind.Callback, "Authorize", callback, WritableFields: ["token"])]),
            }),
        };
        return new(manifest, InstallId.New(), Path.GetDirectoryName(executable)!, new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
    }
}
