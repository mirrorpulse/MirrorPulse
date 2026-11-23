using System.Security.Cryptography;
using System.Text;
using CfSharp;
using CfSharp.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseProductCatalogTests
{
    [TestMethod]
    public async Task CatalogRejectsSecondInstallationWhenFirstPackageRequiresUniqueness()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        AdapterId adapterId = AdapterId.Parse("example.unique");
        var manifest = new AdapterManifest(1, adapterId, "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string>
            {
                ["win-x64"] = "worker/adapter.exe",
                ["win-arm64"] = "worker/adapter.exe",
            }, new AdapterInstallPolicy(1), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, false), ["en-US"], "1.0.0");
        InstalledAdapter NewInstallation(string directory, AdapterManifest packageManifest) =>
            new(packageManifest, InstallId.New(), Path.Combine(root, directory),
                new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null,
                true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.AddInstallationAsync(NewInstallation("first", manifest));
            var permissive = new AdapterManifest(1, adapterId, "Example", "2.0.0",
                manifest.Protocol, manifest.Entrypoints, new AdapterInstallPolicy(null),
                manifest.InstancePolicy, manifest.Capabilities, manifest.Locales,
                manifest.MinimumMirrorPulseVersion);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                catalog.AddInstallationAsync(NewInstallation("second", permissive)));
            Assert.HasCount(1, (await catalog.ReadAdapterTopologyAsync()).Installations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatingAnInstanceAtomicallyRegistersManifestRootsAndCacheDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        AdapterId adapterId = AdapterId.Parse("example.instance");
        var manifest = new AdapterManifest(1, adapterId, "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string>
            {
                ["win-x64"] = "worker/adapter.exe",
                ["win-arm64"] = "worker/adapter.exe",
            }, new AdapterInstallPolicy(null), new AdapterInstancePolicy(2, 2),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
            [new AdapterRootDefinition("documents", "Documents", "Documents", false),
             new AdapterRootDefinition("archive", "Archive", "Archive", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null,
            true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.AddInstallationAsync(installation);
            AdapterInstance instance = await catalog.CreateInstanceAsync(
                installation.InstallId,
                "Example storage",
                new Dictionary<string, string> { ["endpoint"] = "https://example.test" },
                ["credential-1"],
                Path.Combine(root, "cache", "files"),
                Path.Combine(root, "cache", "transfers"));

            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            Assert.HasCount(1, topology.Instances);
            Assert.HasCount(2, topology.Roots);
            Assert.AreEqual(instance.InstanceId, topology.Roots[0].InstanceId);
            Assert.AreEqual(RootRegistrationState.Active, topology.Roots[1].State);
            Assert.IsTrue(Directory.Exists(instance.FileCacheDirectory));
            Assert.IsTrue(Directory.Exists(instance.TransferCacheDirectory));
            Assert.AreEqual("credential-1", instance.CredentialReferences.Single());

            AdapterInstance second = await catalog.CreateInstanceAsync(
                installation.InstallId, "Second storage", new Dictionary<string, string>(), [],
                Path.Combine(root, "cache-2", "files"), Path.Combine(root, "cache-2", "transfers"),
                rootLabels: new Dictionary<string, string>
                {
                    ["documents"] = "Documents 2",
                    ["archive"] = "Archive 2",
                });
            MirrorPulseAdapterTopology withSecond = await catalog.ReadAdapterTopologyAsync();
            Assert.HasCount(2, withSecond.Instances);
            Assert.HasCount(4, withSecond.Roots);
            Assert.IsTrue(withSecond.Roots.Where(item => item.InstanceId == second.InstanceId)
                .Select(item => item.Label).SequenceEqual(["Documents 2", "Archive 2"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnoozedConflictNotificationSurvivesRestartAndCanBeRestored()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Guid conflictId = Guid.NewGuid();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SetConflictSnoozedAsync(conflictId, true);
                Assert.Contains(conflictId, await catalog.ReadSnoozedConflictIdsAsync());
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.Contains(conflictId, await reopened.ReadSnoozedConflictIdsAsync());
                await reopened.SetConflictSnoozedAsync(conflictId, false);
                Assert.DoesNotContain(conflictId, await reopened.ReadSnoozedConflictIdsAsync());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UploadConflictSurvivesCatalogRestartAndKeepsJournalOperationPending()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Guid operationId = Guid.NewGuid();
        var conflict = new MirrorPulseConflictRecord(operationId, InstanceId.New(),
            operationId.ToString("D"), "Documents/note.txt",
            MirrorPulseConflictReason.StaleRemoteRevision,
            MirrorPulseVersionComparison.Diverged, "base", "changed", DateTimeOffset.UtcNow);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SaveUploadConflictAsync(conflict);
                await catalog.SaveUploadConflictAsync(conflict);
                Assert.IsTrue(await catalog.HasPendingUploadConflictAsync(operationId));
                await catalog.SetConflictSnoozedAsync(operationId, true);
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.IsTrue(await reopened.HasPendingUploadConflictAsync(operationId));
                MirrorPulseConflictRecord stored = (await reopened.ReadUploadConflictsAsync()).Single();
                Assert.AreEqual(conflict.InstanceId, stored.InstanceId);
                Assert.AreEqual(conflict.RelativePath, stored.RelativePath);
                Assert.AreEqual(conflict.LocalRevision, stored.LocalRevision);
                Assert.AreEqual(conflict.RemoteRevision, stored.RemoteRevision);
                Assert.AreEqual(MirrorPulseConflictSource.Upload, stored.Source);
                Assert.Contains(operationId, await reopened.ReadSnoozedConflictIdsAsync());
                Assert.IsNotNull(await reopened.ReadUploadConflictAsync(operationId));
                await reopened.SetUploadConflictStatusAsync(operationId, MirrorPulseConflictStatus.Resolved);
                Assert.IsFalse(await reopened.HasPendingUploadConflictAsync(operationId));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AdapterTopologyReopensAndRejectsCollidingRootWithoutChangingStoredInventory()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        AdapterId adapterId = AdapterId.Parse("example.drive");
        var installId = InstallId.New();
        var instanceId = InstanceId.New();
        var manifest = new AdapterManifest(1, adapterId, "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string>
            {
                ["win-x64"] = "worker/win-x64/adapter.exe",
                ["win-arm64"] = "worker/win-arm64/adapter.exe",
            }, new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0");
        var installation = new InstalledAdapter(manifest, installId, Path.Combine(root, "installed"),
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null,
            true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(adapterId, installId, instanceId, "Personal drive",
            new Dictionary<string, string> { ["server"] = "example.test" }, ["credential-1"],
            Path.Combine(root, "file-cache"), Path.Combine(root, "transfer-cache"), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        RootRegistration first = AdapterRootRegistrationMapper.Map(adapterId, instanceId,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var secondInstallId = InstallId.New();
        var secondInstanceId = InstanceId.New();
        var newerManifest = new AdapterManifest(1, adapterId, "Example", "2.0.0",
            manifest.Protocol, manifest.Entrypoints, manifest.InstallPolicy, manifest.InstancePolicy,
            manifest.Capabilities, manifest.Locales, manifest.MinimumMirrorPulseVersion);
        var newerInstallation = new InstalledAdapter(newerManifest, secondInstallId,
            Path.Combine(root, "installed-newer"), new Sha256Digest(new string('B', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var secondInstance = new AdapterInstance(adapterId, secondInstallId, secondInstanceId, "Backup",
            new Dictionary<string, string>(), [], Path.Combine(root, "files-2"),
            Path.Combine(root, "transfer-2"), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        RootRegistration secondRoot = AdapterRootRegistrationMapper.Map(adapterId, secondInstanceId,
            new AdapterRootDefinition("backup", "Backup", "Backup", false), RootRegistrationState.Active);
        var topology = new MirrorPulseAdapterTopology(
            [installation, newerInstallation], [instance, secondInstance], [first, secondRoot]);

        try
        {
            MirrorPulseAdapterTopology beforeOpen = await MirrorPulseProductCatalog
                .ReadAdapterTopologySnapshotAsync(paths);
            Assert.IsEmpty(beforeOpen.Installations);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SaveAdapterTopologyAsync(topology);
                MirrorPulseAdapterTopology whileHostOwnsCatalog = await MirrorPulseProductCatalog
                    .ReadAdapterTopologySnapshotAsync(paths);
                Assert.HasCount(2, whileHostOwnsCatalog.Installations);
                Assert.AreEqual(installId, whileHostOwnsCatalog.Installations[0].InstallId);
                RootRegistration duplicate = AdapterRootRegistrationMapper.Map(adapterId, instanceId,
                    new AdapterRootDefinition("other", "files", "files", false), RootRegistrationState.Disabled);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    catalog.SaveAdapterTopologyAsync(new(
                        [installation, newerInstallation], [instance, secondInstance],
                        [first, secondRoot, duplicate])));
                MirrorPulseAdapterTopology disabled = await catalog.SetInstanceEnabledAsync(instanceId, false);
                Assert.IsFalse(disabled.Instances[0].Enabled);
                Assert.AreEqual(RootRegistrationState.Disabled, disabled.Roots[0].State);
                Assert.IsTrue(disabled.Instances[1].Enabled);
                Assert.AreEqual(RootRegistrationState.Active, disabled.Roots[1].State);
                MirrorPulseAdapterTopology switched = await catalog.SelectInstanceInstallationAsync(
                    instanceId, secondInstallId);
                Assert.AreEqual(secondInstallId, switched.Instances[0].InstallId);
                Assert.AreEqual(instanceId, switched.Roots[0].InstanceId);
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseAdapterTopology restored = await reopened.ReadAdapterTopologyAsync();
            MirrorPulseAdapterTopology snapshot = await MirrorPulseProductCatalog.ReadAdapterTopologySnapshotAsync(paths);
            Assert.HasCount(2, restored.Installations);
            Assert.HasCount(2, restored.Instances);
            Assert.HasCount(2, restored.Roots);
            Assert.AreEqual("example.drive", restored.Installations[0].AdapterId.ToString());
            Assert.AreEqual("Personal drive", restored.Instances[0].DisplayName);
            Assert.IsFalse(restored.Instances[0].Enabled);
            Assert.AreEqual(secondInstallId, restored.Instances[0].InstallId);
            Assert.AreEqual(RootRegistrationState.Disabled, restored.Roots[0].State);
            Assert.AreEqual("credential-1", restored.Instances[0].CredentialReferences[0]);
            Assert.AreEqual(first.RootId, restored.Roots[0].RootId);
            Assert.AreEqual(first.RootId, snapshot.Roots[0].RootId);
            Assert.IsNotNull(await reopened.FindAsync(installId));
            Assert.AreEqual("2.0.0", (await reopened.FindAsync(secondInstallId))?.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SelectingASignedSingleRootVersionPreservesTheEffectiveSettingsOrRejectsMultipleRoots(bool twoRoots)
    {
        string root = Directory.CreateTempSubdirectory("MirrorPulse-form-migration-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "state"));
            InstalledAdapter newer = AdapterConfigurationWorkerSessionTests.NewFixture("authorize");
            AdapterManifest latest = newer.Manifest;
            var previous = new AdapterManifest(1, latest.AdapterId, latest.Publisher, "0.9.0", latest.Protocol,
                latest.Entrypoints, new(null), new(null, null), latest.Capabilities, latest.Locales,
                latest.MinimumMirrorPulseVersion, latest.RootDefinitions);
            var older = new InstalledAdapter(previous, InstallId.New(), Path.Combine(root, "older"),
                new(new string('B', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow,
                AdapterLifecycleState.Installed);
            InstanceId id = InstanceId.New();
            var instance = new AdapterInstance(older.AdapterId, older.InstallId, id, "Documents", new Dictionary<string, string>
            {
                ["endpoint"] = "https://default.example.test/",
                ["root.files.endpoint"] = "https://selected.example.test/",
                ["root.files.extra"] = "preserved",
            }, [], Path.Combine(root, "files"), Path.Combine(root, "transfers"), true, AdapterLifecycleState.Enabled,
                null, DateTimeOffset.UtcNow);
            RootRegistration files = AdapterRootRegistrationMapper.Map(older.AdapterId, id, latest.RootDefinitions.Single(),
                RootRegistrationState.Active);
            RootRegistration extra = AdapterRootRegistrationMapper.Map(older.AdapterId, id, new("extra", "Extra", "Extra", false),
                RootRegistrationState.Disabled);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.SaveAdapterTopologyAsync(new([older, newer], [instance], twoRoots ? [files, extra] : [files]));

            if (twoRoots)
            {
                await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() =>
                    catalog.SelectInstanceInstallationAsync(id, newer.InstallId));
                var retained = await catalog.ReadAdapterTopologyAsync();
                Assert.AreEqual(older.InstallId, retained.Instances.Single().InstallId);
                CollectionAssert.AreEqual(new[] { files.RootId, extra.RootId }, retained.Roots.Select(item => item.RootId).ToArray());
                return;
            }

            await catalog.SelectInstanceInstallationAsync(id, newer.InstallId);
            var migrated = await catalog.ReadAdapterTopologyAsync();
            Assert.AreEqual(files.RootId, migrated.Roots.Single().RootId);
            AdapterInstance current = migrated.Instances.Single();
            Assert.AreEqual(newer.InstallId, current.InstallId);
            Assert.AreEqual("https://selected.example.test/", current.Configuration["endpoint"]);
            Assert.AreEqual("preserved", current.Configuration["root.files.extra"]);
            Assert.IsFalse(current.Configuration.ContainsKey("root.files.endpoint"));

            var reverted = await catalog.SelectInstanceInstallationAsync(id, older.InstallId);
            Assert.AreEqual(files.RootId, reverted.Roots.Single().RootId);
            Assert.AreEqual("https://selected.example.test/", reverted.Instances.Single().Configuration["endpoint"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("webdav", true)]
    [DataRow("smb", true)]
    [DataRow("webdav", false)]
    public async Task NetworkFormMigrationKeepsTheAuthorizedRootCredentialAndSupportsRollback(string provider, bool authorized)
    {
        string root = Directory.CreateTempSubdirectory("MirrorPulse-network-migration-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "state"));
            AdapterId adapterId = AdapterId.Parse("com.mirrorpulse.adapter." + provider);
            AdapterRootDefinition definition = new(provider, "Documents", "Documents", false);
            var entrypoints = new Dictionary<string, string>
            {
                ["win-x64"] = "worker/win-x64/worker.exe",
                ["win-arm64"] = "worker/win-arm64/worker.exe",
            };
            AdapterManifest Manifest(int schema, string version, int? rootLimit) => new(schema, adapterId, "MirrorPulse", version,
                new(2, 2), entrypoints, new(null), new(null, rootLimit), new(true, false, true, true), ["en-US"], "1.0.0", [definition]);
            InstalledAdapter Installation(AdapterManifest manifest) => new(manifest, InstallId.New(), Path.Combine(root, manifest.Version),
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            InstalledAdapter previous = Installation(Manifest(1, "0.9.0", null));
            InstalledAdapter next = Installation(Manifest(2, "1.0.0", 1) with
            {
                Presentation = new("logo.png", new Dictionary<string, AdapterPackageLocale>
                {
                    ["en-us"] = new("Network provider", "Test fixture", [new(AdapterPackageFormKind.Password, "Password", "password")]),
                }),
            });
            InstanceId id = InstanceId.New();
            const string scopedReference = "authorized-reference";
            string scopedKey = $"root.{provider}.credentialReference";
            var instance = new AdapterInstance(adapterId, previous.InstallId, id, "Documents", new Dictionary<string, string>
            {
                ["credentialReference"] = "deployment-default",
                [scopedKey] = authorized ? scopedReference : "other-deployment-reference",
            }, ["deployment-default", scopedReference], Path.Combine(root, "files"), Path.Combine(root, "transfers"), true,
                AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            RootRegistration registration = AdapterRootRegistrationMapper.Map(adapterId, id, definition, RootRegistrationState.Active);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.SaveAdapterTopologyAsync(new([previous, next], [instance], [registration]));

            if (!authorized)
            {
                await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => catalog.SelectInstanceInstallationAsync(id, next.InstallId));
                var retained = await catalog.ReadAdapterTopologyAsync();
                Assert.AreEqual(previous.InstallId, retained.Instances.Single().InstallId);
                Assert.AreEqual("other-deployment-reference", retained.Instances.Single().Configuration[scopedKey]);
                return;
            }

            var upgraded = await catalog.SelectInstanceInstallationAsync(id, next.InstallId);
            AdapterInstance updated = upgraded.Instances.Single();
            Assert.AreEqual(scopedReference, updated.CredentialBindings!["password"]);
            Assert.IsFalse(updated.Configuration.ContainsKey(scopedKey));
            Assert.IsFalse(updated.Configuration.ContainsKey("credentialReference"));
            if (provider == "webdav") Assert.AreEqual("Basic", updated.Configuration["authentication"]);
            CollectionAssert.AreEqual(instance.CredentialReferences.ToArray(), updated.CredentialReferences.ToArray());
            Assert.AreEqual(registration.RootId, upgraded.Roots.Single().RootId);
            var rolledBack = await catalog.SelectInstanceInstallationAsync(id, previous.InstallId);
            Assert.AreEqual(scopedReference, rolledBack.Instances.Single().Configuration["credentialReference"]);
            Assert.IsNull(rolledBack.Instances.Single().CredentialBindings);
            Assert.AreEqual(registration.RootId, rolledBack.Roots.Single().RootId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task VersionOneCatalogUpgradesWithoutLosingWorkerFingerprint()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!);
        Guid operationId = Guid.NewGuid();
        var instance = InstanceId.New();
        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("legacy request"));
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.ProductCatalogDatabasePath,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand legacy = connection.CreateCommand();
                legacy.CommandText = """
                    CREATE TABLE worker_requests (
                        operation_id TEXT PRIMARY KEY,
                        instance_id TEXT NOT NULL,
                        fingerprint BLOB NOT NULL,
                        attempt INTEGER NOT NULL DEFAULT 0,
                        next_attempt_utc TEXT NULL
                    );
                    PRAGMA user_version=1;
                    INSERT INTO worker_requests (operation_id, instance_id, fingerprint)
                    VALUES ($operation, $instance, $fingerprint);
                    """;
                legacy.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                legacy.Parameters.AddWithValue("$instance", instance.ToString());
                legacy.Parameters.AddWithValue("$fingerprint", fingerprint);
                await legacy.ExecuteNonQueryAsync();
            }

            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseWorkerRequestRecord? restored = await catalog.ReadWorkerRequestAsync(operationId);
            Assert.IsNotNull(restored);
            CollectionAssert.AreEqual(fingerprint, restored.Fingerprint);
            await catalog.SaveInstanceRuntimeStateAsync(new(instance, "starting", true, null));
            Assert.IsNotNull(await catalog.ReadInstanceRuntimeStateAsync(instance));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task VersionFiveRuntimeUpgradesWithoutLosingPhaseAndPersistsRecentError()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!);
        InstanceId instanceId = InstanceId.New();
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.ProductCatalogDatabasePath,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand legacy = connection.CreateCommand();
                legacy.CommandText = """
                    CREATE TABLE instance_runtime (
                        instance_id TEXT PRIMARY KEY,
                        phase TEXT NOT NULL,
                        requires_full_rescan INTEGER NOT NULL,
                        last_successful_sync_utc TEXT NULL
                    );
                    INSERT INTO instance_runtime (instance_id, phase, requires_full_rescan)
                    VALUES ($instance, 'recovering', 1);
                    PRAGMA user_version=5;
                    """;
                legacy.Parameters.AddWithValue("$instance", instanceId.ToString());
                await legacy.ExecuteNonQueryAsync();
            }

            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseInstanceRuntimeState? restored = await catalog.ReadInstanceRuntimeStateAsync(instanceId);
                Assert.AreEqual("recovering", restored?.Phase);
                Assert.IsNull(restored?.LastErrorCode);
                await catalog.SaveInstanceRuntimeStateAsync(new(instanceId, "Worker error", true,
                    null, "NetworkUnavailable"));
                await catalog.SaveInstanceRuntimeStateAsync(new(instanceId, "Connected", false, null));
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseInstanceRuntimeState? updated = await reopened.ReadInstanceRuntimeStateAsync(instanceId);
            Assert.AreEqual("Connected", updated?.Phase);
            Assert.AreEqual("NetworkUnavailable", updated?.LastErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransferProgressSurvivesCatalogRestartAndDoesNotClearOnPhaseUpdates()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        InstanceId instanceId = InstanceId.New();
        try
        {
            var progress = new MirrorPulseTransferProgress("upload", 128, 512, DateTimeOffset.UtcNow);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SaveInstanceRuntimeStateAsync(new(instanceId, "Transferring", false,
                    null, null, progress));
                await catalog.SaveInstanceRuntimeStateAsync(new(instanceId, "Connected", false, null));
                MirrorPulseInstanceRuntimeState? current = await catalog.ReadInstanceRuntimeStateAsync(instanceId);
                Assert.AreEqual("Connected", current?.Phase);
                Assert.AreEqual(progress.Operation, current?.TransferProgress?.Operation);
                Assert.AreEqual(progress.BytesTransferred, current?.TransferProgress?.BytesTransferred);
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseInstanceRuntimeState? restored = await reopened.ReadInstanceRuntimeStateAsync(instanceId);
            Assert.AreEqual("upload", restored?.TransferProgress?.Operation);
            Assert.AreEqual(512, restored?.TransferProgress?.TotalBytes);
            IReadOnlyList<MirrorPulseInstanceRuntimeState> snapshot =
                await MirrorPulseProductCatalog.ReadRuntimeSnapshotAsync(paths);
            Assert.HasCount(1, snapshot);
            Assert.AreEqual("upload", snapshot[0].TransferProgress?.Operation);
            Assert.AreEqual(128, snapshot[0].TransferProgress?.BytesTransferred);
            Assert.AreEqual(512, snapshot[0].TransferProgress?.TotalBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductMetadataReopensSeparatelyFromOfficialCfSharpState()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        var operationId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var instance = InstanceId.New();
        var runtime = new MirrorPulseInstanceRuntimeState(instance, "recovering", true,
            DateTimeOffset.UtcNow, "NetworkUnavailable");
        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("stable Worker request"));
        try
        {
            var cfSharpFactory = new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath);
            await using (ICloudStateStore cfSharp = await cfSharpFactory.OpenAsync(
                new CloudStateStoreContext(paths.SyncRootPath)))
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
                Assert.IsTrue(await catalog.TryRecordWorkerRequestAsync(operationId, instance, fingerprint));
                Assert.IsFalse(await catalog.TryRecordWorkerRequestAsync(operationId, instance, fingerprint));
                await catalog.SaveUserCommandAsync(new(commandId, "keep-both", "conflict-1", "pending"));
                await catalog.SaveInstanceRuntimeStateAsync(runtime);
                IReadOnlyList<MirrorPulseInstanceRuntimeState> liveRuntime = await MirrorPulseProductCatalog
                    .ReadRuntimeSnapshotAsync(paths);
                Assert.HasCount(1, liveRuntime);
                Assert.AreEqual(runtime, liveRuntime[0]);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    catalog.TryRecordWorkerRequestAsync(
                        operationId,
                        instance,
                        SHA256.HashData(Encoding.UTF8.GetBytes("different request"))));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    catalog.SaveUserCommandAsync(new(commandId, "delete", "conflict-1", "pending")));

                await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseWorkerRequestRecord? worker = await reopened.ReadWorkerRequestAsync(operationId);
                Assert.IsNotNull(worker);
                Assert.AreEqual(instance, worker.InstanceId);
                CollectionAssert.AreEqual(fingerprint, worker.Fingerprint);
                Assert.AreEqual(0, worker.Attempt);
                MirrorPulseUserCommandRecord? userCommand = await reopened.ReadUserCommandAsync(commandId);
                Assert.IsNotNull(userCommand);
                Assert.AreEqual("keep-both", userCommand.Action);
                Assert.AreEqual("pending", userCommand.State);
                MirrorPulseInstanceRuntimeState? restored = await reopened.ReadInstanceRuntimeStateAsync(instance);
                Assert.IsNotNull(restored);
                Assert.AreEqual(runtime, restored);
            }

            Assert.AreNotEqual(paths.ProductCatalogDatabasePath, paths.CfSharpStateDatabasePath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.CfSharpStateDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand tables = connection.CreateCommand();
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('worker_requests', 'user_commands');";
                Assert.AreEqual(0L, await tables.ExecuteScalarAsync());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
