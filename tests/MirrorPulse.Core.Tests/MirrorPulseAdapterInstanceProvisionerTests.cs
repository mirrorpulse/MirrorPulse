using System.Text;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseAdapterInstanceProvisionerTests
{
    [TestMethod]
    public async Task CreatesTwoConfiguredInstancesAndRollsBackCredentialOnRootCollision()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-provisioner-tests",
            Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"),
            Path.Combine(root, "data"));
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.provisioner"),
            "Example", "1.0.0", new ProtocolVersionRange(1, 1),
            new Dictionary<string, string>
            {
                ["win-x64"] = "worker/win-x64/worker.exe",
                ["win-arm64"] = "worker/win-arm64/worker.exe",
            },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
            [new AdapterRootDefinition("files", "Files", "Files", false)],
            configurationFields:
            [
                new("endpoint", "Server endpoint", AdapterConfigurationFieldKind.Text,
                    true, null, []),
                new("mode", "Transfer mode", AdapterConfigurationFieldKind.Choice,
                    false, "safe", ["safe", "fast"]),
                new("credentialReference", "Password", AdapterConfigurationFieldKind.Secret,
                    false, null, []),
            ]);
        var installation = new InstalledAdapter(manifest, InstallId.New(),
            Path.Combine(root, "installed"), new Sha256Digest(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow,
            AdapterLifecycleState.Installed);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.AddInstallationAsync(installation);
            var credentials = new MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials,
                paths.DataRootPath);
            var firstRequest = new MirrorPulseCreateInstanceRequest(
                installation.InstallId.ToString(), "First source",
                new Dictionary<string, string> { ["endpoint"] = "https://example.test/" },
                new Dictionary<string, string> { ["files"] = "Personal files" }, "password-one", true);
            AdapterInstance first = await provisioner.CreateAsync(firstRequest);
            Assert.AreEqual("Personal files", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().Label);
            Assert.AreEqual("safe", first.Configuration["mode"]);
            Assert.HasCount(1, first.CredentialReferences);
            Assert.AreEqual(first.CredentialReferences.Single(),
                first.Configuration["credentialReference"]);
            Assert.IsFalse(first.Configuration.Values.Contains("password-one"));
            Assert.AreEqual("password-one", credentials.Read(first.CredentialReferences.Single()));

            var duplicate = firstRequest with { DisplayName = "Duplicate", Secret = "password-two" };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                provisioner.CreateAsync(duplicate));
            Assert.HasCount(1, (await catalog.ReadAdapterTopologyAsync()).Instances);
            Assert.AreEqual("password-one", credentials.Read(first.CredentialReferences.Single()));
            Assert.AreEqual(1, credentials.DeletionCount);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provisioner.CreateAsync(
                firstRequest with
                {
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = "https://example.test/",
                        ["mode"] = "unknown",
                    },
                }));

            foreach (string managedKey in new[] { "credentialReference", "CredentialReference" })
            {
                var injected = new Dictionary<string, string>(firstRequest.Configuration)
                {
                    [managedKey] = "injected-secret",
                };
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provisioner.CreateAsync(
                    firstRequest with { Configuration = injected }));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ConfigureInstanceAsync(
                    first.InstanceId, "Injected", injected, new Dictionary<string, string>()));
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ConfigureInstanceAsync(
                first.InstanceId, "Invalid patch", new Dictionary<string, string> { ["mode"] = "unknown" },
                new Dictionary<string, string>()));
            AdapterInstance unchanged = (await catalog.ReadAdapterTopologyAsync()).Instances.Single();
            Assert.AreEqual(first.DisplayName, unchanged.DisplayName);
            Assert.AreEqual("safe", unchanged.Configuration["mode"]);

            AdapterInstance patched = await catalog.ConfigureInstanceAsync(first.InstanceId, "Patched",
                new Dictionary<string, string> { ["mode"] = "fast" }, new Dictionary<string, string>());
            Assert.AreEqual("https://example.test/", patched.Configuration["endpoint"]);
            Assert.AreEqual("fast", patched.Configuration["mode"]);
            Assert.AreEqual(first.CredentialReferences.Single(), patched.Configuration["credentialReference"]);
            Assert.IsFalse(patched.Configuration.Values.Contains("password-one"));
            Assert.AreEqual("password-one", credentials.Read(patched.CredentialReferences.Single()));

            AdapterInstance second = await provisioner.CreateAsync(firstRequest with
            {
                DisplayName = "Second source",
                Secret = null,
                RootLabels = new Dictionary<string, string> { ["files"] = "Work files" },
            });
            Assert.IsEmpty(second.CredentialReferences);
            Assert.HasCount(2, (await catalog.ReadAdapterTopologyAsync()).Instances);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SignedFormPersistsNamedPasswordsAndRejectsStaleOrInvalidEdits()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-provisioner-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var manifest = new AdapterManifest(2, AdapterId.Parse("example.signed-form"), "Example", "1.0.0",
            new ProtocolVersionRange(2, 2), new Dictionary<string, string>
            {
                ["win-x64"] = "worker/win-x64/worker.exe",
                ["win-arm64"] = "worker/win-arm64/worker.exe",
            },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, 1),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
            [new AdapterRootDefinition("files", "Files", "Files", false)])
        {
            Presentation = new("logo.png", new Dictionary<string, AdapterPackageLocale>
            {
                ["en-us"] = new("Source", "Connect an account",
                [
                    new(AdapterPackageFormKind.Text, "Account", Key: "account", Required: true),
                    new(AdapterPackageFormKind.MultiChoice, "Folders", Key: "folders", DefaultValue: "one",
                        Options: [new("one", "One"), new("two", "Two")]),
                    new(AdapterPackageFormKind.Password, "Password", Key: "password", Required: true),
                    new(AdapterPackageFormKind.Password, "PIN", Key: "pin"),
                ]),
            }),
        };
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow,
            AdapterLifecycleState.Installed);
        var credentials = new MemoryCredentialStore();
        var noLabels = new Dictionary<string, string>();
        var noValues = new Dictionary<string, string>();
        try
        {
            AdapterInstance created;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.AddInstallationAsync(installation);
                var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
                var request = new MirrorPulseCreateInstanceRequest(installation.InstallId.ToString(), "Source",
                    new Dictionary<string, string> { ["account"] = "sample" }, noLabels, null, true,
                    new Dictionary<string, string?> { ["password"] = "first-password", ["pin"] = "first-pin" });
                await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => provisioner.CreateAsync(request with
                {
                    Configuration = new Dictionary<string, string> { ["account"] = "sample", ["pin"] = "wrong-input" },
                }));
                created = await provisioner.CreateAsync(request);
                Assert.AreEqual("one", created.Configuration["folders"]);
                Assert.IsFalse(created.Configuration.ContainsKey("password"));
                Assert.IsFalse(created.Configuration.ContainsKey("pin"));
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var edits = new MirrorPulseAdapterInstanceProvisioner(reopened, credentials, paths.DataRootPath);
            var reloaded = (await reopened.ReadAdapterTopologyAsync()).Instances.Single();
            Assert.AreEqual(created.ConfigurationRevision, reloaded.ConfigurationRevision);
            string passwordReference = reloaded.CredentialBindings!["password"];
            string pinReference = reloaded.CredentialBindings["pin"];
            Assert.AreEqual("first-password", credentials.Read(passwordReference));
            Assert.AreEqual("first-pin", credentials.Read(pinReference));

            var replacement = new Dictionary<string, string?> { ["password"] = "new-password", ["pin"] = null };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => edits.ConfigureAsync(created.InstanceId, "Source",
                noValues, new Dictionary<string, string> { ["unknown"] = "Invalid root" },
                secretFields: replacement, expectedRevision: reloaded.ConfigurationRevision));
            Assert.AreEqual("first-password", credentials.Read(passwordReference));
            Assert.AreEqual("first-pin", credentials.Read(pinReference));
            Assert.AreEqual(2, credentials.Count);

            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => edits.ConfigureAsync(created.InstanceId, "Source",
                new Dictionary<string, string> { ["folders"] = "one,one" }, noLabels,
                expectedRevision: reloaded.ConfigurationRevision));
            AdapterInstance edited = await edits.ConfigureAsync(created.InstanceId, "Renamed deployment",
                new Dictionary<string, string> { ["folders"] = "one,two" }, noLabels,
                secretFields: replacement, expectedRevision: reloaded.ConfigurationRevision);
            Assert.AreEqual(created.InstanceId, edited.InstanceId);
            Assert.AreNotEqual(reloaded.ConfigurationRevision, edited.ConfigurationRevision);
            Assert.AreEqual("one,two", edited.Configuration["folders"]);
            Assert.AreEqual("new-password", credentials.Read(edited.CredentialBindings!["password"]));
            Assert.AreEqual(1, credentials.Count);
            Assert.HasCount(1, edited.CredentialReferences);
            await Assert.ThrowsExactlyAsync<AdapterConfigurationRevisionException>(() => edits.ConfigureAsync(created.InstanceId, "Stale",
                noValues, noLabels, secretFields: replacement, expectedRevision: reloaded.ConfigurationRevision));
            Assert.AreEqual(1, credentials.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemoryCredentialStore : ISecureCredentialStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public int DeletionCount { get; private set; }
        public int Count => _values.Count;

        public ValueTask SaveAsync(CredentialReference reference, ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[reference.ReferenceId] = secret.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SecureCredentialValue?> TryGetAsync(CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_values.TryGetValue(reference.ReferenceId, out byte[]? value)
                ? new SecureCredentialValue(reference, value) : null);
        }

        public ValueTask DeleteAsync(CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(reference.ReferenceId);
            DeletionCount++;
            return ValueTask.CompletedTask;
        }

        public string Read(string referenceId) => Encoding.UTF8.GetString(_values[referenceId]);
    }
}
