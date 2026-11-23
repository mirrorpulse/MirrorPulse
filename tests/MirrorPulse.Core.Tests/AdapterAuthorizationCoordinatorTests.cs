using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterAuthorizationCoordinatorTests
{
    [TestMethod]
    public async Task FirstAuthorizationReservesDeploymentAndCommitsOnlyWithAValidForm()
    {
        await WithAuthorizationFixtureAsync(async (catalog, authorization, credentials, installation, token) =>
        {
            var inputs = Values("https://example.test/storage/");
            AdapterAuthorizationStatus ready = await CompleteAsync(authorization, new(installation.InstallId, null, null,
                null, "authorize", inputs), token);
            Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync(token)).Instances);
            Assert.AreEqual(0, credentials.Count);
            Assert.DoesNotContain("fixture-authorization", JsonSerializer.Serialize(ready));

            var form = new MirrorPulseCreateInstanceRequest(installation.InstallId.ToString(), "Source", inputs,
                new Dictionary<string, string> { ["files"] = "Source Files" }, null, true);
            Guid[] ids = [ready.AuthorizationId];
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() =>
                authorization.CreateAsync(form with { Configuration = Values("https://other.example.test/") }, ids, token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => authorization.CreateAsync(form with
            {
                RootLabels = new Dictionary<string, string> { ["other"] = "Invalid label" },
            }, ids, token));
            Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync(token)).Instances);
            Assert.AreEqual(0, credentials.Count);
            Assert.AreEqual(AdapterAuthorizationPhase.ReadyToSave, (await authorization.PollAsync(ready.AuthorizationId, token)).Phase);

            AdapterInstance created = await authorization.CreateAsync(form, ids, token);
            Assert.AreEqual(ready.DeploymentId, created.InstanceId.ToString());
            Assert.AreEqual("fixture-authorization", credentials.Read(created.CredentialBindings!["token"]));
            Assert.DoesNotContain("fixture-authorization", JsonSerializer.Serialize(await catalog.ReadAdapterTopologyAsync(token)));
            Assert.AreEqual(AdapterAuthorizationPhase.Saved, (await authorization.PollAsync(ready.AuthorizationId, token)).Phase);
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => authorization.CreateAsync(form, ids, token));
            Assert.HasCount(1, (await catalog.ReadAdapterTopologyAsync(token)).Instances);
            Assert.AreEqual(1, credentials.Count);
        });
    }

    [TestMethod]
    public async Task ReauthorizationCannotCrossDeploymentsOrOverwriteAConcurrentEdit()
    {
        await WithAuthorizationFixtureAsync(async (catalog, authorization, credentials, installation, token) =>
        {
            var inputs = Values("https://example.test/storage/");
            var labels = new Dictionary<string, string>();
            var originalSecret = new Dictionary<string, string?> { ["token"] = "first-token" };
            var form = new MirrorPulseCreateInstanceRequest(installation.InstallId.ToString(), "Source", inputs, labels, null,
                false, originalSecret);
            AdapterInstance original = await authorization.CreateAsync(form, null, token);
            var other = await authorization.CreateAsync(form with
            {
                DisplayName = "Other",
                RootLabels = new Dictionary<string, string> { ["files"] = "Other Files" },
            }, null, token);
            var target = new AdapterAuthorizationStart(installation.InstallId, original.InstanceId, null,
                original.ConfigurationRevision, "authorize", inputs);
            AdapterAuthorizationStatus ready = await CompleteAsync(authorization, target, token);
            Guid[] ids = [ready.AuthorizationId];
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() =>
                authorization.ConfigureAsync(other.InstanceId, "Other", inputs, labels, null, false, null,
                    other.ConfigurationRevision, ids, token));
            await authorization.ConfigureAsync(original.InstanceId, "Changed", inputs, labels, null, false, null,
                original.ConfigurationRevision, null, token);
            await Assert.ThrowsExactlyAsync<AdapterConfigurationRevisionException>(() =>
                authorization.ConfigureAsync(original.InstanceId, "Source", inputs, labels, null, false, null,
                    original.ConfigurationRevision, ids, token));
            Assert.AreEqual("first-token", credentials.Read(original.CredentialBindings!["token"]));
            Assert.AreEqual(AdapterAuthorizationPhase.Cancelled, (await authorization.CancelAsync(ready.AuthorizationId, token)).Phase);

            MirrorPulseAdapterTopology current = await catalog.ReadAdapterTopologyAsync(token);
            AdapterInstance changed = current.Instances.Single(item => item.InstanceId == original.InstanceId);
            AdapterAuthorizationStatus replacement = await CompleteAsync(authorization, target with
            {
                ExpectedRevision = changed.ConfigurationRevision,
            }, token);
            await authorization.ConfigureAsync(changed.InstanceId, "Changed", inputs, labels, null, false, null,
                changed.ConfigurationRevision, [replacement.AuthorizationId], token);
            current = await catalog.ReadAdapterTopologyAsync(token);
            Assert.AreEqual("fixture-authorization", credentials.Read(current.Instances.Single(item => item.InstanceId == changed.InstanceId)
                .CredentialBindings!["token"]));
            Assert.AreEqual("first-token", credentials.Read(other.CredentialBindings!["token"]));
            Assert.AreEqual(2, credentials.Count);
        });
    }

    private static Dictionary<string, string> Values(string endpoint) => new(StringComparer.Ordinal) { ["endpoint"] = endpoint };

    private static async Task<AdapterAuthorizationStatus> CompleteAsync(AdapterAuthorizationCoordinator authorization,
        AdapterAuthorizationStart arguments, CancellationToken token)
    {
        AdapterAuthorizationStatus pending = await authorization.StartAsync(arguments, token);
        Assert.AreEqual(AdapterAuthorizationPhase.Pending, pending.Phase);
        AdapterAuthorizationStatus ready = await authorization.PollAsync(pending.AuthorizationId, token);
        Assert.AreEqual(AdapterAuthorizationPhase.ReadyToSave, ready.Phase);
        Assert.IsNull(ready.InteractionUri);
        return ready;
    }

    private static async Task WithAuthorizationFixtureAsync(Func<MirrorPulseProductCatalog, AdapterAuthorizationCoordinator,
        MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore, InstalledAdapter, CancellationToken, Task> run)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string root = Directory.CreateTempSubdirectory("MirrorPulse-authorization-save-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: timeout.Token);
            var credentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            await using var authorization = new AdapterAuthorizationCoordinator(catalog, credentials, provisioner, paths.DataRootPath);
            InstalledAdapter installation = AdapterConfigurationWorkerSessionTests.NewFixture("authorize");
            await catalog.AddInstallationAsync(installation, timeout.Token);
            await run(catalog, authorization, credentials, installation, timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
