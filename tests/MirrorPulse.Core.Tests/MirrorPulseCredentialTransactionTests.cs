using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseCredentialTransactionTests
{
    [TestMethod]
    public async Task RequiredCredentialCannotBeRemovedAndOrdinaryPatchRetainsIt()
    {
        await using var fixture = await Fixture.OpenAsync(required: true);
        AdapterInstance original = await fixture.CreateAsync("old-secret");
        AdapterInstance patched = await fixture.Service.ConfigureAsync(original.InstanceId, "Patched",
            new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.AreEqual(original.Configuration["credentialReference"], patched.Configuration["credentialReference"]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Service.ConfigureAsync(original.InstanceId,
            "Removed", new Dictionary<string, string>(), new Dictionary<string, string>(), removeCredential: true));
        Assert.AreEqual(original.Configuration["credentialReference"],
            (await fixture.Catalog.ReadAdapterTopologyAsync()).Instances.Single().Configuration["credentialReference"]);
        Assert.AreEqual("old-secret", fixture.Store.Values.Values.Single());
    }

    [TestMethod]
    public async Task FailedSaveAndFailedCleanupRemainRecoverableWithoutPublishedReference()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.Store.FailSave = true;
        fixture.Store.FailDelete = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.CreateAsync("secret-marker"));
        Assert.IsEmpty((await fixture.Catalog.ReadAdapterTopologyAsync()).Instances);
        Assert.AreEqual(1, await fixture.Service.RecoverPendingCredentialsAsync());
        await fixture.ReopenAsync();
        fixture.Store.FailDelete = false;
        Assert.AreEqual(0, await fixture.Service.RecoverPendingCredentialsAsync());
        Assert.IsEmpty(fixture.Store.Values);
    }

    [TestMethod]
    public async Task FailedCreationOrRotationDoesNotPublishPartialCredentials()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.Store.FailSave = true;
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => fixture.CreateAsync("secret-marker"));
        Assert.IsFalse(error.ToString().Contains("secret-marker", StringComparison.Ordinal));
        Assert.IsEmpty((await fixture.Catalog.ReadAdapterTopologyAsync()).Instances);
        Assert.IsEmpty(fixture.Store.Values);
        fixture.Store.FailSave = false;
        AdapterInstance original = await fixture.CreateAsync("old-secret");
        fixture.Store.FailSave = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RotateAsync(original, "new-secret"));
        AdapterInstance current = (await fixture.Catalog.ReadAdapterTopologyAsync()).Instances.Single();
        Assert.AreEqual(original.Configuration["credentialReference"], current.Configuration["credentialReference"]);
        Assert.AreEqual("old-secret", fixture.Store.Values[current.Configuration["credentialReference"]]);
        Assert.HasCount(1, fixture.Store.Values);
    }

    [TestMethod]
    public async Task CatalogFailureRollsBackCandidateWithoutOverwritingOldValue()
    {
        await using var fixture = await Fixture.OpenAsync();
        AdapterInstance original = await fixture.CreateAsync("old-secret");
        await using var connection = new SqliteConnection($"Data Source={fixture.Paths.ProductCatalogDatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON adapter_topology BEGIN SELECT RAISE(ABORT, 'fixture'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.RotateAsync(original, "new-secret"));
        Assert.AreEqual(original.Configuration["credentialReference"],
            (await fixture.Catalog.ReadAdapterTopologyAsync()).Instances.Single().Configuration["credentialReference"]);
        Assert.HasCount(1, fixture.Store.Values);
        Assert.AreEqual("old-secret", fixture.Store.Values.Values.Single());
    }

    [TestMethod]
    public async Task CommittedRotationAndRemovalRecoverFailedRevocationAfterCatalogReopen()
    {
        await using var fixture = await Fixture.OpenAsync();
        AdapterInstance original = await fixture.CreateAsync("old-secret");
        fixture.Store.FailDelete = true;
        AdapterInstance rotated = await fixture.RotateAsync(original, "new-secret");
        Assert.AreNotEqual(original.Configuration["credentialReference"], rotated.Configuration["credentialReference"]);
        Assert.AreEqual("old-secret", fixture.Store.Values[original.Configuration["credentialReference"]]);
        Assert.AreEqual("new-secret", fixture.Store.Values[rotated.Configuration["credentialReference"]]);
        Assert.AreEqual(1, await fixture.Service.RecoverPendingCredentialsAsync());
        Assert.IsFalse(JsonSerializer.Serialize(await fixture.Catalog.ReadAdapterTopologyAsync())
            .Contains("new-secret", StringComparison.Ordinal));

        await fixture.ReopenAsync();
        fixture.Store.FailDelete = false;
        Assert.AreEqual(0, await fixture.Service.RecoverPendingCredentialsAsync());
        Assert.HasCount(1, fixture.Store.Values);
        fixture.Store.FailDelete = true;
        AdapterInstance removed = await fixture.Service.ConfigureAsync(rotated.InstanceId, "Removed",
            new Dictionary<string, string>(), new Dictionary<string, string>(), removeCredential: true);
        Assert.IsEmpty(removed.CredentialReferences);
        Assert.IsFalse(removed.Configuration.ContainsKey("credentialReference"));
        await fixture.ReopenAsync();
        fixture.Store.FailDelete = false;
        Assert.AreEqual(0, await fixture.Service.RecoverPendingCredentialsAsync());
        Assert.IsEmpty(fixture.Store.Values);
    }

    private sealed class FaultStore : ISecureCredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }

        public ValueTask SaveAsync(CredentialReference reference, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Add(reference.ReferenceId, Encoding.UTF8.GetString(secret.Span));
            if (FailSave) throw new IOException("secret-marker");
            return ValueTask.CompletedTask;
        }

        public ValueTask<SecureCredentialValue?> TryGetAsync(CredentialReference reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SecureCredentialValue?>(Values.TryGetValue(reference.ReferenceId, out string? value)
                ? new(reference, Encoding.UTF8.GetBytes(value)) : null);

        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            if (FailDelete) throw new IOException("secret-marker");
            Values.Remove(reference.ReferenceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MirrorPulse-credential-tests", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public MirrorPulseAdapterInstanceProvisioner Service { get; private set; } = null!;
        public FaultStore Store { get; } = new();
        private InstallId _installId;

        public static async Task<Fixture> OpenAsync(bool required = false)
        {
            var fixture = new Fixture();
            fixture.Paths = new(Path.Combine(fixture._root, "sync"), Path.Combine(fixture._root, "data"));
            await fixture.ReopenAsync();
            var manifest = new AdapterManifest(1, AdapterId.Parse("example.credentials"), "Credentials", "1.0.0",
                new ProtocolVersionRange(1, 1), new Dictionary<string, string>
                {
                    ["win-x64"] = "worker/win-x64/worker.exe",
                    ["win-arm64"] = "worker/win-arm64/worker.exe",
                },
                new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
                new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
                [new AdapterRootDefinition("files", "Files", "Files", false)], configurationFields:
                [new("credentialReference", "Password", AdapterConfigurationFieldKind.Secret, required, null, [])]);
            fixture._installId = InstallId.New();
            await fixture.Catalog.AddInstallationAsync(new(manifest, fixture._installId, fixture._root,
                new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null, true,
                DateTimeOffset.UtcNow, AdapterLifecycleState.Installed));
            return fixture;
        }

        public Task<AdapterInstance> CreateAsync(string secret) => Service.CreateAsync(new(_installId.ToString(), "Source",
            new Dictionary<string, string>(), new Dictionary<string, string>(), secret, false));
        public Task<AdapterInstance> RotateAsync(AdapterInstance instance, string secret)
            => Service.ConfigureAsync(instance.InstanceId, "Rotated", new Dictionary<string, string>(),
                new Dictionary<string, string>(), secret);

        public async Task ReopenAsync()
        {
            if (Catalog is not null) await Catalog.DisposeAsync();
            Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
            Service = new(Catalog, Store, Paths.DataRootPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
